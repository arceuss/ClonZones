using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Owns the exact Clone Hero renderer leaves the GH3 HUD replaces: score and combo
    /// SpriteFont glyphs, ComboColor multiplier/glow/x/ticks, SPBar fill and arrows,
    /// HealthContainer bars/arrow/glows, StarProgress bar/star. Controllers keep running (they still update
    /// their sprites); only `forceRenderingOff` is flipped, and the prior value is
    /// restored while the objects are alive. High score, FC indicator and solo counter
    /// stay untouched.
    /// </summary>
    internal sealed class Gh3HudVisibility
    {
        private readonly struct Owned
        {
            public readonly Renderer Renderer;
            public readonly IntPtr Pointer;
            public readonly bool WasOff;
            public Owned(Renderer renderer) { Renderer = renderer; Pointer = renderer.Pointer; WasOff = renderer.forceRenderingOff; }
        }

        private readonly List<Owned> _owned = new(64);
        private readonly HashSet<IntPtr> _ownedPointers = new();
        private float _nextDiscoveryTime;
        private int _phase = -1;
        private readonly MelonLogger.Instance _log;
        private readonly BasePlayer _player;
        private bool _hidden;

        public Gh3HudVisibility(BasePlayer player, MelonLogger.Instance log) { _player = player; _log = log; }

        public int Count => _owned.Count;

        /// <summary>Collect and hide. Called after the first complete GH3 frame has been uploaded.</summary>
        public void Hide()
        {
            if (_hidden) return;
            _hidden = true;
            Collect();
            _log.Msg($"[ClonZones] GH3 HUD hid {_owned.Count} Clone Hero HUD renderer leaves.");
        }

        /// <summary>
        /// Keep cached leaves hidden every LateUpdate, including while paused. Dynamic
        /// glyph and multiplier arrays are checked directly. A reset request rediscovers at
        /// once; the wall-clock fallback for late online/ghost widgets runs one discovery
        /// phase per LateUpdate, so no single frame pays for every scene-wide search.
        /// </summary>
        public void Reassert(bool rediscover = false)
        {
            if (!_hidden) return;
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                // the stored pointer stays valid: _owned roots the wrapper and its gchandle.
                if (!UnityIcalls.AlivePtr(_owned[i].Pointer)) { _ownedPointers.Remove(_owned[i].Pointer); _owned.RemoveAt(i); continue; }
                Renderer r = _owned[i].Renderer;
                if (!r.forceRenderingOff) r.forceRenderingOff = true;
            }
            ScoreManager score = _player.gameManager?.scoreManager;
            if (UnityIcalls.Alive(score)) { Font(score.scoreFont); Font(score.comboFont); }
            Combo(_player.comboCounter);
            if (rediscover) Collect();
            else if (_phase >= 0 || UnityIcalls.UnscaledTime >= _nextDiscoveryTime) CollectPhase();
        }

        private const int Phases = 6;

        private void Collect()
        {
            _phase = 0;
            while (_phase >= 0) CollectPhase();
        }

        private void CollectPhase()
        {
            long start = ClonZonesBenchmark.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (_phase < 0) _phase = 0;
            switch (_phase)
            {
                case 0: CollectOwned(); break;
                // Leaderboard mode instantiates extra multiplier/SP/health widgets beside the
                // leaderboard. Same component types, other instances: sweep the loaded scene.
                // Leaderboard/ghost widgets are created after their online fetch, i.e. after the first
                // sweep, so the type sweep repeats on every rediscovery (1 s fallback, right click).
                case 1:
                {
                    var all = Scene<ComboColor>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        var o = all[i].TryCast<ComboColor>();
                        if (UnityIcalls.Alive(o) && o.Pointer != _player.comboCounter?.Pointer && Combo(o)) Note("ComboColor", o.transform);
                    }
                    break;
                }
                case 2:
                {
                    var all = Scene<SPBar>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        var o = all[i].TryCast<SPBar>();
                        if (UnityIcalls.Alive(o) && o.Pointer != _player.spBar?.Pointer && Sp(o)) Note("SPBar", o.transform);
                    }
                    break;
                }
                case 3:
                {
                    var all = Scene<HealthContainer>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        var o = all[i].TryCast<HealthContainer>();
                        if (UnityIcalls.Alive(o) && o.Pointer != _player.healthContainer?.Pointer && Health(o)) Note("HealthContainer", o.transform);
                    }
                    break;
                }
                case 4:
                {
                    StarProgress own = _player.gameManager?.scoreManager?.starProgress;
                    var all = Scene<StarProgress>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        var o = all[i].TryCast<StarProgress>();
                        if (UnityIcalls.Alive(o) && o.Pointer != own?.Pointer && Stars(o)) Note("StarProgress", o.transform);
                    }
                    break;
                }
                default:
                {
                    var all = Scene<GhostHealthBar>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        var o = all[i].TryCast<GhostHealthBar>();
                        if (UnityIcalls.Alive(o) && Ghost(o)) Note("GhostHealthBar", o.transform);
                    }
                    break;
                }
            }
            if (++_phase >= Phases)
            {
                _phase = -1;
                _nextDiscoveryTime = UnityIcalls.UnscaledTime + 1f;
            }
            if (start != 0)
            {
                ClonZonesBenchmark.RecordDiscovery(System.Diagnostics.Stopwatch.GetTimestamp() - start);
                ClonZonesBenchmark.Mark(BenchmarkEvent.Discovery);
            }
        }

        private void CollectOwned()
        {
            ScoreManager score = _player.gameManager?.scoreManager;
            if (UnityIcalls.Alive(score))
            {
                Font(score.scoreFont);
                Font(score.comboFont);
                if (UnityIcalls.Alive(score.comboTransform)) Children(score.comboTransform.gameObject);
                Stars(score.starProgress);
            }
            Combo(_player.comboCounter);
            Sp(_player.spBar);
            Health(_player.healthContainer);
            Housings(score, _player.comboCounter, _player.spBar, _player.healthContainer);
        }

        private readonly HashSet<IntPtr> _swept = new();

        /// <summary>
        /// The component properties only expose the dynamic leaves. The housings around them
        /// (multiplier background/connector/smoke, FC ring, ghost meter ring, streak_meter
        /// backing, SPBar frame and dark arrow, Score_BG/overlay, star backgrounds, song
        /// progress) are plain
        /// child sprites, so walk the subtrees anchored on known leaves. Discovery only: the
        /// walks allocate.
        /// </summary>
        private void Housings(ScoreManager score, ComboColor combo, SPBar sp, HealthContainer health)
        {
            if (UnityIcalls.Alive(combo))
            {
                if (UnityIcalls.Alive(combo.multiplierRenderer)) Subtree(combo.multiplierRenderer.transform.parent);
                var ticks = combo.tickRenderers;
                if (ticks != null && ticks.Length > 0 && UnityIcalls.Alive(ticks[0])) Subtree(ticks[0].transform.parent?.parent);
            }
            if (UnityIcalls.Alive(sp?.starPowerBar)) Subtree(sp.starPowerBar.transform.parent?.parent);
            if (UnityIcalls.Alive(health?.redBar)) Subtree(health.redBar.transform.parent);
            if (UnityIcalls.Alive(score))
            {
                var digits = score.scoreFont?.Sprites;
                if (digits != null && digits.Length > 0 && UnityIcalls.Alive(digits[0]))
                {
                    Transform scoreHousing = digits[0].transform.parent;
                    Subtree(scoreHousing);
                    // Sibling group on the HUD camera: song progress bar, end cap, overlay and time text.
                    Subtree(scoreHousing?.parent?.Find("Song Progress"));
                }
                var stars = score.starProgress;
                if (UnityIcalls.Alive(stars?.progressBar)) Subtree(stars.progressBar.parent);
            }
        }

        /// <summary>Hide every renderer under a housing; never the player root, a scene root, or the HUD camera root.</summary>
        private void Subtree(Transform housing)
        {
            if (!UnityIcalls.Alive(housing) || !UnityIcalls.Alive(housing.parent)) return;
            if (housing.Pointer == _player.transform.Pointer) return;
            Children(housing.gameObject);
        }

        private bool Stars(StarProgress stars)
        {
            if (!UnityIcalls.Alive(stars)) return false;
            bool fresh = _swept.Add(stars.Pointer);
            if (UnityIcalls.Alive(stars.progressBar)) Children(stars.progressBar.gameObject);
            if (UnityIcalls.Alive(stars.progressBarEnd)) Children(stars.progressBarEnd.gameObject);
            One(stars.starCount); One(stars.starCountBG);
            if (UnityIcalls.Alive(stars.starParticles)) One(stars.starParticles.GetComponent<Renderer>());
            return fresh;
        }

        /// <summary>Leaderboard-mode lifebar: bar, arrow and its two cached renderers.</summary>
        private bool Ghost(GhostHealthBar ghost)
        {
            if (!UnityIcalls.Alive(ghost)) return false;
            bool fresh = _swept.Add(ghost.Pointer);
            if (fresh && UnityIcalls.Alive(ghost.healthBar)) Subtree(ghost.healthBar.transform.parent);
            One(ghost.healthBar);
            One(ghost.field_Private_SpriteRenderer_0); One(ghost.field_Private_SpriteRenderer_1);
            Children(ghost.topArrow);
            return fresh;
        }

        private bool Combo(ComboColor combo)
        {
            if (!UnityIcalls.Alive(combo)) return false;
            bool fresh = _swept.Add(combo.Pointer);
            One(combo.multiplierRenderer); One(combo.glowRenderer); One(combo.xRenderer);
            Array(combo.tickRenderers);
            return fresh;
        }

        private bool Sp(SPBar sp)
        {
            if (!UnityIcalls.Alive(sp)) return false;
            bool fresh = _swept.Add(sp.Pointer);
            One(sp.starPowerBar);
            One(sp.field_Private_SpriteRenderer_0); One(sp.field_Private_SpriteRenderer_1);
            Children(sp.topArrow); Children(sp.bottomArrow);
            return fresh;
        }

        private bool Health(HealthContainer health)
        {
            if (!UnityIcalls.Alive(health)) return false;
            bool fresh = _swept.Add(health.Pointer);
            Children(health.redBar); Children(health.yellowBar); Children(health.greenBar);
            if (UnityIcalls.Alive(health.arrowTransform)) Children(health.arrowTransform.gameObject);
            One(health.arrowGlowRenderer); One(health.glowRenderer);
            return fresh;
        }

        /// <summary>Scene instances of a component type, inactive included (non-generic lookup: the generic one is stripped).</summary>
        private static Il2CppReferenceArray<UnityEngine.Object> Scene<T>() where T : Component
        {
            return UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<T>(), true);
        }

        private void Note(string type, Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; UnityIcalls.Alive(p); p = p.parent) path = p.name + "/" + path;
            _log.Msg($"[ClonZones] GH3 HUD also hid {type} at {path} ({_owned.Count} leaves owned).");
        }

        private void Font(SpriteFont font)
        {
            if (!UnityIcalls.Alive(font)) return;
            Array(font.Sprites);
        }

        private void Array(Il2CppReferenceArray<SpriteRenderer> renderers)
        {
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++) One(renderers[i]);
        }

        private void Children(GameObject go)
        {
            if (!UnityIcalls.Alive(go)) return;
            var components = go.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Renderer>(), true);
            for (int i = 0; i < components.Length; i++) One(components[i].TryCast<Renderer>());
        }

        private void One(Renderer renderer)
        {
            if (!UnityIcalls.Alive(renderer)) return;
            if (_ownedPointers.Add(renderer.Pointer)) _owned.Add(new Owned(renderer));
            // Owning it already does not mean CH has left the flag alone this frame.
            if (!renderer.forceRenderingOff) renderer.forceRenderingOff = true;
        }

        /// <summary>Restore prior values on objects that still exist; dead wrappers are skipped, never written through.</summary>
        public void Restore()
        {
            foreach (Owned o in _owned)
            {
                try { if (UnityIcalls.Alive(o.Renderer)) o.Renderer.forceRenderingOff = o.WasOff; }
                catch (System.Exception) { /* object already destroyed by the scene teardown */ }
            }
            _owned.Clear(); _ownedPointers.Clear(); _swept.Clear();
            _hidden = false;
            _phase = -1;
        }
    }
}
