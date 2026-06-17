using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// GH3-style hit flames. Hooks GuitarNeckController.PlayFret and, for each lit
    /// lane, plays a pooled flame sprite (<see cref="FlameSpriteBank"/>) anchored to
    /// that fret. Self-contained: it does not touch the note or fret reskin paths.
    ///
    /// Note: turn OFF Clone Hero's built-in flames if vanilla flames render
    /// alongside these. PlayFret still fires regardless of that setting, so our
    /// GH3 flames play either way.
    /// </summary>
    internal static class GuitarFlamePatch
    {
        // ── Tunables (adjust at runtime; see also FlameSpriteBank.FlamePpu/Pivot) ──
        // BetterGH3's high-FPS fix converts GH3's 6+10 gameframe waits to
        // 100.100100ms + 166.833500ms, i.e. NTSC 59.94 FPS frame time.
        private const float Gh3FlameFrameSeconds = 1001f / 60000f;
        private const float Gh3HitFxSeconds = 16f * Gh3FlameFrameSeconds;
        private const float FlameLifeSeconds = 0.20f;             // legacy custom-spawn path, currently unused
        private const int FlameSortingOrder = 25000;             // legacy custom-spawn path, currently unused
        private static readonly Vector3 LocalOffset = new Vector3(0f, 0f, -0.20f);
        private const int MaxConcurrent = 48;

        private const ushort OpenNoteMask = 0x0001;

        private static MelonLogger.Instance _log;
        private static bool _active;


        private static readonly Dictionary<IntPtr, Transform[]> FretTransformsByNeck = new();
        private static readonly Dictionary<IntPtr, string> FretLayerByNeck = new();
        private static readonly Dictionary<IntPtr, Il2Cpp.Animator[]> FlameAnimatorsByNeck = new();
        private static readonly HashSet<IntPtr> _flamesSuppressed = new();
        private static readonly List<Flame> _pool = new(MaxConcurrent);
        private static readonly List<StackedFlame> _stackedFlames = new(MaxConcurrent);
        private static readonly Dictionary<IntPtr, List<StackedFlame>> _stackedByTemplate = new();
        private static int _activePoolCount;
        private static int _activeStackedCount;

        private static bool _warnedPlayFretError;

        private sealed class Flame
        {
            public GameObject Go;
            public SpriteRenderer Renderer;
            public float StartTime;
            public bool Starpower;
            public bool Active;
            public int LastFrame = -1;
            public Sprite[] Frames;
        }

        private sealed class StackedFlame
        {
            public GameObject Go;
            public Il2Cpp.Animator Animator;
            public SpriteRenderer Renderer;
            public IntPtr TemplatePtr;
            public float StartTime;
            public bool Starpower;
            public bool Active;
            public Sprite[] Frames;
            public int LastFrame = -1;
        }

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            _log = log;
            if (!FlameSpriteBank.IsReady)
            {
                log.Warning("[ClonZones] FlameSpriteBank not ready; flames disabled.");
                return;
            }

            var neckType = FindIl2CppType("GuitarNeckController");
            var playFret = ResolvePlayFretMethod(neckType);
            if (playFret == null)
            {
                log.Warning("[ClonZones] Could not resolve GuitarNeckController.PlayFret; flames disabled.");
                return;
            }

            harmony.Patch(playFret, prefix: new HarmonyMethod(typeof(GuitarFlamePatch), nameof(PlayFretPrefix)));
            log.Msg($"[ClonZones] Installed vanilla flame PlayFret prefix: {playFret.Name}");
        }

        public static void SetActive(bool active)
        {
            _active = active;
            if (active)
                RefreshChFlamesEnabled();
            else
                DeactivateAll();
        }

        public static void ClearRuntimeState()
        {
            // Pooled flame objects are parented to fret transforms, so they are
            // destroyed with the gameplay scene. Drop the (now-dead) references and
            // recreate lazily on the next song.
            FretTransformsByNeck.Clear();
            FretLayerByNeck.Clear();
            FlameAnimatorsByNeck.Clear();
            _flamesSuppressed.Clear();
            _pool.Clear();
            _stackedFlames.Clear();
            _stackedByTemplate.Clear();
            _activePoolCount = 0;
            _activeStackedCount = 0;
        }

        // ─── PlayFret prefix: prepare vanilla flame animators before CH plays them ──

        private static void PlayFretPrefix(object __instance, ObjectPublicObInObDoSiDoUIInBoInUnique __0, bool __1, bool __2)
        {
            if (!_active || !FlameSpriteBank.IsReady || __instance == null || __0 == null)
                return;

            // Obey Clone Hero's Video settings "Flames" toggle, same as vanilla
            // flames do. The value is cached once per gameplay scene activation.
            if (!_chFlamesEnabled)
                return;

            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.FlamePlayFret);
            try
            {
                ushort mask = __0.field_Public_UInt16_0;
                if (mask == 0 || mask == OpenNoteMask)
                    return;

                bool starpower = __2;
                int frameCount = FlameSpriteBank.FrameCount;
                if (frameCount <= 0)
                    return;

                float now = Time.time;
                var flameAnimators = GetVanillaFlameAnimators(__instance);
                if (flameAnimators == null)
                    return;

                for (int i = 0; i < 5 && i < flameAnimators.Length; i++)
                {
                    if ((mask & (1 << (i + 1))) == 0)
                        continue;

                    PrepareVanillaFlame(flameAnimators[i], starpower, i, now);
                }
            }
            catch (Exception ex)
            {
                if (!_warnedPlayFretError)
                {
                    _warnedPlayFretError = true;
                    _log?.Warning($"[ClonZones] flame prepare error: {ex.Message}");
                }
            }
            finally
            {
                ClonZonesProfiler.EndScope(ProfileScope.FlamePlayFret, profileStart);
            }
        }

        // ─── Per-frame animation (driven from Core.OnUpdate) ───────────────────

        public static void Tick()
        {
            if (!_active || (_activePoolCount <= 0 && _activeStackedCount <= 0))
                return;

            int frameCount = FlameSpriteBank.FrameCount;
            if (frameCount <= 0)
                return;

            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.FlameTick);

            float now = Time.time;
            int remainingPool = _activePoolCount;
            for (int i = 0; i < _pool.Count && remainingPool > 0; i++)
            {
                var f = _pool[i];
                if (!f.Active)
                    continue;
                remainingPool--;

                if (f.Go == null)
                {
                    Deactivate(f); // destroyed with the scene
                    continue;
                }

                float life = (now - f.StartTime) / FlameLifeSeconds;
                if (life >= 1f)
                {
                    Deactivate(f);
                    continue;
                }

                int frame = (int)(life * frameCount);
                if (frame >= frameCount)
                    frame = frameCount - 1;
                if (frame != f.LastFrame)
                {
                    Sprite[] frames = f.Frames;
                    if (frames != null && frame < frames.Length)
                        f.Renderer.sprite = frames[frame];
                    f.LastFrame = frame;
                }
            }

            int remainingStacked = _activeStackedCount;
            for (int i = 0; i < _stackedFlames.Count && remainingStacked > 0; i++)
            {
                var f = _stackedFlames[i];
                if (!f.Active)
                    continue;
                remainingStacked--;

                if (f.Go == null)
                {
                    Deactivate(f);
                    continue;
                }
                float life = (now - f.StartTime) / Gh3HitFxSeconds;
                if (life >= 1f)
                {
                    Deactivate(f);
                    continue;
                }

                int frame = (int)(life * frameCount);
                if (frame >= frameCount)
                    frame = frameCount - 1;
                if (frame != f.LastFrame)
                {
                    Sprite[] frames = f.Frames;
                    if (frames != null && frame < frames.Length)
                        f.Renderer.sprite = frames[frame];
                    f.LastFrame = frame;
                }
            }
            ClonZonesProfiler.EndScope(ProfileScope.FlameTick, profileStart);
        }

        // ─── CH "Flames" video setting ──────────────────────────────────────────
        // The GameSetting/GameConfig types are name-mangled in the live build, so
        // the toggle is read from its persisted source instead: PlayerData/
        // settings.ini → [video] flames. CH only allows changing it outside
        // gameplay, so one read per gameplay scene activation matches vanilla
        // semantics with zero per-note cost.

        private static bool _chFlamesEnabled = true;

        private static void RefreshChFlamesEnabled()
        {
            bool enabled = true;
            try
            {
                var modDir = Path.GetDirectoryName(typeof(GuitarFlamePatch).Assembly.Location);
                var gameDir = string.IsNullOrEmpty(modDir) ? null : Path.GetDirectoryName(modDir);
                var path = gameDir == null ? null : Path.Combine(gameDir, "PlayerData", "settings.ini");
                if (path != null && File.Exists(path))
                {
                    bool inVideo = false;
                    foreach (var rawLine in File.ReadAllLines(path))
                    {
                        var line = rawLine.Trim();
                        if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                            continue;

                        if (line.StartsWith("[") && line.EndsWith("]"))
                        {
                            inVideo = string.Equals(line, "[video]", StringComparison.OrdinalIgnoreCase);
                            continue;
                        }

                        if (!inVideo)
                            continue;

                        int equals = line.IndexOf('=');
                        if (equals <= 0 || !string.Equals(line.Substring(0, equals).Trim(), "flames", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var value = line.Substring(equals + 1).Trim();
                        enabled = !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"[ClonZones] Could not read CH flames setting: {ex.Message}");
            }

            if (enabled != _chFlamesEnabled)
                _log?.Msg($"[ClonZones] CH flames setting: {(enabled ? "on" : "off")}.");
            _chFlamesEnabled = enabled;
        }

        // ─── Helpers ───────────────────────────────────────────────────────────

        private static void SpawnAt(Transform fret, string layer, bool starpower, float now)
        {
            if (fret == null)
                return;

            var flame = GetFreeFlame();
            if (flame == null)
                return;

            var t = flame.Go.transform;
            t.SetParent(fret, false);
            t.localPosition = LocalOffset;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            var fretGo = fret.gameObject;
            if (fretGo != null)
                flame.Go.layer = fretGo.layer;

            flame.Starpower = starpower;
            flame.Frames = FlameSpriteBank.GetFrames(starpower);
            flame.StartTime = now;
            if (!flame.Active)
                _activePoolCount++;
            flame.Active = true;
            flame.LastFrame = 0;

            var sr = flame.Renderer;
            sr.sprite = flame.Frames != null && flame.Frames.Length > 0 ? flame.Frames[0] : null;

            // Match the fret's sorting layer so the flame isn't hidden behind the
            // highway (sorting layer takes priority over sortingOrder).
            if (!string.IsNullOrEmpty(layer))
                sr.sortingLayerName = layer;
            sr.sortingOrder = FlameSortingOrder;

            flame.Go.SetActive(true);

        }

        // Read the fret lane's sorting layer from the animator's Base renderer via
        // typed field access (no GetComponent* — those overloads can be stripped).
        private static string GetFretLayer(object neck)
        {
            var ptr = ObjectPointer(neck);
            if (ptr != IntPtr.Zero && FretLayerByNeck.TryGetValue(ptr, out var cached))
                return cached;

            string layer = null;
            var flames = GetMember(neck, "FretAnimators");
            if (flames is IEnumerable enumerable)
            {
                foreach (var a in enumerable)
                {
                    var fret = a as GuitarFretAnimator;
                    var rend = fret != null ? (fret.Base ?? fret.head) : null;
                    if (rend != null)
                    {
                        layer = rend.sortingLayerName;
                        break;
                    }
                }
            }

            if (ptr != IntPtr.Zero)
                FretLayerByNeck[ptr] = layer;
            return layer;
        }

        private static Flame GetFreeFlame()
        {
            for (int i = 0; i < _pool.Count; i++)
            {
                var f = _pool[i];
                if (f.Go == null)
                {
                    Deactivate(f);
                    continue;
                }
                if (!f.Active)
                    return f;
            }

            if (_pool.Count >= MaxConcurrent)
                return null;

            return CreateFlame();
        }

        private static Flame CreateFlame()
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.FlameCreate);
            try
            {
                var go = new GameObject("ClonZonesFlame");
                go.hideFlags = HideFlags.HideAndDontSave;
                var sr = go.AddComponent<SpriteRenderer>();
                var mat = FlameSpriteBank.FlameMaterial;
                if (mat != null)
                    sr.sharedMaterial = mat;
                go.SetActive(false);

                var flame = new Flame { Go = go, Renderer = sr };
                _pool.Add(flame);
                return flame;
            }
            finally
            {
                ClonZonesProfiler.EndScope(ProfileScope.FlameCreate, profileStart);
            }
        }

        private static void Deactivate(Flame f)
        {
            if (f == null)
                return;

            if (f.Active && _activePoolCount > 0)
                _activePoolCount--;
            f.Active = false;
            f.LastFrame = -1;
            if (f.Go != null)
                f.Go.SetActive(false);
        }

        private static void Deactivate(StackedFlame f)
        {
            if (f == null)
                return;

            if (f.Active && _activeStackedCount > 0)
                _activeStackedCount--;
            f.Active = false;
            f.LastFrame = -1;
            if (f.Renderer != null)
                f.Renderer.sprite = null;
            if (f.Go != null)
                f.Go.SetActive(false);
        }

        private static void DeactivateAll()
        {
            for (int i = 0; i < _pool.Count; i++)
                Deactivate(_pool[i]);
            for (int i = 0; i < _stackedFlames.Count; i++)
                Deactivate(_stackedFlames[i]);
            _activePoolCount = 0;
            _activeStackedCount = 0;
        }

        private static void SuppressVanillaFlames(object neck)
        {
            var ptr = ObjectPointer(neck);
            if (ptr == IntPtr.Zero || _flamesSuppressed.Contains(ptr))
                return;
            _flamesSuppressed.Add(ptr);

            // FlameAnimators are the vanilla, per-fret-colored hit flames. Disabling
            // their GameObjects keeps PlayFret's Play() calls from rendering anything,
            // so only ClonZones' GH3 flames remain. Lightning/sparks are untouched.
            var flames = GetMember(neck, "FlameAnimators");
            if (flames is not IEnumerable enumerable)
                return;

            foreach (var a in enumerable)
            {
                var comp = a as UnityEngine.Component;
                if (comp != null)
                    comp.gameObject.SetActive(false);
            }
        }


        private static Il2Cpp.Animator[] GetVanillaFlameAnimators(object neck)
        {
            var ptr = ObjectPointer(neck);
            if (ptr != IntPtr.Zero && FlameAnimatorsByNeck.TryGetValue(ptr, out var cached))
                return cached;

            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.FlameCacheBuild);
            try
            {
                var flames = GetMember(neck, "FlameAnimators")
                             ?? GetMember(neck, "field_Public_Animator_Array_0");
                if (flames is not IEnumerable enumerable)
                    return null;

                var animators = new Il2Cpp.Animator[5];
                int count = 0;
                foreach (var a in enumerable)
                {
                    if (count >= animators.Length)
                        break;
                    animators[count++] = a as Il2Cpp.Animator;
                }

                if (count == 0)
                    return null;

                if (ptr != IntPtr.Zero)
                    FlameAnimatorsByNeck[ptr] = animators;
                return animators;
            }
            finally
            {
                ClonZonesProfiler.EndScope(ProfileScope.FlameCacheBuild, profileStart);
            }
        }

        private static void PrepareVanillaFlame(Il2Cpp.Animator template, bool starpower, int laneIndex, float now)
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.FlamePrepare);
            try
            {
                if (template == null)
                    return;



                var flame = GetFreeStackedFlame(template);
                if (flame == null)
                    return;

                SuppressTemplateFlame(template);

                flame.Animator.enabled = false;
                flame.Renderer.color = Color.white;
                flame.Frames = FlameSpriteBank.GetFrames(starpower);
                flame.Renderer.sprite = flame.Frames != null && flame.Frames.Length > 0 ? flame.Frames[0] : null;
                flame.StartTime = now;
                flame.Starpower = starpower;
                if (!flame.Active)
                    _activeStackedCount++;
                flame.Active = true;
                flame.LastFrame = 0;

                flame.Go.SetActive(true);
            }
            finally
            {
                ClonZonesProfiler.EndScope(ProfileScope.FlamePrepare, profileStart);
            }
        }

        private static StackedFlame GetFreeStackedFlame(Il2Cpp.Animator template)
        {
            var templatePtr = ObjectPointer(template);
            if (templatePtr != IntPtr.Zero && _stackedByTemplate.TryGetValue(templatePtr, out var list))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var f = list[i];
                    if (f.Go == null)
                    {
                        Deactivate(f);
                        continue;
                    }
                    if (!f.Active)
                        return f;
                }
            }
            else if (templatePtr == IntPtr.Zero)
            {
                for (int i = 0; i < _stackedFlames.Count; i++)
                {
                    var f = _stackedFlames[i];
                    if (f.Go == null)
                    {
                        Deactivate(f);
                        continue;
                    }
                    if (!f.Active && f.TemplatePtr == templatePtr)
                        return f;
                }
            }

            if (_stackedFlames.Count >= MaxConcurrent)
                return null;

            return CreateStackedFlame(template, templatePtr);
        }

        private static StackedFlame CreateStackedFlame(Il2Cpp.Animator template, IntPtr templatePtr)
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.FlameCreate);
            try
            {
                var templateGo = template.gameObject;
                if (templateGo == null)
                    return null;

                var parent = templateGo.transform.parent;
                var go = UnityEngine.Object.Instantiate(templateGo, parent);
                go.name = "ClonZonesStackedFlame";
                go.hideFlags = HideFlags.HideAndDontSave;

                var anim = go.GetComponent<Il2Cpp.Animator>();
                var sr = go.GetComponent<SpriteRenderer>();
                if (anim == null || sr == null)
                {
                    UnityEngine.Object.Destroy(go);
                    return null;
                }

                EnsureAnimatorRenderer(anim, sr);
                anim.enabled = false;
                go.SetActive(false);

                var flame = new StackedFlame
                {
                    Go = go,
                    Animator = anim,
                    Renderer = sr,
                    TemplatePtr = templatePtr
                };
                _stackedFlames.Add(flame);
                if (templatePtr != IntPtr.Zero)
                {
                    if (!_stackedByTemplate.TryGetValue(templatePtr, out var list))
                    {
                        list = new List<StackedFlame>(4);
                        _stackedByTemplate[templatePtr] = list;
                    }
                    list.Add(flame);
                }
                return flame;
            }
            finally
            {
                ClonZonesProfiler.EndScope(ProfileScope.FlameCreate, profileStart);
            }
        }

        private static void SuppressTemplateFlame(Il2Cpp.Animator template)
        {
            var go = template.gameObject;
            if (go != null && go.activeSelf)
                go.SetActive(false);
        }

        private static void EnsureAnimatorRenderer(Il2Cpp.Animator animator, SpriteRenderer renderer)
        {
            SetMember(animator, "field_Private_SpriteRenderer_0", renderer);
        }

        private static Transform[] GetFretTransforms(object neck)
        {
            var ptr = ObjectPointer(neck);
            if (ptr != IntPtr.Zero && FretTransformsByNeck.TryGetValue(ptr, out var cached))
                return cached;

            var animators = GetMember(neck, "FretAnimators")
                            ?? GetMember(neck, "field_Public_BaseFretAnimator_Array_0");
            if (animators is not IEnumerable enumerable)
                return null;

            var list = new List<Transform>(5);
            foreach (var a in enumerable)
            {
                var comp = a as UnityEngine.Component;
                list.Add(comp != null ? comp.transform : null);
            }

            if (list.Count == 0)
                return null;

            var arr = list.ToArray();
            if (ptr != IntPtr.Zero)
                FretTransformsByNeck[ptr] = arr;
            return arr;
        }


        private static object GetMember(object instance, string name)
        {
            if (instance == null)
                return null;

            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
                return property.GetValue(instance);

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(instance);
        }

        private static void SetMember(object instance, string name, object value)
        {
            if (instance == null)
                return;

            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                property.SetValue(instance, value);
                return;
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            field?.SetValue(instance, value);
        }

        private static IntPtr ObjectPointer(object instance)
        {
            return instance switch
            {
                UnityEngine.Object obj when obj != null => obj.Pointer,
                Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj when obj != null => obj.Pointer,
                _ => IntPtr.Zero
            };
        }

        private static Type FindIl2CppType(string typeName)
        {
            var type = Type.GetType("Il2Cpp." + typeName + ", Il2CppCloneHero", false);
            if (type != null)
                return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType("Il2Cpp." + typeName, false) ?? assembly.GetType(typeName, false);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static MethodInfo ResolvePlayFretMethod(Type neckType)
        {
            return neckType?.GetMethod("PlayFret", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? neckType?.GetMethod("Method_Public_Virtual_Void_ObjectPublicObInObDoSiDoUIInBoInUnique_Boolean_Boolean_0", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
    }
}
