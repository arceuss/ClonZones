using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClonZones
{
    internal sealed partial class Gh3HudController
    {
        private Gh3HudElement _wormodNeedle, _wormodNeedleGlow;
        private Gh3HudElement _wormodStarMeter, _wormodStarTip, _wormodStarGlow;
        private Gh3HudElement _wormodCompletionMeter, _wormodCompletionTip;
        private readonly Gh3HudElement[] _wormodStars = new Gh3HudElement[WormodHudLayout.StarIds.Length];
        private static bool _wormodStarRangeWarned;
        private float _wormodRenderedHealth = float.NaN;
        private int _wormodRenderedStars = int.MinValue;
        private float _wormodRenderedStarProgress = float.NaN;
        private float _wormodRenderedCompletion = float.NaN;
        private bool _wormodStarFeatureVisible;
        private bool _wormodCompletionFeatureVisible;
        private bool _wormodDullerActive;
        private static readonly float _wormodNeedleSpanX =
            WormodHudLayout.NeedleCurveEnd.x - WormodHudLayout.NeedleCurveStart.x;
        private static readonly float _wormodNeedleSpanY =
            WormodHudLayout.NeedleCurveEnd.y - WormodHudLayout.NeedleCurveStart.y;
        private Vector2 _wormodNeedlePosition = new Vector2 { x = 0f, y = 0f };
        private Vector2 _wormodNeedleScale = new Vector2 { x = 1f, y = 1f };
        private Vector2 _wormodStarDims = new Vector2 { x = 0f, y = 0f };
        private Vector2 _wormodStarTipPosition = new Vector2 { x = 0f, y = 0f };
        private Vector2 _wormodCompletionDims = new Vector2 { x = 0f, y = 0f };
        private Vector2 _wormodCompletionTipPosition = new Vector2 { x = 0f, y = 0f };

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool PracticeRangeChanged(Gh3HudSnapshot previous, Gh3HudSnapshot current)
        {
            if (PracticeStartChanged(previous.PracticeStartTime, current.PracticeStartTime)) return true;
            return previous.PracticeSectionStart != current.PracticeSectionStart ||
                previous.PracticeStartTick != current.PracticeStartTick ||
                previous.PracticeEndTick != current.PracticeEndTick;
        }

        private static bool PracticeStartChanged(double previous, double current)
        {
            bool previousValid = IsFinite(previous);
            bool currentValid = IsFinite(current);
            return previousValid != currentValid || (previousValid && Math.Abs(previous - current) > 1e-6);
        }

        private void BuildWormodNotifications(Gh3HudElement destroyGroup)
        {
            _spReadyText = null;
            foreach (WormodHudLayout.NotificationDecl declaration in WormodHudLayout.Notifications)
            {
                Gh3HudElement text = _scene.CreateText(
                    declaration.Id + PlayerText, destroyGroup, _assets.Font("text_a6"), declaration.Text,
                    declaration.Pos, WormodHudLayout.JustCenterTop, 80f, 0f,
                    WormodHudLayout.NotificationRgba, new Vector2(declaration.Scale, declaration.Scale));
                if (declaration.Shadow)
                {
                    text.Shadow = true;
                    text.ShadowOffset = declaration.ShadowOffset;
                    text.ShadowRgba = declaration.ShadowRgba;
                }
                if (declaration.Id == "star_power_ready_text") _spReadyText = text;
            }
            if (_spReadyText == null)
                throw new InvalidOperationException("WORMod notification table omitted star_power_ready_text");
        }

        private void BindWormodElements(Dictionary<string, Gh3HudElement> byId)
        {
            _wormodNeedle = byId[WormodHudLayout.NeedleId];
            _wormodNeedleGlow = byId[WormodHudLayout.NeedleGlowId];
            _wormodStarMeter = byId[WormodHudLayout.StarMeterId];
            _wormodStarTip = byId[WormodHudLayout.StarTipId];
            _wormodCompletionMeter = byId[WormodHudLayout.CompletionMeterId];
            _wormodCompletionTip = byId[WormodHudLayout.CompletionTipId];
            _wormodStarGlow = byId[WormodHudLayout.StarGlowId];
            for (int i = 0; i < _wormodStars.Length; i++)
                _wormodStars[i] = byId[WormodHudLayout.StarIds[i]];
            _wormodRenderedHealth = float.NaN;
            _wormodRenderedStars = int.MinValue;
            _wormodRenderedStarProgress = float.NaN;
            _wormodRenderedCompletion = float.NaN;
            _wormodStarFeatureVisible = true;
            _wormodCompletionFeatureVisible = true;
            _wormodDullerActive = false;
        }

        /// <summary>
        /// Port of career_hud_2d.qb's custom needle script. The native BG/lights
        /// path has already run in UpdateRockMeter; this only drives the authored
        /// WOR needle and its first additive glow child.
        /// </summary>
        private void UpdateWormodRockMeter(float currentHealth)
        {
            if (_wormodNeedle == null || currentHealth == _wormodRenderedHealth) return;
            _wormodRenderedHealth = currentHealth;
            // The source QB converts raw [0,2] health with 0.5. Gh3HudStateBridge
            // already exposes that same normalized [0,1] value.
            float health = Math.Clamp(currentHealth, 0f, 1f);
            float c1 = 2f - (4f * WormodHudLayout.NeedleCurve);
            float c2 = (4f * WormodHudLayout.NeedleCurve) - 1f;
            float k = ((c1 * health) * health) + (c2 * health);
            _wormodNeedlePosition.x = WormodHudLayout.NeedleCurveStart.x + (k * _wormodNeedleSpanX);
            _wormodNeedlePosition.y = WormodHudLayout.NeedleCurveStart.y + (k * _wormodNeedleSpanY);
            float scale = ((WormodHudLayout.NeedleScaleEnd - WormodHudLayout.NeedleScaleStart) * k) + WormodHudLayout.NeedleScaleStart;
            _wormodNeedle.SetPos(_wormodNeedlePosition);
            _wormodNeedleScale.x = scale;
            _wormodNeedleScale.y = scale;
            _wormodNeedle.SetScale(_wormodNeedleScale);
            Color32 glow = health >= (0.5f * WormodHudLayout.HealthMediumGood)
                ? WormodHudLayout.NeedleGlowGreen
                : health >= (0.5f * WormodHudLayout.HealthPoorMedium)
                    ? WormodHudLayout.NeedleGlowYellow
                    : WormodHudLayout.NeedleGlowRed;
            _wormodNeedleGlow.SetRgba(glow);
        }

        /// <summary>
        /// The two event call sites for source score-duller are represented by
        /// monotonic CH event counters: every miss, and an unnecessary/ghost note
        /// only after score has become positive. It intentionally does not restore
        /// the glow or meter colour until the native HUD epoch is rebuilt.
        /// </summary>
        private void UpdateWormodDullerEdges()
        {
            bool missed = _snap.MissEvents > _prev.MissEvents;
            bool ghosted = _snap.GhostEvents > _prev.GhostEvents && _snap.Score > 0;
            if (!missed && !ghosted) return;
            _wormodDullerActive = true;
            _changed = true;
            _wormodStarGlow?.SetAlpha(0f);
            _wormodStarMeter?.SetRgba(WormodHudLayout.DullerMeterRgba);
        }

        private void UpdateWormodStarMeter()
        {
            if (_wormodStarMeter == null) return;

            // CH count 0..7 all have art; anything past the last layer is unsupported, not
            // folded back onto a lower numeral.
            bool starValid = _snap.Stars >= 0 && _snap.Stars < _wormodStars.Length &&
                _snap.StarsMax >= 0 && IsFinite(_snap.StarProgressFraction);
            if (!starValid)
            {
                if (_snap.Stars >= _wormodStars.Length && !_wormodStarRangeWarned)
                {
                    _wormodStarRangeWarned = true;
                    _log?.Warning($"[ClonZones] WORMod has star art for 0..{_wormodStars.Length - 1}; CH reported {_snap.Stars}/{_snap.StarsMax}, star counter hidden.");
                }
                if (_wormodStarFeatureVisible)
                {
                    _wormodStarFeatureVisible = false;
                    _wormodRenderedStars = int.MinValue;
                    _wormodRenderedStarProgress = float.NaN;
                    _changed = true;
                    _wormodStarMeter.SetAlpha(0f);
                    _wormodStarTip.SetAlpha(0f);
                    _wormodStarGlow.SetAlpha(0f);
                    for (int i = 0; i < _wormodStars.Length; i++) _wormodStars[i].SetAlpha(0f);
                }
            }
            else
            {
                if (!_wormodStarFeatureVisible)
                {
                    _wormodStarFeatureVisible = true;
                    _changed = true;
                    _wormodStarMeter.SetAlpha(1f);
                    _wormodStarTip.SetAlpha(1f);
                    _wormodStarGlow.SetAlpha(_wormodDullerActive ? 0f : 1f);
                }
                int stars = _snap.Stars;
                if (stars != _wormodRenderedStars)
                {
                    // update_star_meter only ever turns the reached layer on, and a WOR rating
                    // can't drop inside one HUD epoch, so N stars always look like layers 0..N
                    // stacked with N on top. Rebuild exactly that set from CH's count so jumps,
                    // restarts and recovery can't leave a stale higher numeral showing.
                    for (int i = 0; i < _wormodStars.Length; i++)
                        _wormodStars[i].SetAlpha(i <= stars ? 1f : 0f);
                    _wormodRenderedStars = stars;
                    _changed = true;
                }
                float progress = Math.Clamp(_snap.StarProgressFraction, 0f, 1f);
                if (progress != _wormodRenderedStarProgress)
                {
                    _wormodRenderedStarProgress = progress;
                    _wormodStarDims.x = WormodHudLayout.StarMeterWidth * progress;
                    _wormodStarDims.y = WormodHudLayout.StarMeterHeight;
                    _wormodStarMeter.Dims = _wormodStarDims;
                    _wormodStarTipPosition.x = WormodHudLayout.StarTipOrigin.x + (WormodHudLayout.StarMeterWidth * progress);
                    _wormodStarTipPosition.y = WormodHudLayout.StarTipOrigin.y;
                    _wormodStarTip.SetPos(_wormodStarTipPosition);
                    _wormodStarMeter.SetAlpha(1f);
                    _wormodStarTip.SetAlpha(1f);
                    _changed = true;
                }
            }

            bool completionValid = _snap.IsPractice ||
                (_snap.SongLength > 0.0 && IsFinite(_snap.SongLength) && IsFinite(_snap.SongTime));
            if (!completionValid)
            {
                if (_wormodCompletionFeatureVisible)
                {
                    _wormodCompletionFeatureVisible = false;
                    _changed = true;
                    _wormodCompletionMeter.SetAlpha(0f);
                    _wormodCompletionTip.SetAlpha(0f);
                }
                return;
            }

            if (!_wormodCompletionFeatureVisible)
            {
                _wormodCompletionFeatureVisible = true;
                _changed = true;
                _wormodCompletionMeter.SetAlpha(1f);
                _wormodCompletionTip.SetAlpha(1f);
            }
            float completion = _snap.IsPractice
                ? 1f
                : Math.Clamp((float)(_snap.SongTime / _snap.SongLength), 0f, 1f);
            if (completion == _wormodRenderedCompletion) return;
            _wormodRenderedCompletion = completion;
            _wormodCompletionDims.x = WormodHudLayout.CompletionMeterWidth * completion;
            _wormodCompletionDims.y = WormodHudLayout.CompletionMeterHeight;
            _wormodCompletionMeter.Dims = _wormodCompletionDims;
            _wormodCompletionTipPosition.x = WormodHudLayout.CompletionTipOrigin.x + (WormodHudLayout.CompletionMeterWidth * completion);
            _wormodCompletionTipPosition.y = WormodHudLayout.CompletionTipOrigin.y;
            _wormodCompletionTip.SetPos(_wormodCompletionTipPosition);
            _wormodCompletionMeter.SetAlpha(1f);
            _wormodCompletionTip.SetAlpha(1f);
            _changed = true;
        }
    }
}
