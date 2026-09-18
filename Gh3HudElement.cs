using System;
using UnityEngine;

namespace ClonZones
{
    /// <summary>Timer easing modes as stored in the GH3 morph timer (sub_55B2D0 mapping).</summary>
    internal enum Gh3Motion
    {
        Linear = 0,   // default word for a fresh element and explicit `linear`
        Smooth = 1,   // 3t^2 - 2t^3
        EaseIn = 2,   // t^2
        EaseOut = 3,  // 2t - t^2
        /// <summary>Script `gentle` has no case in the mapper; the timer keeps its previous mode.</summary>
        Gentle = -1,
        /// <summary>No `motion` key: mode word untouched.</summary>
        Unchanged = -2,
    }

    /// <summary>
    /// Parameter block of one `DoMorph`/`doScreenElementMorph`. Only the channels
    /// flagged present are touched; everything else keeps its start/target memory,
    /// exactly like the native setup (0x4FE125 guards every channel branch).
    /// </summary>
    internal struct Gh3Morph
    {
        public bool HasPos, PosRelative; public Vector2 Pos;
        public bool HasAlpha; public float Alpha;
        public bool HasScale; public Vector2 Scale;
        public bool HasRot; public float Rot;
        public bool HasRgba; public Color32 Rgba;
        public float Time;         // seconds; omitted = 0 => snap on the next tick
        public Gh3Motion Motion;   // default Unchanged

        public static Gh3Morph Of(float time = 0f, Gh3Motion motion = Gh3Motion.Unchanged) => new() { Time = time, Motion = motion };
        public Gh3Morph WithPos(Vector2 pos, bool relative = false) { HasPos = true; Pos = pos; PosRelative = relative; return this; }
        public Gh3Morph WithAlpha(float alpha) { HasAlpha = true; Alpha = alpha; return this; }
        public Gh3Morph WithScale(float scale) { HasScale = true; Scale = new Vector2(scale, scale); return this; }
        public Gh3Morph WithScale(Vector2 scale) { HasScale = true; Scale = scale; return this; }
        public Gh3Morph WithRot(float degrees) { HasRot = true; Rot = degrees; return this; }
        public Gh3Morph WithRgba(Color32 rgba) { HasRgba = true; Rgba = rgba; return this; }
    }

    /// <summary>
    /// One GH3 ScreenElement (container, sprite or text) with its five morph channels and
    /// the element's single shared millisecond timer. Channel memory layout follows the
    /// native element: current / target / start per channel, one timer per element
    /// (re-armed unconditionally by every morph), dirty flag cleared when the timer is
    /// done and the tick snaps current = target.
    /// </summary>
    internal sealed class Gh3HudElement
    {
        public readonly string Id;
        public readonly Gh3HudElement Parent;
        public readonly int Order;       // construction order: z tie-break
        public bool IsContainer;
        public bool Alive = true;        // DestroyScreenElement

        // Sprite
        public string TextureName;
        public Gh3HudRegion Region;
        public Vector2 Dims;             // logical dims in parent-local units before Scale
        // Text
        public Gh3HudFont Font;
        public readonly char[] Text = new char[24];
        public int TextLength;
        public float FontSpacing;
        public bool Shadow;
        public Vector2 ShadowOffset;
        public Color32 ShadowRgba;

        public Vector2 Just;             // native just space: -1 = left/top, 0 = center, +1 = right/bottom
        public float Z;

        // Channels: current, target, start.
        public Vector2 Pos, PosTarget, PosStart;
        public float Alpha = 1f, AlphaTarget = 1f, AlphaStart = 1f;
        public Vector2 Scale = Vector2.one, ScaleTarget = Vector2.one, ScaleStart = Vector2.one;
        public float Rot, RotTarget, RotStart;   // degrees (native stores radians; lerp is linear either way)
        public Color32 Rgba = new(255, 255, 255, 255), RgbaTarget = new(255, 255, 255, 255), RgbaStart = new(255, 255, 255, 255);

        // Shared timer (sub_55B260 / sub_55B120): integer milliseconds.
        private long _timerStart;
        private int _durationMs;
        private Gh3Motion _mode = Gh3Motion.Linear;
        private bool _dirty;

        public Gh3HudElement(string id, Gh3HudElement parent, int order)
        {
            Id = id; Parent = parent; Order = order;
        }

        public bool MorphDone(long nowMs) => _durationMs == 0 || nowMs - _timerStart >= _durationMs;
        public bool IsAnimating => _dirty;

        /// <summary>Immediate property set (SetScreenElementProps): all three slots of the channel.</summary>
        public void SetPos(Vector2 pos) { Pos = PosTarget = PosStart = pos; }
        public void SetAlpha(float alpha) { Alpha = AlphaTarget = AlphaStart = alpha; }
        public void SetScale(Vector2 scale) { Scale = ScaleTarget = ScaleStart = scale; }
        public void SetScale(float scale) => SetScale(new Vector2(scale, scale));
        public void SetRot(float degrees) { Rot = RotTarget = RotStart = degrees; }
        public void SetRgba(Color32 rgba) { Rgba = RgbaTarget = RgbaStart = rgba; }

        public void SetText(string text)
        {
            TextLength = Math.Min(text.Length, Text.Length);
            for (int i = 0; i < TextLength; i++) Text[i] = text[i];
        }
        /// <summary>method_4FBBA0 with a3 = 0: write the target alpha only and mark dirty; the timer decides when it shows.</summary>
        public void SetTargetAlpha(float alpha) { AlphaTarget = alpha; _dirty = true; }

        /// <summary>sub_55B260 on its own: re-arm the shared timer without naming a channel (a running morph completes at once when seconds == 0).</summary>
        public void ArmTimer(float seconds, long nowMs)
        {
            _durationMs = (int)(seconds * 1000f);
            _timerStart = nowMs;
            if (_durationMs == 0 && _dirty) SnapCurrent();
        }

        /// <summary>
        /// Native DoMorph setup (0x4FE125 + sub_55B2D0/sub_55B260). Every channel setter
        /// guards on `target == new` (asm-verified): only a named channel whose target
        /// changes takes start = current / target = new. The shared timer is re-armed for
        /// every morph; an unrecognised motion (`gentle`) leaves the mode alone.
        /// </summary>
        public void Morph(in Gh3Morph m, long nowMs)
        {
            // Deliberate deviation from the recovered shared setup (0x4FE125): every channel's
            // start is re-snapshotted here, so a channel the morph does not name holds its
            // current value instead of re-lerping from a stale start. The asm of the shared
            // setup and tick does not do this, but the obfuscated vtable+44 wrapper that
            // precedes it was not recoverable, and the stale-start replay produced a visible
            // per-pulse alpha dip that the real game does not show.
            PosStart = Pos; AlphaStart = Alpha; ScaleStart = Scale; RotStart = Rot; RgbaStart = Rgba;
            if (m.HasPos)
            {
                // `relative` adds to the pending target, not to the on-screen point.
                Vector2 target = m.PosRelative ? PosTarget + m.Pos : m.Pos;
                if (target != PosTarget) { PosStart = Pos; PosTarget = target; _dirty = true; }
            }
            if (m.HasAlpha && m.Alpha != AlphaTarget)
            {
                AlphaStart = Alpha; AlphaTarget = m.Alpha; _dirty = true;
            }
            if (m.HasScale && m.Scale != ScaleTarget)
            {
                ScaleStart = Scale; ScaleTarget = m.Scale; _dirty = true;
            }
            if (m.HasRot && m.Rot != RotTarget)
            {
                RotStart = Rot; RotTarget = m.Rot; _dirty = true;
            }
            if (m.HasRgba && !SameRgba(m.Rgba, RgbaTarget))
            {
                RgbaStart = Rgba; RgbaTarget = m.Rgba; _dirty = true;
            }
            if (m.Motion >= Gh3Motion.Linear) _mode = m.Motion;
            _durationMs = (int)(m.Time * 1000f);
            _timerStart = nowMs;
            // Zero duration: the setup tail already copies target -> current.
            if (_durationMs == 0) SnapCurrent();
        }

        private static bool SameRgba(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        /// <summary>Done branch of the tick: current = target for every channel; starts are left as they are.</summary>
        private void SnapCurrent()
        {
            Pos = PosTarget; Alpha = AlphaTarget; Scale = ScaleTarget; Rot = RotTarget; Rgba = RgbaTarget;
            _dirty = false;
        }

        /// <summary>sub_55B010: eased fraction for the shared timer.</summary>
        public float Eased(long nowMs)
        {
            double t = _durationMs == 0 || nowMs - _timerStart >= _durationMs ? 1.0 : (double)(nowMs - _timerStart) / _durationMs;
            switch (_mode)
            {
                case Gh3Motion.EaseIn: return (float)(t * t);
                case Gh3Motion.EaseOut: return (float)(2.0 * t - t * t);
                case Gh3Motion.Smooth: return (float)(-2.0 * t * t * t + 3.0 * t * t);
                default: return (float)t;
            }
        }

        /// <summary>
        /// Per-frame tick (0x4FE510): while the shared timer runs, every channel is lerped
        /// from its stored start, named or not; when done, current = target and the dirty
        /// flag clears.
        /// </summary>
        public void Tick(long nowMs)
        {
            if (!_dirty) return;
            if (MorphDone(nowMs)) { SnapCurrent(); return; }
            float k = Eased(nowMs);
            Pos = PosStart + (PosTarget - PosStart) * k;
            Alpha = AlphaStart + (AlphaTarget - AlphaStart) * k;
            Scale = ScaleStart + (ScaleTarget - ScaleStart) * k;
            Rot = RotStart + (RotTarget - RotStart) * k;
            Rgba = new Color32(
                LerpByte(RgbaStart.r, RgbaTarget.r, k), LerpByte(RgbaStart.g, RgbaTarget.g, k),
                LerpByte(RgbaStart.b, RgbaTarget.b, k), LerpByte(RgbaStart.a, RgbaTarget.a, k));
        }

        // Native lerps the bytes in float and truncates back ((int)(float)).
        private static byte LerpByte(byte a, byte b, float k) => (byte)(int)(a + (b - a) * k);
    }
}
