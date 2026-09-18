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
            _nextDiscoveryTime = Time.unscaledTime + 1f;
            _log.Msg($"[ClonZones] GH3 HUD hid {_owned.Count} Clone Hero HUD renderer leaves ({_extra} extra HUD component instances).");
        }

        /// <summary>
        /// Keep cached leaves hidden every LateUpdate, including while paused. Dynamic
        /// glyph and multiplier arrays are checked directly; expensive hierarchy discovery
        /// runs only on a reset request or the wall-clock fallback, not every 64 frames.
        /// </summary>
        public void Reassert(bool rediscover = false)
        {
            if (!_hidden) return;
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                Renderer r = _owned[i].Renderer;
                if (r == null) { _ownedPointers.Remove(_owned[i].Pointer); _owned.RemoveAt(i); continue; }
                if (!r.forceRenderingOff) r.forceRenderingOff = true;
            }
            ScoreManager score = _player.gameManager?.scoreManager;
            if (score != null) { Font(score.scoreFont); Font(score.comboFont); }
            Combo(_player.comboCounter);
            if (rediscover || Time.unscaledTime >= _nextDiscoveryTime)
            {
                _nextDiscoveryTime = Time.unscaledTime + 1f;
                Collect();
            }
        }

        private int _extra;

        private void Collect()
        {
            ScoreManager score = _player.gameManager?.scoreManager;
            if (score != null)
            {
                Font(score.scoreFont);
                Font(score.comboFont);
                if (score.comboTransform != null) Children(score.comboTransform.gameObject);
                Stars(score.starProgress);
            }
            Combo(_player.comboCounter);
            Sp(_player.spBar);
            Health(_player.healthContainer);
            // Leaderboard mode instantiates extra multiplier/SP/health widgets beside the
            // leaderboard. Same component types, other instances: sweep the loaded scene.
            if (_sweptScene) return;
            _sweptScene = true;
            foreach (var o in Scene<ComboColor>()) { if (o.Pointer != _player.comboCounter?.Pointer) { Combo(o); _extra++; Note("ComboColor", o.transform); } }
            foreach (var o in Scene<SPBar>()) { if (o.Pointer != _player.spBar?.Pointer) { Sp(o); _extra++; Note("SPBar", o.transform); } }
            foreach (var o in Scene<HealthContainer>()) { if (o.Pointer != _player.healthContainer?.Pointer) { Health(o); _extra++; Note("HealthContainer", o.transform); } }
            StarProgress own = _player.gameManager?.scoreManager?.starProgress;
            foreach (var o in Scene<StarProgress>()) { if (o.Pointer != own?.Pointer) { Stars(o); _extra++; Note("StarProgress", o.transform); } }
        }

        private bool _sweptScene;

        private void Stars(StarProgress stars)
        {
            if (stars == null) return;
            if (stars.progressBar != null) Children(stars.progressBar.gameObject);
            if (stars.progressBarEnd != null) Children(stars.progressBarEnd.gameObject);
            One(stars.starCount); One(stars.starCountBG);
            if (stars.starParticles != null) One(stars.starParticles.GetComponent<Renderer>());
        }

        private void Combo(ComboColor combo)
        {
            if (combo == null) return;
            One(combo.multiplierRenderer); One(combo.glowRenderer); One(combo.xRenderer);
            Array(combo.tickRenderers);
        }

        private void Sp(SPBar sp)
        {
            if (sp == null) return;
            One(sp.starPowerBar);
            One(sp.field_Private_SpriteRenderer_0); One(sp.field_Private_SpriteRenderer_1);
            Children(sp.topArrow); Children(sp.bottomArrow);
        }

        private void Health(HealthContainer health)
        {
            if (health == null) return;
            Children(health.redBar); Children(health.yellowBar); Children(health.greenBar);
            if (health.arrowTransform != null) Children(health.arrowTransform.gameObject);
            One(health.arrowGlowRenderer); One(health.glowRenderer);
        }

        /// <summary>Scene instances of a component type, inactive included (non-generic lookup: the generic one is stripped).</summary>
        private static List<T> Scene<T>() where T : Component
        {
            var result = new List<T>();
            var all = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<T>(), true);
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i].TryCast<T>();
                if (c != null) result.Add(c);
            }
            return result;
        }

        private void Note(string type, Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            _log.Msg($"[ClonZones] GH3 HUD also hid {type} at {path}.");
        }

        private void Font(SpriteFont font)
        {
            if (font == null) return;
            Array(font.Sprites);
        }

        private void Array(Il2CppReferenceArray<SpriteRenderer> renderers)
        {
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++) One(renderers[i]);
        }

        private void Children(GameObject go)
        {
            if (go == null) return;
            var components = go.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Renderer>(), true);
            for (int i = 0; i < components.Length; i++) One(components[i].TryCast<Renderer>());
        }

        private void One(Renderer renderer)
        {
            if (renderer == null) return;
            if (_ownedPointers.Add(renderer.Pointer)) _owned.Add(new Owned(renderer));
            // Owning it already does not mean CH has left the flag alone this frame.
            if (!renderer.forceRenderingOff) renderer.forceRenderingOff = true;
        }

        /// <summary>Restore prior values on objects that still exist; dead wrappers are skipped, never written through.</summary>
        public void Restore()
        {
            foreach (Owned o in _owned)
            {
                try { if (o.Renderer != null) o.Renderer.forceRenderingOff = o.WasOff; }
                catch (System.Exception) { /* object already destroyed by the scene teardown */ }
            }
            _owned.Clear(); _ownedPointers.Clear();
            _hidden = false;
        }
    }
}
