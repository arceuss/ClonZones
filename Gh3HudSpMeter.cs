using UnityEngine;

namespace ClonZones
{
    internal sealed partial class Gh3HudController
    {
        private const float BulbUnit = 16.666667f;   // 0x41855555, PerFrame's UpdateSPMeter argument

        /// <summary>
        /// UpdateSPMeter (0x4230F0), instruction-verified per-bulb writes. Each bulb
        /// consumes one unit of the 0..100 amount in order (boundary inclusive-full). The
        /// visible fill is the `tube` child's scale (0.8 * small_bulb_scale wide, 3 * f
        /// tall, as multipliers of its authored dims) plus the `full` child's alpha f; the
        /// tube child's alpha is never written. Rising edges stamp `old_alpha` = f on the
        /// full child and re-arm both timers at 0 ms (which also ends a running readiness
        /// pulse morph); draining edges only write the alpha target.
        /// </summary>
        private void UpdateSpMeter(float amount)
        {
            bool draining = _gStarPowerPrev > amount;
            _gStarPowerPrev = amount;
            float b = Gh3HudLayout.SmallBulbScale;
            float remaining = amount;
            for (int i = 0; i < 6; i++)
            {
                Gh3HudElement tube = _tubeFill[i], full = _tubeFull[i];
                if (remaining >= BulbUnit)
                {
                    tube.SetScale(new Vector2(0.8f * b, 3f));
                    tube.ArmTimer(0f, _sched.NowMs);
                    full.SetTargetAlpha(1f);
                    full.ArmTimer(0f, _sched.NowMs);
                    _fullOldAlpha[i] = 1f;
                }
                else
                {
                    float f = remaining / BulbUnit;
                    tube.SetScale(new Vector2(0.8f * b, 3f * f));
                    if (draining)
                    {
                        full.SetTargetAlpha(f);
                    }
                    else
                    {
                        tube.ArmTimer(0f, _sched.NowMs);
                        full.SetTargetAlpha(f);
                        full.ArmTimer(0f, _sched.NowMs);
                        _fullOldAlpha[i] = f;
                    }
                }
                remaining = Mathf.Max(0f, remaining - BulbUnit);
            }
        }
    }
}
