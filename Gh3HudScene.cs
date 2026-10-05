using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClonZones
{
    /// <summary>Receives world-composed quads in authored 1280x720 space (y down), already z-ordered.</summary>
    internal interface IGh3HudQuadSink
    {
        void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float u0, float v0, float u1, float v1, Color32 color, Gh3HudBlend blend = Gh3HudBlend.Alpha);
    }

    /// <summary>
    /// The ScreenElement tree for one player's HUD: creation/destroy, the per-frame
    /// compose recovered from 0x4FE8E0 (child pos scaled and rotated by the parent,
    /// multiplicative scale and alpha, additive rotation) and the draw pass (sprites and
    /// text, z_priority ascending, construction order for ties).
    /// </summary>
    internal sealed class Gh3HudScene
    {
        private struct World
        {
            public Vector2 Pos, Scale;
            public float Rot, Alpha;
        }

        private readonly List<Gh3HudElement> _elements = new(192);
        private readonly Dictionary<string, Gh3HudElement> _byId = new(192, StringComparer.Ordinal);
        private World[] _world = new World[192];
        private Gh3HudElement[] _drawOrder = new Gh3HudElement[192];
        private int _nextOrder;
        private bool _orderDirty = true;
        private readonly Func<string, Gh3HudRegion> _regions;

        public Gh3HudScene(Func<string, Gh3HudRegion> regions) { _regions = regions; }

        public Gh3HudElement Find(string id) => _byId.TryGetValue(id, out var e) && e.Alive ? e : null;
        public bool Exists(string id) => Find(id) != null;
        public int Count => _elements.Count;

        public Gh3HudElement CreateContainer(string id, Gh3HudElement parent, Vector2 pos, float rot = 0f, float scale = 1f, float z = 0f)
        {
            var e = Add(id, parent);
            e.IsContainer = true;
            e.SetPos(pos); e.SetRot(rot); e.SetScale(scale); e.Z = z;
            return e;
        }

        public Gh3HudElement CreateSprite(string id, Gh3HudElement parent, string texture, Vector2 pos, Vector2 just,
            float z, float alpha, Color32 rgba, float rot = 0f, Vector2? dims = null)
        {
            var e = Add(id, parent);
            SetTexture(e, texture);
            if (dims.HasValue) e.Dims = dims.Value;
            e.SetPos(pos); e.Just = just; e.Z = z; e.SetAlpha(alpha); e.SetRgba(rgba); e.SetRot(rot);
            return e;
        }

        public Gh3HudElement CreateText(string id, Gh3HudElement parent, Gh3HudFont font, string text, Vector2 pos, Vector2 just,
            float z, float alpha, Color32 rgba, Vector2 scale, float rot = 0f)
        {
            var e = Add(id, parent);
            e.Font = font; e.FontSpacing = -1f;
            e.SetText(text);
            e.SetPos(pos); e.Just = just; e.Z = z; e.SetAlpha(alpha); e.SetRgba(rgba); e.SetScale(scale); e.SetRot(rot);
            return e;
        }

        /// <summary>SetScreenElementProps texture = ...: swaps art, keeps dims (the bulb glow swaps rely on this).</summary>
        public void SetTexture(Gh3HudElement e, string texture)
        {
            Gh3HudRegion region = _regions(texture);
            if (!region.IsValid) throw new InvalidOperationException($"HUD texture '{texture}' is not loaded");
            bool keepDims = e.TextureName != null && e.Dims.x > 0f;
            e.TextureName = texture; e.Region = region;
            if (!keepDims) e.Dims = new Vector2(region.Width, region.Height);
        }

        /// <summary>DestroyScreenElement: the element and every descendant stop existing.</summary>
        public void Destroy(Gh3HudElement root)
        {
            if (root == null || !root.Alive) return;
            for (int i = 0; i < _elements.Count; i++)
            {
                Gh3HudElement e = _elements[i];
                for (Gh3HudElement p = e; p != null; p = p.Parent)
                    if (p == root) { e.Alive = false; break; }
            }
            for (int i = _elements.Count - 1; i >= 0; i--)
                if (!_elements[i].Alive) { _byId.Remove(_elements[i].Id); _elements[i].SceneIndex = -1; _elements.RemoveAt(i); }
            for (int i = 0; i < _elements.Count; i++) _elements[i].SceneIndex = i;
            _orderDirty = true;
        }

        public void Clear()
        {
            foreach (var e in _elements) { e.Alive = false; e.SceneIndex = -1; }
            _elements.Clear(); _byId.Clear(); _nextOrder = 0; _orderDirty = true;
        }

        private Gh3HudElement Add(string id, Gh3HudElement parent)
        {
            if (_byId.TryGetValue(id, out var old)) Destroy(old);
            var e = new Gh3HudElement(id, parent, _nextOrder++);
            e.SceneIndex = _elements.Count;
            _elements.Add(e); _byId[id] = e; _orderDirty = true;
            if (_elements.Count > _world.Length)
            {
                Array.Resize(ref _world, _elements.Count * 2);
                Array.Resize(ref _drawOrder, _elements.Count * 2);
            }
            return e;
        }

        public void ZChanged() { _orderDirty = true; }

        /// <summary>Morph timers for every live element (frame driver 0x4FF260: tick before compose). True when any element moved.</summary>
        public bool Tick(long nowMs)
        {
            bool animating = false;
            for (int i = 0; i < _elements.Count; i++)
            {
                Gh3HudElement e = _elements[i];
                if (!e.IsAnimating) continue;
                animating = true;
                e.Tick(nowMs);
            }
            return animating;
        }

        /// <summary>Compose the world state of every element (parents are always created before children).</summary>
        private void Compose()
        {
            for (int i = 0; i < _elements.Count; i++)
            {
                Gh3HudElement e = _elements[i];
                ref World w = ref _world[i];
                if (e.Parent == null)
                {
                    w.Pos = e.Pos; w.Scale = e.Scale; w.Rot = e.Rot; w.Alpha = e.Alpha;
                    continue;
                }
                World p = _world[e.Parent.SceneIndex];
                float lx = e.Pos.x * p.Scale.x, ly = e.Pos.y * p.Scale.y;
                e.Parent.RotationTrig(p.Rot, out float cos, out float sin);
                // 0x4FE8E0: wx = px + lx cos - ly sin, wy = py + lx sin + ly cos (clockwise in y-down space).
                w.Pos = new Vector2 { x = p.Pos.x + lx * cos - ly * sin, y = p.Pos.y + lx * sin + ly * cos };
                w.Scale = new Vector2 { x = p.Scale.x * e.Scale.x, y = p.Scale.y * e.Scale.y };
                w.Rot = p.Rot + e.Rot;
                w.Alpha = p.Alpha * e.Alpha;
            }
        }

        private void SortDrawOrder()
        {
            int n = _elements.Count;
            for (int i = 0; i < n; i++) _drawOrder[i] = _elements[i];
            // Insertion sort: nearly sorted after the first frame, tiny n.
            for (int i = 1; i < n; i++)
            {
                Gh3HudElement key = _drawOrder[i];
                int j = i - 1;
                while (j >= 0 && Later(_drawOrder[j], key)) { _drawOrder[j + 1] = _drawOrder[j]; j--; }
                _drawOrder[j + 1] = key;
            }
            _orderDirty = false;
        }

        // Higher z_priority draws on top; equal z keeps construction order (unverified tie rule, see report).
        private static bool Later(Gh3HudElement a, Gh3HudElement b) => a.Z > b.Z || (a.Z == b.Z && a.Order > b.Order);

        /// <summary>Compose and emit every visible quad in draw order.</summary>
        public void Draw(IGh3HudQuadSink sink)
        {
            Compose();
            if (_orderDirty) SortDrawOrder();
            int n = _elements.Count;
            for (int k = 0; k < n; k++)
            {
                Gh3HudElement e = _drawOrder[k];
                if (e.IsContainer) continue;
                World w = _world[e.SceneIndex];
                if (w.Alpha < 1e-4f) continue;
                if (e.Font != null) DrawText(e, w, sink);
                else DrawSprite(e, w, sink);
            }
        }

        private static void DrawSprite(Gh3HudElement e, World w, IGh3HudQuadSink sink)
        {
            byte a = (byte)(int)(e.Rgba.a * w.Alpha);
            if (a == 0) return;
            var color = new Color32 { r = e.Rgba.r, g = e.Rgba.g, b = e.Rgba.b, a = a };
            Vector2 anchor = new() { x = (e.Just.x + 1f) * 0.5f * e.Dims.x, y = (e.Just.y + 1f) * 0.5f * e.Dims.y };
            e.RotationTrig(w.Rot, out float cos, out float sin);
            Vector2 Corner(float x, float y)
            {
                float lx = (x - anchor.x) * w.Scale.x, ly = (y - anchor.y) * w.Scale.y;
                return new Vector2 { x = w.Pos.x + lx * cos - ly * sin, y = w.Pos.y + lx * sin + ly * cos };
            }
            Gh3HudRegion r = e.Region;
            // pivot still measures against the full dims; only the drawn art shrinks.
            float cw = e.Dims.x * r.CoverageX, ch = e.Dims.y * r.CoverageY;
            sink.Quad(Corner(0f, 0f), Corner(cw, 0f), Corner(cw, ch), Corner(0f, ch), r.U0, r.V1, r.U1, r.V0, color, e.Blend);
        }

        /// <summary>
        /// CTextElement per-frame push (0x5AD9C0) + SText emitter: integer-truncated
        /// position, just offsets from the measured dims, shadow pass first at pos +
        /// shadow_offs with shadow_rgba * world alpha, glyph tops at
        /// (lineheight - (h - yoff) - yorigin) * sy + 1.25.
        /// </summary>
        private static void DrawText(Gh3HudElement e, World w, IGh3HudQuadSink sink)
        {
            Gh3HudFont font = e.Font;
            Vector2 measured = font.Measure(e.Text, e.TextLength, e.FontSpacing);
            float sx = w.Scale.x, sy = w.Scale.y;
            float jx = Clamp01((e.Just.x + 1f) * 0.5f), jy = Clamp01((e.Just.y + 1f) * 0.5f);
            Vector2 origin = new() { x = -jx * measured.x * sx, y = -jy * measured.y * sy };
            Vector2 basePos = new() { x = (int)w.Pos.x, y = (int)w.Pos.y };
            if (e.Shadow)
            {
                byte sa = (byte)(int)(e.ShadowRgba.a * w.Alpha);
                if (sa != 0)
                {
                    Vector2 shadowPos = new() { x = (int)(w.Pos.x + e.ShadowOffset.x), y = (int)(w.Pos.y + e.ShadowOffset.y) };
                    EmitGlyphs(e, font, shadowPos, origin, w, new Color32 { r = e.ShadowRgba.r, g = e.ShadowRgba.g, b = e.ShadowRgba.b, a = sa }, sink);
                }
            }
            byte a = (byte)(int)(e.Rgba.a * w.Alpha);
            if (a != 0) EmitGlyphs(e, font, basePos, origin, w, new Color32 { r = e.Rgba.r, g = e.Rgba.g, b = e.Rgba.b, a = a }, sink);
        }

        // Mathf.Clamp01's body (0x181E51660: below 0 -> 0, above 1 -> 1, NaN and -0 pass through).
        // The interop version is a boxing runtime_invoke, and this runs on every HUD redraw.
        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        private static void EmitGlyphs(Gh3HudElement e, Gh3HudFont font, Vector2 pos, Vector2 origin, World w, Color32 color, IGh3HudQuadSink sink)
        {
            float sx = w.Scale.x, sy = w.Scale.y;
            float pre = e.FontSpacing < 0f ? font.DefaultPre : 0f;
            float post = e.FontSpacing < 0f ? font.DefaultPost : e.FontSpacing;
            e.RotationTrig(w.Rot, out float cos, out float sin);
            Vector2 P(float x, float y)
            {
                float lx = origin.x + x, ly = origin.y + y;
                return new Vector2 { x = pos.x + lx * cos - ly * sin, y = pos.y + lx * sin + ly * cos };
            }
            float pen = 0f;
            for (int i = 0; i < e.TextLength; i++)
            {
                char c = e.Text[i];
                if (c == ' ') { pen += font.SpaceWidth * sx - pre + post * sx; continue; }
                Gh3HudFont.Glyph g = font.GlyphFor(c);
                float left = pen - (g.XOff * sx + pre);
                float top = (font.LineHeight - (g.Height - g.YOff) - font.YOrigin) * sy + Gh3HudFont.BaselineOffset;
                float qw = g.Width * sx, qh = g.Height * sy;
                font.AtlasUv(g, out float u0, out float v0, out float u1, out float v1);
                sink.Quad(P(left, top), P(left + qw, top), P(left + qw, top + qh), P(left, top + qh), u0, v1, u1, v0, color, e.Blend);
                pen = left + qw + g.Extra * sx + post * sx;
            }
        }
    }
}
