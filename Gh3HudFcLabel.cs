using System.Collections.Generic;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Full-combo label, reimplemented from GH3 Deluxe's dx_fc_hud.q / guitar_events.q
    /// with PFC support: on the first hit "PFC" rises in with a looping Char_Select_Hilite1
    /// glow; a combo break that CH does not count as a missed note (overstrum / ghost
    /// input) turns it into "FC"; a missed note sends it back down and it never returns
    /// this run (Deluxe's dont_create_fc_hud). Positions are this layout's adaptation of
    /// the Deluxe anchor; colours, timings and the glow loop are Deluxe's.
    /// </summary>
    internal sealed partial class Gh3HudController
    {
        private enum FcState { Waiting, Perfect, Full, Lost }

        private FcState _fcState;
        private bool _fcGhosted;       // a no-miss combo break happened before the label existed
        private Gh3HudElement _fcText, _fcGlow;

        private const string FcTextId = "dx_fc_hud", FcGlowId = "dx_fc_hud_glowburst";

        /// <summary>Called from the native updater after the snapshot is current; needs _prev for edges.</summary>
        private void RunFcLabel()
        {
            if (_synchronizeState)
            {
                // Hydrating mid-song: misses so far are known, ghosts are not.
                _fcGhosted = false;
                if (_snap.MissedNotes > 0) { _fcState = FcState.Lost; return; }
                if (_snap.Streak > 0) { CreateFcLabel(FcState.Perfect); _fcText.SetPos(Gh3HudLayout.FcLabelPos); _fcText.SetAlpha(1f); }
                return;
            }
            bool missed = _snap.MissedNotes > _prev.MissedNotes;
            bool broke = _snap.Streak == 0 && _prev.Streak > 0;
            if (missed)
            {
                if (_fcState == FcState.Perfect || _fcState == FcState.Full)
                    _sched.Spawn("dx_fc_hud_go_away", FcGoAway());
                _fcState = FcState.Lost;
                return;
            }
            if (broke)
            {
                // Overstrum/ghost: CH breaks the combo without counting a missed note.
                if (_fcState == FcState.Perfect) { _fcState = FcState.Full; _fcText.SetText("FC"); _changed = true; }
                else if (_fcState == FcState.Waiting) _fcGhosted = true;
                return;
            }
            if (_fcState == FcState.Waiting && _snap.Streak > _prev.Streak)
            {
                CreateFcLabel(_fcGhosted ? FcState.Full : FcState.Perfect);
                _fcText.Morph(Gh3Morph.Of(0.2f, Gh3Motion.Linear).WithPos(Gh3HudLayout.FcLabelPos).WithAlpha(1f), _sched.NowMs);
                _sched.Spawn("animate_dx_fc_glowburst", FcGlowburst());
                _changed = true;
            }
        }

        private void CreateFcLabel(FcState state)
        {
            _fcState = state;
            _fcText = _scene.CreateText(FcTextId, _hudDestroyGroup, Gh3HudAssets.Font("text_a6"), state == FcState.Full ? "FC" : "PFC",
                Gh3HudLayout.FcLabelHiddenPos, Gh3HudLayout.JustCenterTop, 2f, 0f, Gh3HudLayout.FcLabelRgba, Vector2.one);
            _fcText.Shadow = true; _fcText.ShadowOffset = new Vector2(2f, 2f); _fcText.ShadowRgba = Gh3HudLayout.FcLabelShadowRgba;
            // Deluxe parents the 64x64 glow to the text; centred behind the label here.
            _fcGlow = _scene.CreateSprite(FcGlowId, _fcText, "Char_Select_Hilite1", new Vector2(0f, 24f), Gh3HudLayout.JustCenterCenter,
                1f, 0f, Gh3HudLayout.FcGlowRgba);
        }

        // dx_fc_hud_watchdog with fc_hud_go_away = 1.
        private IEnumerator<Gh3Wait> FcGoAway()
        {
            _fcText.Morph(Gh3Morph.Of(0.2f, Gh3Motion.Linear).WithPos(Gh3HudLayout.FcLabelHiddenPos).WithAlpha(0f), _sched.NowMs);
            yield return Gh3Wait.Morph(_fcText);
            _sched.Kill("animate_dx_fc_glowburst");
        }

        // animate_dx_fc_glowburst: 0.2/0.4/0.6/0.8 up with warming tints, hold, back down, repeat.
        private IEnumerator<Gh3Wait> FcGlowburst()
        {
            Gh3HudElement g = _fcGlow;
            Color32[] pulse = Gh3HudLayout.FcGlowPulse;
            while (g.Alive)
            {
                for (int i = 0; i < 4; i++)
                {
                    g.Morph(Gh3Morph.Of(0.2f, Gh3Motion.Linear).WithAlpha(0.2f * (i + 1)).WithRgba(pulse[i]), _sched.NowMs);
                    yield return Gh3Wait.Morph(g);
                }
                g.Morph(Gh3Morph.Of(0.3f, Gh3Motion.Linear).WithAlpha(0.8f), _sched.NowMs);
                yield return Gh3Wait.Morph(g);
                for (int i = 2; i >= 0; i--)
                {
                    g.Morph(Gh3Morph.Of(0.2f, Gh3Motion.Linear).WithAlpha(0.2f * (i + 1)).WithRgba(pulse[i]), _sched.NowMs);
                    yield return Gh3Wait.Morph(g);
                }
                g.Morph(Gh3Morph.Of(0.2f, Gh3Motion.Linear).WithAlpha(0f).WithRgba(Gh3HudLayout.FcGlowRgba), _sched.NowMs);
                yield return Gh3Wait.Morph(g);
            }
        }
    }
}
