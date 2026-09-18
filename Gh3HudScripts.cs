using System.Collections.Generic;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Line-by-line ports of the GH3 PC 1.31 HUD scripts (guitar_hud_2d.q,
    /// guitar_starpower.q, guitar_hud.q) for ordinary single-player. `doScreenElementMorph`
    /// is an async morph; `elem :DoMorph` yields until the element's timer is done; `Wait`
    /// yields seconds or game frames. Omitted `time` snaps, omitted `motion` keeps the
    /// element's previous timer mode, exactly like the native morph setup.
    /// </summary>
    internal sealed partial class Gh3HudController
    {
        private static readonly Color32 White = new(255, 255, 255, 255);

        // guitar_hud_2d.q:557-622 (1P career/quickplay branch).
        private IEnumerator<Gh3Wait> HudMoveNoteScorebar(bool @in, float time)
        {
            Vector2 missOff = new(0f, 60f), easeOff = new(0f, 10f);
            Vector2 countPos = Gh3HudLayout.CounterPos;
            Gh3HudElement c = _noteContainer;
            if (@in)
            {
                c.Morph(Gh3Morph.Of(time, Gh3Motion.EaseOut).WithPos(countPos - missOff), _sched.NowMs);
                yield return Gh3Wait.Seconds(time);
                c.Morph(Gh3Morph.Of(time / 3f, Gh3Motion.EaseIn).WithPos(countPos), _sched.NowMs);
                c.Morph(Gh3Morph.Of(0.1f, Gh3Motion.EaseOut).WithPos(easeOff, relative: true), _sched.NowMs);
                yield return Gh3Wait.Morph(c);
                c.Morph(Gh3Morph.Of(0.1f, Gh3Motion.EaseIn).WithPos(easeOff * -1f, relative: true), _sched.NowMs);
                yield return Gh3Wait.Morph(c);
            }
            else
            {
                c.Morph(Gh3Morph.Of(time / 2f, Gh3Motion.EaseOut).WithPos(countPos - missOff), _sched.NowMs);
                yield return Gh3Wait.Seconds(time);
                c.Morph(Gh3Morph.Of(time).WithPos(countPos + Gh3HudLayout.OffscreenNoteStreakBarOff), _sched.NowMs);
            }
        }

        // guitar_hud_2d.q:1047-1057: drop 10 px invisible (snap), then slide back over 0.1 s.
        private IEnumerator<Gh3Wait> HudFlipNoteStreakNum(int dial)
        {
            Gh3HudElement d = _digits[dial - 1];
            Vector2 basePos = d.Pos;
            d.Morph(Gh3Morph.Of().WithPos(basePos + new Vector2(0f, 10f)).WithAlpha(0f), _sched.NowMs);
            d.Morph(Gh3Morph.Of(0.1f).WithPos(_digitInitialPos[dial - 1]).WithAlpha(1f), _sched.NowMs);
            yield break;
        }

        // guitar_hud_2d.q:751-879, single player.
        private IEnumerator<Gh3Wait> HudShowNoteStreakCombo(int combo)
        {
            while (_starPowerReadyOn) yield return Gh3Wait.GameFrames(1);
            if (_scene.Exists(StreakContainerId)) yield break;
            Gh3HudElement container = _scene.CreateContainer(StreakContainerId, _hudDestroyGroup, Vector2.zero);
            const float baseScale = 1f, s = 0.8f;
            Vector2 pos = Gh3HudLayout.StreakPos;
            // GH_SFX_Note_Streak_SinglePlayer would play here; no cue asset is shipped.
            int len = FormatInt(combo, _scoreBuffer);
            string text = new string(_scoreBuffer, 0, len) + " Note Streak!";
            Gh3HudElement id = _scene.CreateText("note_streak_alert_1", container, Gh3HudAssets.Font("text_a6"), text, pos,
                Gh3HudLayout.JustCenterTop, 50f, 0f, new Color32(223, 223, 223, 255), new Vector2(baseScale * 3f, baseScale * 3f));
            id.Shadow = true; id.ShadowOffset = new Vector2(2f, 2f); id.ShadowRgba = new Color32(0, 0, 0, 255);
            id.Morph(Gh3Morph.Of(0.2f, Gh3Motion.EaseIn).WithScale(baseScale).WithAlpha(1f), _sched.NowMs);
            yield return Gh3Wait.Morph(id);
            if (!id.Alive) { _scene.Destroy(container); yield break; }
            _sched.Spawn("hud_glowburst_alert", HudGlowburstAlert());
            Color32 color0 = new(245, 255, 120, 255), color1 = new(245, 255, 160, 255);
            var pulses = new (float scale, float time, Color32 rgba, float rot, Gh3Motion motion)[]
            {
                (baseScale + s, 0.4f, color1, 3f, Gh3Motion.EaseOut),
                (baseScale, 0.4f, color0, 2f, Gh3Motion.EaseIn),
                (baseScale + s / 1.5f, 0.3f, color1, -2f, Gh3Motion.EaseOut),
                (baseScale, 0.3f, color0, -1f, Gh3Motion.EaseIn),
                (baseScale + s / 2f, 0.2f, color1, 2f, Gh3Motion.EaseOut),
                (baseScale, 0.2f, color0, 1f, Gh3Motion.EaseIn),
                (baseScale + s / 2.5f, 0.1f, color1, -1f, Gh3Motion.EaseOut),
                (baseScale, 0.1f, color0, 1f, Gh3Motion.EaseIn),
            };
            foreach (var p in pulses)
            {
                if (!id.Alive) break;
                id.Morph(Gh3Morph.Of(p.time, p.motion).WithScale(p.scale).WithRgba(p.rgba).WithRot(p.rot), _sched.NowMs);
                yield return Gh3Wait.Morph(id);
            }
            if (id.Alive)
            {
                id.Morph(Gh3Morph.Of(0f, Gh3Motion.Gentle).WithRot(0f).WithScale(baseScale), _sched.NowMs);
                yield return Gh3Wait.Morph(id);
            }
            if (id.Alive)
            {
                id.Morph(Gh3Morph.Of(0.35000002f, Gh3Motion.EaseIn).WithPos(pos - new Vector2(0f, 230f)).WithScale(baseScale * 0.8f), _sched.NowMs);
                yield return Gh3Wait.Morph(id);
            }
            _scene.Destroy(container);
        }

        // guitar_hud_2d.q:992-1045, ordinary 1P.
        private IEnumerator<Gh3Wait> HudGlowburstAlert()
        {
            const string glowId = "star_power_ready_glow_1";
            var old = _scene.Find(glowId);
            if (old != null) _scene.Destroy(old);
            Vector2 baseScale = new(15f, 1f), scale2 = new(20f, 5f), scale3 = new(15f, 0.5f), scale4 = new(80f, 0f);
            Gh3HudElement glow = _scene.CreateSprite(glowId, _hudDestroyGroup, "Char_Select_Hilite1", Gh3HudLayout.GlowburstPos,
                Gh3HudLayout.JustCenterCenter, 50f, 1f, new Color32(245, 255, 200, 255));
            glow.SetScale(baseScale);
            glow.Morph(Gh3Morph.Of(0.1f, Gh3Motion.EaseOut).WithScale(scale2).WithAlpha(0.5f), _sched.NowMs);
            yield return Gh3Wait.Morph(glow);
            if (!glow.Alive) yield break;
            glow.Morph(Gh3Morph.Of(0.1f, Gh3Motion.EaseOut).WithScale(scale3).WithAlpha(0.5f).WithRgba(new Color32(245, 255, 160, 255)), _sched.NowMs);
            yield return Gh3Wait.Morph(glow);
            if (!glow.Alive) yield break;
            glow.Morph(Gh3Morph.Of(0.8f, Gh3Motion.EaseIn).WithScale(scale4).WithAlpha(0f), _sched.NowMs);
            yield return Gh3Wait.Morph(glow);
            if (glow.Alive) _scene.Destroy(glow);
        }

        // guitar_starpower.q:319-402, ordinary 1P.
        private IEnumerator<Gh3Wait> ShowStarPowerReady()
        {
            // Star_Power_Ready_SFX would play here; no cue asset is shipped.
            _sched.Spawn("rock_meter_star_power_on", RockMeterStarPowerOn());
            while (_scene.Exists(StreakContainerId)) yield return Gh3Wait.GameFrames(1);
            if (_starPowerReadyOn) yield break;
            _starPowerReadyOn = true;
            if (_starPowerUsed) yield break;   // early return leaves the flag set, exactly like the script
            Gh3HudElement id = _spReadyText;
            Vector2 originalPos = Gh3HudLayout.SpReadyPos;
            const float baseScale = 1.2f, scaleBigMult = 1.5f;
            id.Morph(Gh3Morph.Of().WithPos(originalPos).WithScale(4f).WithRgba(new Color32(190, 225, 255, 250)).WithAlpha(0f).WithRot(3f), _sched.NowMs);
            _sched.Spawn("hud_lightning_alert", HudLightningAlert(id));
            id.Morph(Gh3Morph.Of(0.3f, Gh3Motion.EaseIn).WithPos(originalPos).WithScale(baseScale).WithAlpha(1f).WithRot(-3f), _sched.NowMs);
            yield return Gh3Wait.Morph(id);
            id.Morph(Gh3Morph.Of(0.3f, Gh3Motion.EaseOut).WithPos(originalPos).WithScale(baseScale * scaleBigMult).WithRot(4f), _sched.NowMs);
            yield return Gh3Wait.Morph(id);
            id.Morph(Gh3Morph.Of(0.3f, Gh3Motion.EaseIn).WithPos(originalPos).WithScale(baseScale).WithRot(-5f).WithRgba(new Color32(145, 215, 235, 250)), _sched.NowMs);
            yield return Gh3Wait.Morph(id);
            float rotation = 10f;
            for (int i = 0; i < 12; i++)
            {
                rotation *= -0.7f;
                id.Morph(Gh3Morph.Of(0.08f, Gh3Motion.EaseOut).WithPos(originalPos).WithRot(rotation).WithAlpha(1f), _sched.NowMs);
                yield return Gh3Wait.Morph(id);
            }
            id.Morph(Gh3Morph.Of(0f, Gh3Motion.EaseOut).WithPos(originalPos).WithRot(0f), _sched.NowMs);
            yield return Gh3Wait.Morph(id);
            id.Morph(Gh3Morph.Of(0.3f, Gh3Motion.EaseIn).WithPos(originalPos - new Vector2(0f, 230f)).WithScale(baseScale * 0.5f).WithAlpha(0f), _sched.NowMs);
            yield return Gh3Wait.Morph(id);
            _starPowerReadyOn = false;
        }

        // guitar_hud_2d.q:881-990.
        private IEnumerator<Gh3Wait> HudLightningAlert(Gh3HudElement alert)
        {
            if (alert == null || !alert.Alive) yield break;
            Vector2 lightningPos = alert.Pos - new Vector2(0f, 20f);
            const float lightningTime = 0.2f;
            var bolts = new Gh3HudElement[4];
            int[] numbers = { 1, 3, 5, 7 };
            for (int i = 0; i < 4; i++)
            {
                string boltId = $"HUD_lightning_0{numbers[i]}_1";
                var old = _scene.Find(boltId);
                if (old != null) _scene.Destroy(old);
                bolts[i] = _scene.CreateSprite(boltId, _hudDestroyGroup, $"HUD_lightning_0{numbers[i]}", lightningPos, Gh3HudLayout.JustCenterTop,
                    45f, 0f, White, 0f, Gh3HudLayout.LightningDims);
            }
            bolts[0].Morph(Gh3Morph.Of(lightningTime).WithAlpha(1f), _sched.NowMs);
            yield return Gh3Wait.Seconds(lightningTime);
            for (int i = 0; i < 4; i++)
            {
                if (!bolts[i].Alive) continue;
                bolts[i].Morph(Gh3Morph.Of(lightningTime).WithAlpha(0f), _sched.NowMs);
                if (i + 1 < 4 && bolts[i + 1].Alive) bolts[i + 1].Morph(Gh3Morph.Of(lightningTime).WithAlpha(1f), _sched.NowMs);
                yield return Gh3Wait.Seconds(lightningTime);
            }
            for (int i = 0; i < 4; i++) if (bolts[i].Alive) _scene.Destroy(bolts[i]);
        }

        // guitar_hud_2d.q:535-555.
        private IEnumerator<Gh3Wait> HudActivatedStarPowerSpawned(float time)
        {
            yield return Gh3Wait.GameFrames(1);
            KillPulsateStarPowerBulbs();
            _scoreFlash.Morph(Gh3Morph.Of(time).WithAlpha(1f).WithScale(5f), _sched.NowMs);
            yield return Gh3Wait.Seconds(time);
            _scoreFlash.Morph(Gh3Morph.Of(time / 2f).WithAlpha(0f).WithScale(1f), _sched.NowMs);
            UpdateNixie();
        }

        // guitar_hud_2d.q:339-376.
        private IEnumerator<Gh3Wait> RockMeterStarPowerOn()
        {
            _sched.Spawn("rock_back_and_forth_star_meter", RockBackAndForthStarMeter());
            _sched.Spawn("pulsate_all_star_power_bulbs", PulsateAllStarPowerBulbs(), PulseScriptId);
            for (int i = 0; i < 6; i++)
            {
                if (_tubeMorph[i]) _tube[i].Morph(Gh3Morph.Of(0.4f).WithPos(_tubeFinal[i]), _sched.NowMs);
                _scene.SetTexture(_tubeFill[i], "HUD_rock_tube_glow_fill_b");
                if (_tubeMorph[i]) _tubeFill[i].Morph(Gh3Morph.Of(0.4f).WithPos(_fillFinal[i]), _sched.NowMs);
                _scene.SetTexture(_tubeFull[i], "HUD_rock_tube_glow_full_b");
                if (_tubeMorph[i])
                {
                    _tubeFull[i].Morph(Gh3Morph.Of(0.4f).WithPos(_tubeFinal[i]), _sched.NowMs);
                    yield return Gh3Wait.Seconds(0.2f);
                }
            }
        }

        // guitar_hud_2d.q:448-485 (1P career/quickplay: rock container, up and down).
        private IEnumerator<Gh3Wait> RockBackAndForthStarMeter()
        {
            Gh3HudElement c = _rockContainer;
            Vector2 pos = c.Pos;
            const float t = 0.15f;
            c.Morph(Gh3Morph.Of(t, Gh3Motion.EaseIn).WithPos(pos - new Vector2(0f, 50f)).WithScale(1.5f).WithRot(10f), _sched.NowMs);
            yield return Gh3Wait.Seconds(t);
            c.Morph(Gh3Morph.Of(t, Gh3Motion.EaseIn).WithPos(pos + new Vector2(0f, 75f)).WithScale(0.5f).WithRot(-15f), _sched.NowMs);
            yield return Gh3Wait.Seconds(t);
            c.Morph(Gh3Morph.Of(t).WithPos(pos).WithScale(1f).WithRot(0f), _sched.NowMs);
        }

        // guitar_hud_2d.q:424-446.
        private IEnumerator<Gh3Wait> PulsateAllStarPowerBulbs()
        {
            for (int i = 0; i < 6; i++)
                _sched.Spawn("pulsate_star_power_bulb", PulsateStarPowerBulb(i), PulseScriptId);
            _sched.Spawn("pulsate_big_glow", PulsateBigGlow(), PulseScriptId);
            yield break;
        }

        /// <summary>Random(@ 0.1 @*2 0.5): 0.1 with weight 1, 0.5 with weight 2.</summary>
        private float RandomAlphaTime() => _random.Next(3) == 0 ? 0.1f : 0.5f;

        // guitar_hud_2d.q:384-405.
        private IEnumerator<Gh3Wait> PulsateStarPowerBulb(int bulb)
        {
            while (true)
            {
                float alphaTime = RandomAlphaTime();
                _tubeFill[bulb].Morph(Gh3Morph.Of(alphaTime, Gh3Motion.EaseIn).WithAlpha(0.3f), _sched.NowMs);
                _tubeFull[bulb].Morph(Gh3Morph.Of(alphaTime, Gh3Motion.EaseIn).WithAlpha(0.3f), _sched.NowMs);
                yield return Gh3Wait.Seconds(alphaTime);
                alphaTime = RandomAlphaTime();
                _tubeFill[bulb].Morph(Gh3Morph.Of(alphaTime, Gh3Motion.EaseOut).WithAlpha(_fillOldAlpha[bulb]), _sched.NowMs);
                _tubeFull[bulb].Morph(Gh3Morph.Of(alphaTime, Gh3Motion.EaseOut).WithAlpha(_fullOldAlpha[bulb]), _sched.NowMs);
                yield return Gh3Wait.Seconds(alphaTime);
            }
        }

        // guitar_hud_2d.q:407-422.
        private IEnumerator<Gh3Wait> PulsateBigGlow()
        {
            Gh3HudElement g = _rockGlow;
            while (true)
            {
                g.Morph(Gh3Morph.Of(1f, Gh3Motion.EaseIn).WithAlpha(0f).WithRgba(new Color32(95, 205, 255, 255)), _sched.NowMs);
                yield return Gh3Wait.Morph(g);
                g.Morph(Gh3Morph.Of(1f, Gh3Motion.EaseOut).WithAlpha(1f).WithRgba(White), _sched.NowMs);
                yield return Gh3Wait.Morph(g);
            }
        }

        // guitar_hud_2d.q:378-382 + native KillPulsateStarPowerBulbs (0x42C480): restore fill/full alphas, glow off.
        private void KillPulsateStarPowerBulbs()
        {
            _sched.KillId(PulseScriptId);
            for (int i = 0; i < 6; i++)
            {
                _tubeFill[i].SetAlpha(_fillOldAlpha[i]);
                _tubeFull[i].SetAlpha(_fullOldAlpha[i]);
            }
            _rockGlow.SetAlpha(0f);
        }

        // guitar_hud_2d.q:487-528.
        private IEnumerator<Gh3Wait> RockMeterStarPowerOff()
        {
            for (int j = 5; j >= 0; j--)
            {
                if (_tubeMorph[j])
                {
                    _tube[j].Morph(Gh3Morph.Of(0.1f).WithPos(_tubeFinal[j] + _tubeFinal[j] * 0.1f), _sched.NowMs);
                    yield return Gh3Wait.Seconds(0.1f);
                    _tube[j].Morph(Gh3Morph.Of(0.4f).WithPos(_tubeInitial[j]), _sched.NowMs);
                    yield return Gh3Wait.Seconds(0.1f);
                }
                _scene.SetTexture(_tubeFill[j], "HUD_rock_tube_glow_fill");
                if (_tubeMorph[j]) _tubeFill[j].SetPos(_fillInitial[j]);
                _scene.SetTexture(_tubeFull[j], "HUD_rock_tube_glow_full");
                if (_tubeMorph[j]) _tubeFull[j].SetPos(_tubeInitial[j]);
            }
        }

        // guitar_hud_2d.q:626-671 (1P branch) and 702-730.
        private IEnumerator<Gh3Wait> HudFlashRedBg()
        {
            if (_flashRedGoing) yield break;
            _flashRedGoing = true;
            const float time = 0.2f;
            Color32 dark = new(0, 0, 0, 255), light = new(225, 225, 225, 255);
            Gh3HudElement bg = _bgRed;
            while (true)
            {
                bg.Morph(Gh3Morph.Of(time).WithRgba(dark), _sched.NowMs);
                yield return Gh3Wait.Seconds(time);
                bg.Morph(Gh3Morph.Of(time).WithRgba(light), _sched.NowMs);
                yield return Gh3Wait.Seconds(time);
                bg.Morph(Gh3Morph.Of(time).WithRgba(dark), _sched.NowMs);
                yield return Gh3Wait.Seconds(time);
                bg.Morph(Gh3Morph.Of(time).WithRgba(light), _sched.NowMs);
                yield return Gh3Wait.Seconds(time * 2.5f);
            }
        }

        private void HudFlashRedBgKill()
        {
            if (!_flashRedGoing) return;
            _bgRed.SetRgba(new Color32(225, 225, 225, 255));
            _sched.Kill("hud_flash_red_bg_p1");
            _flashRedGoing = false;
        }
    }
}
