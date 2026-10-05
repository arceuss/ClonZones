using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>Unity sorting slot (layer, order, render queue) for a HUD renderer.</summary>
    internal readonly struct Gh3HudLayerSlot
    {
        public readonly int SortingLayer, Order, Queue;
        public Gh3HudLayerSlot(int sortingLayer, int order, int queue) { SortingLayer = sortingLayer; Order = order; Queue = queue; }
    }

    /// <summary>
    /// One player's GH3 2D HUD: builds the career hierarchy, ports the native
    /// `UpdateScoreFastPerFrame` presentation rules onto Clone Hero's authoritative
    /// state, replays the HUD scripts, and draws everything as one mesh. Clone Hero keeps
    /// its own visuals alive but hidden (see <see cref="Gh3HudVisibility"/>); any failure
    /// restores them.
    /// </summary>
    internal sealed partial class Gh3HudController : IDisposable
    {
        private static readonly Dictionary<IntPtr, Gh3HudController> Controllers = new();
        private static MelonLogger.Instance _log;
        private static string _assetRoot;
        private static long _assetGeneration;
        private static bool _active;
        private static bool _warned;
        private static bool _enabled = true;

        /// <summary>
        /// Supplies the selected theme root. A bank is still created per attach;
        /// this method stores no decoded textures or regions.
        /// </summary>
        public static void Configure(string assetRoot, MelonLogger.Instance log)
        {
            _assetRoot = assetRoot;
            _log = log;
        }

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            _log = log;
            var category = MelonPreferences.CreateCategory("ClonZonesHud");
            Gh3HudDiagnostics.Configure();
            _enabled = category.CreateEntry("Enabled", true, description: "Draw the GH3 scorebox, counter, rock meter and notifications instead of Clone Hero's HUD.").Value;
            harmony.Patch(AccessTools.Method(typeof(BeatRenderer), nameof(BeatRenderer.Start)),
                postfix: new HarmonyMethod(typeof(Gh3HudController), nameof(Started)));
            harmony.Patch(AccessTools.Method(typeof(BeatRenderer), nameof(BeatRenderer.OnDisable)),
                prefix: new HarmonyMethod(typeof(Gh3HudController), nameof(Disabled)));
        }

        public static void SetActive(bool active) { _active = active; }

        private static void Started(BeatRenderer __instance)
        {
            if (!_enabled || Controllers.ContainsKey(__instance.Pointer)) return;
            BasePlayer player = __instance.field_Private_BasePlayer_0;
            if (player == null || player.neckController == null || player.neckController.TryCast<GuitarNeckController>() == null) return;
            if (player.mainCamera == null || player.gameManager == null) return;
            if (player.gameManager.actualPlayerCount != 1)
            {
                _log?.Warning("[ClonZones] GH3 HUD is the sourced single-player career layout; vanilla HUD retained for multiplayer.");
                return;
            }
            if (player.engine == null)
            {
                _log?.Warning("[ClonZones] player engine not created yet at BeatRenderer.Start; vanilla HUD retained.");
                return;
            }
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) { _log?.Warning("[ClonZones] Sprites/Default unavailable; vanilla HUD retained."); return; }

            // Presentation is sampled once per attach. FlameStyle remains owned by
            // the flame lane and is intentionally not consulted here.
            PresentationSettings settings = PresentationSettings.Read(_assetRoot,
                message => _log?.Warning("[ClonZones] " + message));
            PresentationStyle style = settings.HudStyle;
            IReadOnlyList<string> imageNames = style == PresentationStyle.Wormod
                ? WormodHudLayout.ImageNames
                : Gh3HudAssets.ImageNames;
            Shader additiveShader = style == PresentationStyle.Wormod
                ? Shader.Find("Legacy Shaders/Particles/Additive")
                : null;
            if (style == PresentationStyle.Wormod && additiveShader == null)
            {
                _log?.Warning("[ClonZones] WORMod HUD additive shader unavailable; vanilla HUD retained.");
                return;
            }

            // Phase-B banks are fresh per attach. A missing optional image or
            // shader never disables the Clone Hero HUD before the controller owns it.
            Gh3HudAssets assets = new Gh3HudAssets(
                style, _assetRoot, ++_assetGeneration,
                imageNames, Gh3HudAssets.FontNames, _log);
            if (!assets.IsComplete)
            {
                if (!_warned)
                {
                    _warned = true;
                    _log?.Warning($"[ClonZones] {style} HUD assets incomplete ({assets.MissingSummary}); vanilla HUD retained.");
                }
                assets.Dispose();
                return;
            }

            try
            {
                Controllers.Add(__instance.Pointer, new Gh3HudController(player, shader, assets, additiveShader,
                    Gh3HighwayRenderer.UnderSidesSlot(__instance)));
            }
            catch (Exception error)
            {
                assets.Dispose();
                _log?.Error($"[ClonZones] {style} HUD attach failed; vanilla HUD retained: {error}");
            }
        }

        private static void Disabled(BeatRenderer __instance)
        {
            if (Controllers.Remove(__instance.Pointer, out Gh3HudController hud)) hud.Dispose();
        }

        public static void Tick()
        {
            if (!_active) return;
            foreach (Gh3HudController hud in Controllers.Values)
            {
                try { hud.Update(); }
                catch (Exception error)
                {
                    hud.Dispose();
                    if (!_warned) { _warned = true; _log.Error($"[ClonZones] GH3 HUD stopped; vanilla HUD restored: {error}"); }
                }
            }
        }

        public static void Clear()
        {
            _active = false;
            foreach (Gh3HudController hud in Controllers.Values) hud.Dispose();
            Controllers.Clear();
            Gh3HudBreakHook.Clear();
        }

        // ── instance ────────────────────────────────────────────────────────────

        private readonly BasePlayer _player;
        private readonly PresentationStyle _hudStyle;
        private readonly Gh3HudStateBridge _bridge;
        private readonly Gh3HudAssets _assets;
        private readonly Gh3HudScene _scene;
        private readonly Gh3HudScheduler _sched = new();
        private Gh3HudMesh _mesh;
        // HUD quads below Gh3HudLayout.HighwaySideZ, drawn in the slot just under the GH3 sides.
        // GH3 sorts HUD and highway elements together by z; Unity can only interleave whole renderers.
        private Gh3HudMesh _meshUnderSides;
        private Gh3HudVisibility _visibility;
        private readonly System.Random _random = new(0x4A3F);   // bulb pulse Random(@0.1 @*2 0.5): seeded per attach for reproducible captures
        private Gh3HudSnapshot _snap, _prev;
        private bool _disposed;
        private bool _drawn;
        private bool _changed = true;
        private int _resetDiscoveryFrames;
        private bool _synchronizeState = true;
        private double _clockRemainder;
        private double _lastSongTime = double.NegativeInfinity;
        private int _epoch;

        // Career hierarchy (create_2d_hud_elements) and notification hierarchy (setup_hud).
        private Gh3HudElement _root2d, _rockContainer, _scoreContainer, _noteContainer, _hudDestroyGroup;
        private Gh3HudElement _scoreText, _icon, _scoreFlash, _rockGlow, _spReadyText;
        private readonly Gh3HudElement[] _digits = new Gh3HudElement[4];      // dial 1 (ones) .. 4 (thousands)
        private readonly Vector2[] _digitInitialPos = new Vector2[4];
        private readonly Gh3HudElement[] _nixieA = new Gh3HudElement[6], _nixieB = new Gh3HudElement[6]; // table {1,2,3,4,6,8}
        private static readonly int[] NixieTable = { 1, 2, 3, 4, 6, 8 };
        private readonly Gh3HudElement[] _unlit = new Gh3HudElement[5], _half = new Gh3HudElement[5], _full = new Gh3HudElement[5];
        private Gh3HudElement _needle, _bgGreen, _bgYellow, _bgRed, _bgNoFail, _lightsGreen, _lightsYellow, _lightsRed;
        private readonly Gh3HudElement[] _tube = new Gh3HudElement[6], _tubeFill = new Gh3HudElement[6], _tubeFull = new Gh3HudElement[6];
        private readonly Vector2[] _tubeFinal = new Vector2[6], _tubeInitial = new Vector2[6];
        private readonly Vector2[] _fillFinal = new Vector2[6], _fillInitial = new Vector2[6];
        private readonly bool[] _tubeMorph = new bool[6];
        private readonly float[] _fillOldAlpha = new float[6], _fullOldAlpha = new float[6];

        // Native updater caches (g_NoteStreak / g_scoreMultiplier / g_Score / g_StarPower / g_StarPower2_).
        private int _gNoteStreak = -1, _gScoreMultiplier = -1, _gScore = -1;
        private float _gStarPower = -1f, _gStarPowerPrev, _gHealth = -2f;
        private bool _gNoFail;
        private bool _counterVisible;
        private bool _gMultiplierStarPower;

        // Script globals.
        private bool _starPowerReadyOn;     // star_power_ready_on_p1
        private bool _starPowerUsed;        // player_status.star_power_used
        private bool _flashRedGoing;        // g_flash_red_going_p1
        private bool _entranceStarted, _entranceFinished;
        private double _entranceStartTime;

        private const int PlayerNumber = 1;
        private const string PlayerText = "p1";
        private const string PulseScriptId = "player_spawned_scriptid_p1";
        private const string StreakContainerId = "HUD_Note_Streak_Combo1";


        private Gh3HudController(BasePlayer player, Shader shader, Gh3HudAssets assets, Shader additiveShader, Gh3HudLayerSlot? underSides)
        {
            _player = player;
            _bridge = new Gh3HudStateBridge(player);
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _hudStyle = _assets.Style;
            _scene = new Gh3HudScene(_assets.Region);
            try
            {
                Camera camera = player.mainCamera;
                // GH3 draws frets and hit flames over the HUD containers, and the HUD over the field.
                // CH sorts by layer value first, so sit on the highest layer below "Frets"
                // (Sustains, 6): above Highway/HighwayOverlay/Beat Lines, below Frets/Notes/Flames.
                int sortingLayer = SortingLayer.NameToID("Default");
                int fretValue = int.MaxValue, highestValue = int.MinValue;
                foreach (int layer in SortingLayer.GetSortingLayerIDsInternal())
                    if (SortingLayer.IDToName(layer) == "Frets") fretValue = SortingLayer.GetLayerValueFromID(layer);
                foreach (int layer in SortingLayer.GetSortingLayerIDsInternal())
                {
                    int value = SortingLayer.GetLayerValueFromID(layer);
                    if (value >= fretValue || value <= highestValue) continue;
                    highestValue = value;
                    sortingLayer = layer;
                }
                _mesh = new Gh3HudMesh("clonzones_gh3_hud", _assets.Atlas, shader, camera, camera.gameObject.layer,
                    sortingLayer, 30000, 4000, 256, additiveShader);
                if (underSides is Gh3HudLayerSlot slot)
                    _meshUnderSides = new Gh3HudMesh("clonzones_gh3_hud_under_sides", _assets.Atlas, shader, camera, camera.gameObject.layer,
                        slot.SortingLayer, slot.Order, slot.Queue, 64, additiveShader);
                BuildScene();
                _visibility = new Gh3HudVisibility(player, _log);
                if (!_bridge.Read(ref _snap, _hudStyle == PresentationStyle.Wormod))
                {
                    string reason = _hudStyle == PresentationStyle.Wormod
                        ? Gh3HudStateBridge.PresentationBindingError
                        : null;
                    throw new InvalidOperationException(reason ?? "engine state unreadable at attach");
                }
                _prev = _snap;
                _lastSongTime = _snap.SongTime;
                if (_hudStyle == PresentationStyle.Wormod)
                {
                    UpdateWormodMeterHealth();
                    UpdateWormodRockMeter(_wormodMeterHealth);
                    UpdateWormodStarMeter();
                }
                _log?.Msg($"[ClonZones] {_hudStyle} HUD attached: {_scene.Count} elements, viewport {camera.pixelRect.width}x{camera.pixelRect.height}, songTime={_snap.SongTime:F3}.");
            }
            catch
            {
                ReleaseOwnedResources();
                throw;
            }
        }

        private void ReleaseOwnedResources()
        {
            // The mesh must stop referencing the atlas before the bank destroys it.
            if (_mesh != null)
            {
                _mesh.Dispose();
                _mesh = null;
            }
            if (_meshUnderSides != null)
            {
                _meshUnderSides.Dispose();
                _meshUnderSides = null;
            }
            if (_visibility != null)
            {
                _visibility.Restore();
                _visibility = null;
            }
            _assets?.Dispose();
        }

        // ── scene construction (guitar_hud.q:setup_hud + guitar_hud_2d.q:create_2d_hud_elements) ──

        private void BuildScene()
        {
            Gh3HudElement hudWindow = _scene.CreateContainer("hud_window", null, Vector2.zero);
            _hudDestroyGroup = _scene.CreateContainer("hud_destroygroup_windowp1", hudWindow, Vector2.zero);
            if (_hudStyle == PresentationStyle.Wormod)
                BuildWormodNotifications(_hudDestroyGroup);
            else
            {
                // hud_screen_elements[0]: pre-created Star Power Ready text; reset_hud_text leaves it at alpha 0.
                _spReadyText = _scene.CreateText("star_power_ready_textp1", _hudDestroyGroup, _assets.Font("text_a6"), "Star Power Ready",
                    Gh3HudLayout.AlertBasePos, Gh3HudLayout.JustCenterTop, 80f, 0f, new Color32(210, 210, 210, 250), Vector2.one);
                _spReadyText.Shadow = true; _spReadyText.ShadowOffset = new Vector2(2f, 2f); _spReadyText.ShadowRgba = new Color32(0, 0, 0, 255);
            }

            bool wormod = _hudStyle == PresentationStyle.Wormod;
            float rootScale = wormod ? WormodHudLayout.Scale : Gh3HudLayout.Scale;
            float smallBulbScale = wormod ? WormodHudLayout.SmallBulbScale : Gh3HudLayout.SmallBulbScale;
            float bigBulbScale = wormod ? WormodHudLayout.BigBulbScale : Gh3HudLayout.BigBulbScale;
            Gh3HudLayout.Decl[] declarations = wormod ? WormodHudLayout.Career : Gh3HudLayout.Career;
            _root2d = _scene.CreateContainer("HUD_2D_Containerp1", null, Vector2.zero, 0f, rootScale);
            var byId = new Dictionary<string, Gh3HudElement>(StringComparer.Ordinal);
            foreach (Gh3HudLayout.Decl d in declarations)
            {
                Gh3HudElement parent = d.Parent != null ? byId[d.Parent] : _root2d;
                if (d.Kind == Gh3HudLayout.Kind.Container)
                {
                    Vector2 pos = d.PosType != null
                        ? (wormod ? WormodHudLayout.PosTypeValue(d.PosType) : Gh3HudLayout.PosTypeValue(d.PosType))
                        : Vector2.zero;
                    if (d.NoteStreakBar) pos += wormod ? WormodHudLayout.OffscreenNoteStreakBarOff : Gh3HudLayout.OffscreenNoteStreakBarOff;
                    pos += d.PosOff;
                    byId[d.Id] = _scene.CreateContainer(d.Id + PlayerText, parent, pos, d.Rot);
                    continue;
                }
                float bulbScale = d.SmallBulb ? smallBulbScale : bigBulbScale;
                Vector2? dims = d.Dims ?? (d.ElementDims.HasValue ? d.ElementDims.Value * bulbScale : null);
                Vector2 spritePos = d.Bulb ? d.PosOff : (d.InitialPos ?? d.PosOff);
                Gh3HudElement sprite = _scene.CreateSprite(d.Id + PlayerText, parent, d.Texture, spritePos, d.Just, d.Z, d.Alpha,
                    d.Rgba ?? new Color32(255, 255, 255, 255), d.Rot, dims);
                sprite.Blend = d.Blend;
                // Source `scale` is a post-create logical-dimension write, not a
                // hierarchy scale. Keep the parent transform at the authored scale.
                if (d.SpriteScale != 1f) sprite.Dims *= d.SpriteScale;
                byId[d.Id] = sprite;
                if (!d.Bulb) continue;
                int bulb = int.Parse(d.Id.Substring(d.Id.Length - 1)) - 1;
                _tube[bulb] = sprite;
                _tubeMorph[bulb] = d.InitialPos.HasValue;
                _tubeFinal[bulb] = d.PosOff; _tubeInitial[bulb] = d.InitialPos ?? d.PosOff;
                if (d.InitialPos.HasValue) sprite.SetPos(d.InitialPos.Value);
                // tube/full children hang off the bulb container, not the tube sprite (builder: parent = element_parent).
                Vector2 fillPos = d.PosOff + d.TubePosOff;
                // no bulb scale here. the builder's dims (element_dims * bulb scale) get folded into a scale and the
                // base reset to the texture size (CSpriteElement::SetProperties 0x4FB580), then UpdateSPMeter overwrites
                // that scale with an absolute (0.8 * small_bulb_scale, 3f) (0x4230F0, SetScale relative=false). so every
                // fill draws its 64x16 art at that scale. scaling here shrank small bulbs twice and left WORMod's six
                // fills only touching instead of overlapping into one glow.
                _tubeFill[bulb] = _scene.CreateSprite(d.Id + PlayerText + "tube", parent, d.TubeTexture, fillPos, Gh3HudLayout.JustCenterBottom,
                    d.TubeZ, d.TubeAlpha, new Color32(255, 255, 255, 255), 0f, d.TubeDims);
                _tubeFill[bulb].Blend = Gh3HudBlend.Alpha;
                _fillFinal[bulb] = fillPos; _fillInitial[bulb] = (d.InitialPos ?? d.PosOff) + d.TubePosOff;
                if (d.InitialPos.HasValue) _tubeFill[bulb].SetPos(_fillInitial[bulb]);
                _tubeFull[bulb] = _scene.CreateSprite(d.Id + PlayerText + "full", parent, d.FullTexture, d.PosOff, d.Just,
                    d.FullZ, d.FullAlpha, new Color32(255, 255, 255, 255), 0f, d.ElementDims.Value * bulbScale);
                _tubeFull[bulb].Blend = Gh3HudBlend.Alpha;
                if (d.InitialPos.HasValue) _tubeFull[bulb].SetPos(d.InitialPos.Value);
                _fillOldAlpha[bulb] = d.TubeAlpha; _fullOldAlpha[bulb] = d.FullAlpha;
            }
            _rockContainer = byId["HUD2D_rock_container"];
            _scoreContainer = byId["HUD2D_score_container"];
            _noteContainer = byId["HUD2D_note_container"];
            _icon = byId["HUD2D_counter_drum_icon"];
            _scoreFlash = byId["HUD2D_score_flash"];
            _rockGlow = byId["HUD2D_rock_glow"];
            _needle = byId["HUD2D_rock_needle"];
            _bgGreen = byId["HUD2D_rock_BG_green"]; _bgYellow = byId["HUD2D_rock_BG_yellow"]; _bgRed = byId["HUD2D_rock_BG_red"];
            if (!wormod) _bgNoFail = byId["HUD2D_rock_BG_nofail"];
            _lightsGreen = byId["HUD2D_rock_lights_green"]; _lightsYellow = byId["HUD2D_rock_lights_yellow"]; _lightsRed = byId["HUD2D_rock_lights_red"];
            for (int i = 0; i < 5; i++)
            {
                _unlit[i] = byId[$"HUD2D_score_light_unlit_{i + 1}"];
                _half[i] = byId[$"HUD2D_score_light_halflit_{i + 1}"];
                _full[i] = byId[$"HUD2D_score_light_allwaylit_{i + 1}"];
            }
            for (int i = 0; i < 6; i++)
            {
                int m = NixieTable[i];
                byId.TryGetValue($"HUD2D_score_nixie_{m}a", out _nixieA[i]);
                byId.TryGetValue($"HUD2D_score_nixie_{m}b", out _nixieB[i]);
            }
            if (wormod) BindWormodElements(byId);

            Vector2 scoreTextPos = wormod ? WormodHudLayout.ScoreTextPos : Gh3HudLayout.ScoreTextPos;
            float scoreTextScale = wormod ? WormodHudLayout.ScoreTextScale : Gh3HudLayout.ScoreTextScale;
            _scoreText = _scene.CreateText("HUD2D_Score_Textp1", _scoreContainer, _assets.Font("num_a9"), "",
                scoreTextPos, Gh3HudLayout.JustRightRight, wormod ? WormodHudLayout.ScoreTextZ : Gh3HudLayout.ScoreTextZ,
                1f, new Color32(255, 255, 255, 255), new Vector2(scoreTextScale, scoreTextScale));
            _scoreText.Shadow = true; _scoreText.ShadowOffset = wormod ? WormodHudLayout.DisplayTextShadowOffset : Gh3HudLayout.DisplayTextShadowOffset;
            _scoreText.ShadowRgba = new Color32(0, 0, 0, 255);
            _scoreText.FontSpacing = wormod ? WormodHudLayout.ScoreFontSpacing : Gh3HudLayout.ScoreFontSpacing;
            Vector2 digitBase = wormod ? WormodHudLayout.CounterDigitBase : Gh3HudLayout.CounterDigitBase;
            Vector2 digitStep = wormod ? WormodHudLayout.CounterDigitStep : Gh3HudLayout.CounterDigitStep;
            for (int i = 1; i <= 4; i++)
            {
                Vector2 pos = digitBase + digitStep * i;
                Color32 rgba = wormod
                    ? WormodHudLayout.CounterDigitRgba
                    : (i == 1 ? Gh3HudLayout.CounterDigit1Rgba : Gh3HudLayout.CounterDigitRgba);
                _digits[i - 1] = _scene.CreateText($"HUD2D_Note_Streak_Text_{i}p1", _noteContainer, _assets.Font("num_a7"), "0",
                    pos, Gh3HudLayout.JustCenterCenter, wormod ? WormodHudLayout.CounterDigitZ : Gh3HudLayout.CounterDigitZ,
                    1f, rgba, Vector2.one);
                _digitInitialPos[i - 1] = pos;
            }
        }

        // ── per-frame ───────────────────────────────────────────────────────────

        private void Update()
        {
            if (_disposed) return;
            bool engineChanged = _bridge.EngineChanged;
            if (!_bridge.Read(ref _snap, _hudStyle == PresentationStyle.Wormod))
            {
                if (_hudStyle == PresentationStyle.Wormod)
                    throw new InvalidOperationException(
                        Gh3HudStateBridge.PresentationBindingError ?? "WORMod presentation state became unreadable");
                return;
            }

            // A backwards clock, a score drop, or a new practice section means
            // CH reset/seeked the song: nothing pending survives that epoch.
            bool practiceEpochChanged = _hudStyle == PresentationStyle.Wormod &&
                (_snap.IsPractice != _prev.IsPractice ||
                 (_snap.IsPractice && PracticeRangeChanged(_prev, _snap)));
            bool newAttempt = engineChanged || practiceEpochChanged || _snap.Score < _prev.Score;
            if (newAttempt || _snap.SongTime < _lastSongTime - 1e-6)
            {
                ResetPresentation(newAttempt);
                _changed = true;
            }
            _lastSongTime = _snap.SongTime;
            if (_hudStyle == PresentationStyle.Wormod) UpdateWormodMeterHealth();
            ClonZonesBenchmark.RecordSongTime(_snap.SongTime);

            if (!_snap.Paused)
            {
                double ms = UnityIcalls.UnscaledDeltaTime * 1000.0 + _clockRemainder;
                int whole = (int)ms;
                _clockRemainder = ms - whole;
                // A script can exit with an async morph still running. The clock is not a queue clock.
                _sched.AdvanceClock(whole);
                // Finish last frame's movement before a resumed script snapshots the next morph.
                if (_scene.Tick(_sched.NowMs)) _changed = true;
                RunEntrance();
                RunNativeUpdater();
                if (_hudStyle == PresentationStyle.Wormod)
                {
                    // Active loop order from gem_scroller: native score/rock
                    // update, then the custom WOR needle, then update_star_meter.
                    UpdateWormodRockMeter(_wormodMeterHealth);
                    UpdateWormodDullerEdges();
                    UpdateWormodStarMeter();
                }
                if (_hudStyle == PresentationStyle.Gh3) RunFcLabel();
                RunTransitions();
                if (_sched.Count > 0) _changed = true;
                _sched.Tick(0);
                if (_scene.Tick(_sched.NowMs)) _changed = true;
            }
            else if (_hudStyle == PresentationStyle.Wormod)
            {
                // A reset caught while paused rebuilds the scene with the layout's full-width
                // meter and nothing else would correct it before play resumes. Mirroring the
                // native counter touches no clock, script or morph state.
                UpdateWormodStarMeter();
            }

            if (_mesh.RefreshViewport()) _changed = true;
            if (_meshUnderSides != null && _meshUnderSides.RefreshViewport()) _changed = true;
            // Everything is retained in the element tree; rebuild the mesh only when something moved.
            if (_changed || !_drawn)
            {
                _changed = false;
                ClonZonesBenchmark.Mark(BenchmarkEvent.HudRebuild);
                _mesh.Begin();
                _meshUnderSides?.Begin();
                long drawStart = ClonZonesProfiler.BeginScope(ProfileScope.HudDraw);
                _scene.Draw((IGh3HudQuadSink)_meshUnderSides ?? _mesh, _mesh);
                ClonZonesProfiler.EndScope(ProfileScope.HudDraw, drawStart);
                long uploadStart = ClonZonesProfiler.BeginScope(ProfileScope.HudUpload);
                _meshUnderSides?.Upload();
                _mesh.Upload();
                ClonZonesProfiler.EndScope(ProfileScope.HudUpload, uploadStart);
            }
            Gh3HudDiagnostics.Snapshot(_snap, _mesh, _sched.Count, _bridge, _log);
            Gh3HudDiagnostics.RendererInventory(_log);
            long visibilityStart = ClonZonesProfiler.BeginScope(ProfileScope.HudVisibility);
            if (!_drawn)
            {
                _drawn = true;
                _visibility.Hide();   // first complete frame is out: now the CH leaves may go
            }
            else
            {
                // CH's right-click reset can replace leaves without changing the owning component.
                // Re-discover on the click and release plus the following frame, including while paused.
                if (UnityIcalls.MouseButtonDown(1) || UnityIcalls.MouseButtonUp(1))
                    _resetDiscoveryFrames = 2;
                _visibility.Reassert(_resetDiscoveryFrames > 0);
                if (_resetDiscoveryFrames > 0) _resetDiscoveryFrames--;
            }
            ClonZonesProfiler.EndScope(ProfileScope.HudVisibility, visibilityStart);
            _prev = _snap;
        }

        // newAttempt: engine swap, practice range change or score reset. A plain backwards clock
        // (CH's resume rewind) rebuilds the presentation too, but it is the same attempt.
        private void ResetPresentation(bool newAttempt)
        {
            _epoch++;
            _sched.Reset();
            // WORMod's duller lasts until its HUD is rebuilt, which a pause never does; carry it
            // across our rebuild unless the attempt itself restarted. BindWormodElements re-applies it.
            if (newAttempt) { _wormodDullerActive = false; _wormodMeterHealthValid = false; }
            // A new clock epoch needs new element timers too. Partial property resets leave
            // positive old start times behind a clock that has just gone back to zero.
            _scene.Clear();
            BuildScene();
            _clockRemainder = 0;
            _counterVisible = false;
            _starPowerReadyOn = false;
            _starPowerUsed = false;
            _flashRedGoing = false;
            _gNoteStreak = _gScoreMultiplier = _gScore = -1;
            _gMultiplierStarPower = false;
            _gStarPower = -1f; _gStarPowerPrev = 0f; _gHealth = -2f; _gNoFail = false;
            _entranceStarted = _entranceFinished = false;
            _fcState = FcState.Waiting; _fcText = null; _fcGlow = null;
            _synchronizeState = true;
            _prev = _snap;
        }
        // ── entrance: guitar_intro.q hud_start_time (-400/-1400 ms), hud_move_time 200 ms ──

        private void RunEntrance()
        {
            if (_entranceFinished) return;
            _changed = true;
            bool practice = _hudStyle == PresentationStyle.Wormod && _snap.IsPractice;
            double songTime = practice && IsFinite(_snap.PracticeStartTime)
                ? _snap.SongTime - _snap.PracticeStartTime
                : _snap.SongTime;
            double start = practice ? -1.4 : -0.4;
            const double move = 0.2;
            if (!_entranceStarted)
            {
                if (songTime < start) return;
                _entranceStarted = true;
                _entranceStartTime = start;
                if (songTime > start + move)
                {
                    // Attached mid-song: settle instantly, no intro bounce.
                    Morph2dHudElements(1f, Vector2.zero, 0f, 0f);
                    _entranceFinished = true;
                    return;
                }
            }
            float delta = (float)Math.Min(1.0, (songTime - _entranceStartTime) / move);
            Morph2dHudElements(delta, Vector2.zero, 0f, 0f);
            if (delta >= 1f)
            {
                _entranceFinished = true;
                _sched.Spawn("move_2d_elements_to_default", EntranceBounce());
            }
        }

        /// <summary>guitar_hud.q:morph_2d_hud_elements for 1P (rock_pos/score_pos, off_set_drop only in faceoff).</summary>
        private void Morph2dHudElements(float delta, Vector2 offSet, float time, float rot)
        {
            Vector2 rock = (1f - delta) * (_hudStyle == PresentationStyle.Wormod ? WormodHudLayout.OffscreenRockPos : Gh3HudLayout.OffscreenRockPos) +
                delta * ((_hudStyle == PresentationStyle.Wormod ? WormodHudLayout.RockPos : Gh3HudLayout.RockPos) - offSet);
            Vector2 score = (1f - delta) * (_hudStyle == PresentationStyle.Wormod ? WormodHudLayout.OffscreenScorePos : Gh3HudLayout.OffscreenScorePos) +
                delta * ((_hudStyle == PresentationStyle.Wormod ? WormodHudLayout.ScorePos : Gh3HudLayout.ScorePos) + offSet);
            // active practice uses the native morph_2d_hud_elements exclusion:
            // the rock stays offscreen while score/counter still enter.
            if (!(_hudStyle == PresentationStyle.Wormod && _snap.IsPractice))
                _rockContainer.Morph(Gh3Morph.Of(time).WithPos(rock).WithRot(rot), _sched.NowMs);
            _scoreContainer.Morph(Gh3Morph.Of(time).WithPos(score), _sched.NowMs);
        }

        private IEnumerator<Gh3Wait> EntranceBounce()
        {
            Morph2dHudElements(1f, new Vector2(50f, 0f), 0.1f, -5f);
            yield return Gh3Wait.Seconds(0.1f);
            Morph2dHudElements(1f, new Vector2(-25f, 0f), 0.125f, 5f);
            yield return Gh3Wait.Seconds(0.125f);
            Morph2dHudElements(1f, Vector2.zero, 0.1f, 0f);
        }

        // ── native UpdateScoreFastPerFrame port ─────────────────────────────────

        private readonly char[] _scoreBuffer = new char[16];

        private void RunNativeUpdater()
        {
            // Score text: plain %d, rescaled past 5 characters (0x42F6D0 score branch).
            if (_snap.Score != _gScore)
            {
                _changed = true;
                _gScore = _snap.Score;
                int len = FormatInt(_snap.Score, _scoreBuffer);
                _scoreText.TextLength = len;
                Array.Copy(_scoreBuffer, _scoreText.Text, len);
                float width = _scoreText.Font.Measure(_scoreText.Text, len, _scoreText.FontSpacing).x;
                Gh3HudRules.ScoreScale(len, width, out float sx, out float sy);
                _scoreText.SetScale(new Vector2(sx, sy));
            }

            // Streak driver (0x42FFFC): the multiplier art is re-evaluated only on a streak change
            // or after an UpdateNixie invalidation (end of the activation flash, SP end), exactly
            // like the native driver. Hit-only work (counter, lamps, milestones, digits) needs a
            // real streak change: an invalidation must not manufacture another note event.
            int streak = _snap.Streak;
            int mult = _snap.Multiplier;
            bool spUsed = _snap.StarPowerActive;
            bool streakChanged = streak != _gNoteStreak;
            if (streakChanged || _gScoreMultiplier < 0)
            {
                if (mult != _gScoreMultiplier || spUsed != _gMultiplierStarPower)
                {
                    _changed = true;
                    _gScoreMultiplier = mult;
                    _gMultiplierStarPower = spUsed;
                    InstallLampPalette(mult, spUsed);
                }
                SelectNixie(mult, spUsed);
            }
            if (streakChanged)
            {
                _changed = true;
                _gNoteStreak = streak;
                // Counter show/hide/flip (sub_422BC0 tail). A seek/reattach hydrates
                // the present state; it is not another hit or another announcement.
                if (_synchronizeState)
                {
                    _counterVisible = streak >= Gh3HudRules.CounterShowStreak;
                    _noteContainer.SetPos(
                        (_hudStyle == PresentationStyle.Wormod ? WormodHudLayout.CounterPos : Gh3HudLayout.CounterPos) +
                        (_counterVisible
                            ? Vector2.zero
                            : (_hudStyle == PresentationStyle.Wormod ? WormodHudLayout.OffscreenNoteStreakBarOff : Gh3HudLayout.OffscreenNoteStreakBarOff)));
                }
                else if (streak < Gh3HudRules.CounterShowStreak)
                {
                    if (_counterVisible)
                    {
                        _counterVisible = false;
                        _sched.Kill("hud_move_note_scorebar");
                        _sched.Spawn("hud_move_note_scorebar", HudMoveNoteScorebar(false, 0.5f));
                    }
                }
                else if (!_counterVisible)
                {
                    _counterVisible = true;
                    _sched.Kill("hud_move_note_scorebar");
                    _sched.Spawn("hud_move_note_scorebar", HudMoveNoteScorebar(true, 0.5f));
                }
                else if (_hudStyle != PresentationStyle.Wormod)
                {
                    // WORMod's hud_flip_note_streak_num is an empty source override.
                    _sched.Spawn("hud_flip_note_streak_num", HudFlipNoteStreakNum(Gh3HudRules.FlipDial(streak)));
                }

                UpdateLamps(streak);

                // Milestones (0x422E8A): 50, then every 100.
                if (!_synchronizeState && Gh3HudRules.IsMilestone(streak))
                    _sched.Spawn("hud_show_note_streak_combo", HudShowNoteStreakCombo(streak));

                if (_counterVisible) UpdateDigits(streak);
            }

            // Six-bulb meter (UpdateSPMeter) on SP change.
            float sp = _snap.StarPower * 100f;
            if (sp != _gStarPower)
            {
                _changed = true;
                _gStarPower = sp;
                UpdateSpMeter(sp);
            }

            // Rock meter on health or effective no-fail mode change. The latter must
            // repaint even when health is constant so a mode toggle cannot leave
            // the previous presentation layered over the new one.
            bool noFailChanged = _hudStyle == PresentationStyle.Gh3 && _snap.NoFail != _gNoFail;
            float meterHealth = _hudStyle == PresentationStyle.Wormod ? _wormodMeterHealth : ChMeterHealth();
            if (meterHealth != _gHealth || noFailChanged)
            {
                _changed = true;
                _gHealth = meterHealth;
                if (_hudStyle == PresentationStyle.Gh3) _gNoFail = _snap.NoFail;
                UpdateRockMeter(meterHealth);
            }
        }

        // the health CH's own meter shows: bottomed out for good once the attempt fails, while the
        // engine's health keeps climbing (see Gh3HudStateBridge.ReadFailed). Under no-fail the HUD
        // follows GH3's no-fail rules instead (GH3 face, WORMod's never-draining meter).
        private float ChMeterHealth() => _snap.Failed && !_snap.NoFail ? 0f : _snap.Health;

        private static int FormatInt(int value, char[] buffer)
        {
            if (value == 0) { buffer[0] = '0'; return 1; }
            bool negative = value < 0;
            long v = negative ? -(long)value : value;
            int n = 0;
            while (v > 0) { buffer[n++] = (char)('0' + (int)(v % 10)); v /= 10; }
            if (negative) buffer[n++] = '-';
            Array.Reverse(buffer, 0, n);
            return n;
        }

        /// <summary>Palette install (mult-change path): SP -> blue; 1,2 base; 3 green; 4 purple; other -> keep.</summary>
        private void InstallLampPalette(int mult, bool spUsed)
        {
            string suffix = Gh3HudRules.LampPalette(mult, spUsed);
            if (suffix == null) return;
            for (int i = 0; i < 5; i++)
            {
                _scene.SetTexture(_unlit[i], "HUD_score_light_0" + suffix);
                _scene.SetTexture(_half[i], "HUD_score_light_1" + suffix);
                _scene.SetTexture(_full[i], "HUD_score_light_2" + suffix);
            }
        }

        /// <summary>Nixie select (0x430101): alpha 1 on the matching variant, a-side without SP, b-side with SP.</summary>
        private void SelectNixie(int mult, bool spUsed)
        {
            for (int i = 0; i < 6; i++)
            {
                bool match = NixieTable[i] == mult;
                if (_nixieA[i] != null) _nixieA[i].SetAlpha(match && !spUsed ? 1f : 0f);
                if (_nixieB[i] != null) _nixieB[i].SetAlpha(match && spUsed ? 1f : 0f);
            }
        }

        /// <summary>Lamp levels: v44 decade rule, trunc(v44/2) full lamps plus a half lamp on odd v44.</summary>
        private void UpdateLamps(int s)
        {
            Gh3HudRules.Lamps(s, out int full, out int half);
            for (int i = 0; i < 5; i++) { _half[i].SetAlpha(0f); _full[i].SetAlpha(0f); }
            if (half >= 0) _half[half].SetAlpha(1f);
            for (int i = 0; i < full && i < 5; i++) _full[i].SetAlpha(1f);
        }

        /// <summary>Four drums always written (thousands = full quotient); icon hidden once streak >= 1000.</summary>
        private void UpdateDigits(int s)
        {
            Gh3HudRules.Digits(s, out int ones, out int tens, out int hundreds, out int thousands);
            SetDigit(0, ones); SetDigit(1, tens); SetDigit(2, hundreds); SetDigit(3, thousands);
            _icon.SetAlpha(thousands == 0 ? 1f : 0f);
        }

        private void SetDigit(int dial, int value)
        {
            int len = FormatInt(value, _scoreBuffer);
            _digits[dial].TextLength = len;
            Array.Copy(_scoreBuffer, _digits[dial].Text, len);
        }

        /// <summary>
        /// Health branch: needle = (1-h)*(-84)+42 degrees, bands at 14 and 31.5 degrees
        /// (BG green ramps in over 14 degrees; yellow underlay ramps out below 0), red flash
        /// script at or below -31.5.
        /// </summary>
        private void UpdateRockMeter(float health)
        {
            if (_hudStyle == PresentationStyle.Gh3)
            {
                if (_snap.NoFail)
                {
                    HudFlashRedBgKill();
                    _bgNoFail.SetAlpha(1f);
                    _bgGreen.SetAlpha(0f); _bgYellow.SetAlpha(0f); _bgRed.SetAlpha(0f);
                    _needle.SetAlpha(0f);
                    _lightsGreen.SetAlpha(0f); _lightsYellow.SetAlpha(0f); _lightsRed.SetAlpha(0f);
                    return;
                }
                _bgNoFail.SetAlpha(0f);
                _needle.SetAlpha(1f);
                _bgRed.SetAlpha(1f);
            }

            float a = Gh3HudRules.NeedleAngle(health);
            _needle.SetRot(a);
            Gh3HudRules.RockLayers(a, out float bgGreen, out float bgYellow, out float lGreen, out float lYellow, out float lRed, out bool flash);
            _bgGreen.SetAlpha(bgGreen); _bgYellow.SetAlpha(bgYellow);
            _lightsGreen.SetAlpha(lGreen); _lightsYellow.SetAlpha(lYellow); _lightsRed.SetAlpha(lRed);
            if (flash && !_flashRedGoing) _sched.Spawn("hud_flash_red_bg_p1", HudFlashRedBg());
            else if (!flash && _flashRedGoing) HudFlashRedBgKill();
        }

        // ── CH transitions that GH3 scripts drive (readiness, activation, deactivation) ──

        private void RunTransitions()
        {
            if (_synchronizeState)
            {
                _synchronizeState = false;
                _starPowerUsed = _snap.StarPowerActive;
                if (_snap.StarPowerReady || _snap.StarPowerActive)
                {
                    for (int i = 0; i < 6; i++)
                    {
                        _scene.SetTexture(_tubeFill[i], "HUD_rock_tube_glow_fill_b");
                        _scene.SetTexture(_tubeFull[i], "HUD_rock_tube_glow_full_b");
                        // WORMod's rock_meter_star_power_on leaves initial positions (qb 0x8b8), as in RockMeterStarPowerOn.
                        if (_hudStyle == PresentationStyle.Gh3 && _tubeMorph[i])
                        {
                            _tube[i].SetPos(_tubeFinal[i]);
                            _tubeFill[i].SetPos(_fillFinal[i]);
                            _tubeFull[i].SetPos(_tubeFinal[i]);
                        }
                    }
                    if (_snap.StarPowerReady)
                        _sched.Spawn("pulsate_all_star_power_bulbs", PulsateAllStarPowerBulbs(), PulseScriptId);
                }
                return;
            }
            if (_snap.StarPowerReady != _prev.StarPowerReady || _snap.StarPowerActive != _prev.StarPowerActive) _changed = true;
            if (_snap.StarPowerReady && !_prev.StarPowerReady)
                _sched.Spawn("show_star_power_ready", ShowStarPowerReady());
            if (_snap.StarPowerActive && !_prev.StarPowerActive)
            {
                // star_power_activate_and_drain: star_power_used = 1, hud_activated_star_power.
                _starPowerUsed = true;
                _sched.Spawn("hud_activated_star_power_spawned", HudActivatedStarPowerSpawned(0.2f));
            }
            else if (!_snap.StarPowerActive && _prev.StarPowerActive)
            {
                // Drain finished: star_power_used = 0, UpdateNixie, GuitarEvent_StarPowerOff -> rock_meter_star_power_off.
                _starPowerUsed = false;
                UpdateNixie();
                _sched.Spawn("rock_meter_star_power_off", RockMeterStarPowerOff());
            }
        }

        /// <summary>Request a multiplier repaint without synthesizing another streak change.</summary>
        private void UpdateNixie() { _gScoreMultiplier = -1; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _sched.Reset(); }
            finally { ReleaseOwnedResources(); }
        }
    }
}
