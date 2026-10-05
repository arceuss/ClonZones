// Integration regressions against the actual controller and script files. See
// HostStubs.cs for the synthetic boundary; this is NOT a Clone Hero runtime test.
using System;
using System.Reflection;
using ClonZones;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

internal static class Program
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _passed, _failed;
    // CH's StarProgress terminal is 7 (.ctor 0x1802E0990), so the counter has states 0..7.
    private const int ChStarStates = 8;
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, InstancePrivate).GetValue(o);
    private static void Require(bool condition, string text) { if (!condition) throw new Exception(text); }
    private static void Near(float a, float b, string text) => Require(Math.Abs(a - b) < 0.001f, $"{text}: {a} != {b}");
    private static void Same(Vector2 a, Vector2 b, string text) { Near(a.x, b.x, text + " x"); Near(a.y, b.y, text + " y"); }
    private static void Same(Color32 a, Color32 b, string text) => Require(a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a, $"{text}: {a} != {b}");
    private static void Test(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { _failed++; Console.WriteLine("FAIL " + name + ": " + (e.InnerException ?? e).Message); }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly BasePlayer Player = new();
        public readonly Gh3HudController Hud;
        public GameManager Gm => Player.gameManager;
        public ObjectPublicAbstractDoBoDoInBoObDoInSiBoUnique Engine => Player.engine;
        public Gh3HudScene Scene => Field<Gh3HudScene>(Hud, "_scene");
        public Gh3HudScheduler Scheduler => Field<Gh3HudScheduler>(Hud, "_sched");
        public Gh3HudMesh Mesh => Field<Gh3HudMesh>(Hud, "_mesh");
        public Gh3HudVisibility Visibility => Field<Gh3HudVisibility>(Hud, "_visibility");
        public Fixture(double songTime = 10, PresentationStyle style = PresentationStyle.Gh3, Action<BasePlayer> initialize = null)
        {
            Time.unscaledTime = 0; Input.RightDown = Input.RightUp = false; Gh3HudBreakHook.Clear();
            GlobalVariables.instance.isPracticeEnabled = false;
            ObjectPublicAbstractSealedBoObObObObObObObObObUnique.field_Public_Static_Object2PublicBoSiInSiDoStSiStStUnique_27 = new();
            LeaderboardsOnlineManager.instance = new();
            Gm.songTime = songTime;
            initialize?.Invoke(Player);
            typeof(Gh3HudController).GetField("_log", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, new MelonLogger.Instance());
            Hud = (Gh3HudController)typeof(Gh3HudController).GetConstructor(InstancePrivate, null,
                new[] { typeof(BasePlayer), typeof(Shader), typeof(Gh3HudAssets), typeof(Shader) }, null).Invoke(new object[] {
                    Player, new Shader(), new Gh3HudAssets(style, null, 0,
                        style == PresentationStyle.Wormod ? WormodHudLayout.ImageNames : Gh3HudAssets.ImageNames,
                        Gh3HudAssets.FontNames, new MelonLogger.Instance()), null });
            Frame(0);
        }
        public void Frame(float dt = 0.016f)
        {
            Time.unscaledDeltaTime = dt; Time.unscaledTime += dt;
            if (!Gm.isPaused) Gm.songTime += dt;
            typeof(Gh3HudController).GetMethod("Update", InstancePrivate).Invoke(Hud, null);
            Input.RightDown = Input.RightUp = false;
        }
        public void Run(float seconds, float dt = 0.016f)
        { for (int i = 0; i < (int)Math.Ceiling(seconds / dt); i++) Frame(dt); }
        public void Streak(int streak)
        {
            Engine.field_Public_Int32_1 = streak;
            Engine.field_Protected_Int32_0 = (Math.Min(streak / 10, 3) + 1) * (Engine.field_Public_Boolean_0 ? 2 : 1);
            if (streak > 0) Engine.prop_Int32_2 += 50;
        }
        public void Sp(float amount, bool active)
        {
            Engine.prop_Single_0 = amount;
            Engine.field_Public_Int64_0 = (long)(amount * Engine.field_Public_Int64_2);
            Engine.field_Public_Boolean_0 = active;
            Engine.field_Protected_Int32_0 = (Math.Min(Engine.field_Public_Int32_1 / 10, 3) + 1) * (active ? 2 : 1);
        }
        public void Dispose() => Hud.Dispose();
    }

    private static void VerifyWormodFcPresentation()
    {
        using var f = new Fixture(10, PresentationStyle.Wormod);
        f.Streak(1); f.Frame();
        Require(f.Scene.Find("dx_fc_hud") == null, "WORMod created the extra FC label");
        Require(f.Scene.Find("dx_fc_hud_glowburst") == null, "WORMod created the extra FC glow");
        Near(f.Scene.Find(WormodHudLayout.StarGlowId + "p1").Alpha, 1f, "WORMod FC star glow");
    }

    private static int Main()
    {
        Test("WORMod uses its star glow without the extra FC label", VerifyWormodFcPresentation);
        Test("GH3 no-fail toggles at constant health and leaderboard mode restores flashing", () => {
            using var f = new Fixture();
            var setting = ObjectPublicAbstractSealedBoObObObObObObObObObUnique.field_Public_Static_Object2PublicBoSiInSiDoStSiStStUnique_27;
            var red = f.Scene.Find("HUD2D_rock_BG_redp1");
            var face = f.Scene.Find("HUD2D_rock_BG_nofailp1");
            f.Engine.field_Protected_Single_0 = .1f; f.Frame(0); f.Frame(.1f);
            Require(red.Rgba.r < 225, "low health did not start flashing");
            setting.prop_Boolean_0 = true; f.Frame(0);
            Near(face.Alpha, 1, "no-fail face hidden");
            foreach (string id in new[] { "BG_green", "BG_yellow", "BG_red", "needle", "lights_green", "lights_yellow", "lights_red" })
                Near(f.Scene.Find("HUD2D_rock_" + id + "p1").Alpha, 0, id + " remains visible");
            foreach (string id in new[] { "body", "lights_all", "tube_1", "tube_5" })
                Near(f.Scene.Find("HUD2D_rock_" + id + "p1").Alpha, 1, id + " was hidden");
            f.Run(.6f);
            Same(red.Rgba, new Color32(225,225,225,255), "cancelled red flash kept running");
            LeaderboardsOnlineManager.instance.field_Private_Boolean_0 = true; f.Frame(0);
            Near(face.Alpha, 0, "leaderboard mode retained no-fail face");
            Near(f.Scene.Find("HUD2D_rock_needlep1").Alpha, 1, "leaderboard mode left needle hidden");
            Near(red.Alpha, 1, "leaderboard mode left background hidden");
            f.Frame(.1f); Require(red.Rgba.r < 225, "normal mode did not restart low-health flashing");
            LeaderboardsOnlineManager.instance.field_Private_Boolean_0 = false; f.Frame(0);
            Near(face.Alpha, 1, "return from leaderboard mode lost the saved no-fail preference");
            f.Gm.songTime -= 2; f.Frame(0);
            Near(f.Scene.Find("HUD2D_rock_BG_nofailp1").Alpha, 1, "seek reset lost no-fail face");
            Near(f.Scene.Find("HUD2D_rock_needlep1").Alpha, 0, "seek reset restored the no-fail needle");
        });
        Test("practice keeps the GH3 no-fail face in leaderboard mode", () => {
            using var f = new Fixture();
            GlobalVariables.instance.isPracticeEnabled = true;
            LeaderboardsOnlineManager.instance.field_Private_Boolean_0 = true; f.Frame(0);
            Near(f.Scene.Find("HUD2D_rock_BG_nofailp1").Alpha, 1, "practice showed a failing meter");
            GlobalVariables.instance.isPracticeEnabled = false; f.Frame(0);
            Near(f.Scene.Find("HUD2D_rock_BG_nofailp1").Alpha, 0, "leaving practice kept the no-fail face");
        });
        Test("WOR keeps its normal meter when no-fail changes", () => {
            using var f = new Fixture(10, PresentationStyle.Wormod);
            f.Engine.field_Protected_Single_0 = .1f; f.Frame(0); f.Frame(.1f);
            var red = f.Scene.Find("HUD2D_rock_BG_redp1");
            byte before = red.Rgba.r;
            ObjectPublicAbstractSealedBoObObObObObObObObObUnique.field_Public_Static_Object2PublicBoSiInSiDoStSiStStUnique_27.prop_Boolean_0 = true;
            f.Frame(0);
            Require(red.Rgba.r == before, "no-fail change restarted WOR's flash");
            Near(f.Scene.Find(WormodHudLayout.NeedleId + "p1").Alpha, 1, "no-fail hid WOR's needle");
            Near(f.Scene.Find("HUD2D_rock_needlep1").Alpha, 0, "no-fail exposed WOR's unused GH3 needle");
            f.Frame(.1f);
            Require(red.Rgba.r != before, "no-fail stopped WOR's flash");
        });
        Test("WORMod low-health flash keeps its phase, pauses, and stops on recovery", () => {
            using var f = new Fixture(10, PresentationStyle.Wormod);
            var red = f.Scene.Find("HUD2D_rock_BG_redp1");
            f.Engine.field_Protected_Single_0 = .125f; f.Frame(0);
            f.Frame(.1f);
            Require(red.Rgba.r > 0 && red.Rgba.r < 255, "inclusive threshold did not start the dark fade");
            f.Frame(.1f); Same(red.Rgba, new Color32(0,0,0,255), "first dark endpoint");
            f.Frame(.1f);
            byte halfway = red.Rgba.r;
            Require(halfway > 0 && halfway < 225, "light fade missing");
            f.Engine.field_Protected_Single_0 = .08f; f.Frame(0);
            Require(red.Rgba.r == halfway, "health fluctuation restarted the flash");
            f.Gm.isPaused = true; f.Run(.5f);
            Require(red.Rgba.r == halfway, "pause advanced the flash");
            f.Gm.isPaused = false; f.Frame(.1f);
            Same(red.Rgba, new Color32(225,225,225,255), "first light endpoint");
            f.Frame(.1f); Require(red.Rgba.r < 225, "second dark fade missing");
            f.Engine.field_Protected_Single_0 = .4f; f.Frame(0); f.Run(.6f);
            Same(red.Rgba, new Color32(225,225,225,255), "recovery left a running or dark flash");
            Near(red.Alpha, 1f, "flash changed background alpha instead of RGB");
        });
        Test("WORMod low-health flash repeats without new health samples", () => {
            using var f = new Fixture(10, PresentationStyle.Wormod);
            var red = f.Scene.Find("HUD2D_rock_BG_redp1");
            f.Engine.field_Protected_Single_0 = .1f; f.Frame(0);
            f.Run(2.2f,.1f);
            byte before = red.Rgba.r; f.Frame(.1f);
            Require(red.Rgba.r != before, "unchanged low health stopped the repeating flash");
            f.Engine.field_Protected_Single_0 = .12501f; f.Frame(0); f.Run(.4f);
            Same(red.Rgba, new Color32(225,225,225,255), "above-threshold recovery did not stop the flash");
        });
        Test("target-only alpha keeps the native timer and resamples only a changed target", () => {
            var glow = new Gh3HudElement("glow", null, 0);
            glow.SetAlpha(0);
            glow.Morph(Gh3Morph.Of(1f,Gh3Motion.EaseOut).WithAlpha(1),1000);
            glow.Tick(1250); Near(glow.Alpha,.4375f,"pulse before cancellation");
            glow.SetTargetAlpha(0);
            Near(glow.Alpha,.4375f,"target-only setter snapped current alpha");
            glow.Tick(1500); Near(glow.Alpha,.109375f,"remaining native timer fade");
            glow.SetTargetAlpha(0);
            glow.Tick(1750); Near(glow.Alpha,.02734375f,"same target restarted interpolation");
            glow.Tick(2000); Near(glow.Alpha,0,"original timer endpoint");
        });
        Test("WORMod pulse cancellation fades the big glow instead of snapping it", () => {
            using var f = new Fixture(10, PresentationStyle.Wormod);
            var glow = f.Scene.Find("HUD2D_rock_glowp1");
            glow.SetAlpha(0);
            glow.Morph(Gh3Morph.Of(1f,Gh3Motion.EaseOut).WithAlpha(1),f.Scheduler.NowMs);
            f.Frame(.25f); float before=glow.Alpha;
            typeof(Gh3HudController).GetMethod("KillPulsateStarPowerBulbs",InstancePrivate).Invoke(f.Hud,null);
            Require(glow.Alpha==before && before>0,"pulse kill snapped the big glow");
            f.Frame(.25f); Require(glow.Alpha>0 && glow.Alpha<before,"remaining fade missing");
            f.Frame(.5f); Near(glow.Alpha,0,"pulse kill did not finish on the old timer");
        });
        Test("idle HUD clock advances with zero scripts", () => {
            using var f = new Fixture();
            Require(f.Scheduler.Count == 0, "fixture must expose the idle-queue case");
            long start = f.Scheduler.NowMs; f.Run(0.5f);
            Require(f.Scheduler.NowMs - start >= 490, "clock stopped when the script list became empty");
        });
        foreach (float dt in new[] { 0.001f, 1f / 60f, 0.05f })
        {
            float step = dt;
            Test($"entrance final return finishes at dt={step}", () => {
                using var f = new Fixture(-1);
                f.Run(2, step);
                Same(f.Scene.Find("HUD2D_rock_containerp1").Pos, Gh3HudLayout.RockPos, "rock final position");
                Same(f.Scene.Find("HUD2D_score_containerp1").Pos, Gh3HudLayout.ScorePos, "score final position");
                Near(f.Scene.Find("HUD2D_rock_containerp1").Rot, 0, "rock settles unrotated");
            });
        }
        Test("miss after 32 completes the outgoing counter morph", () => {
            using var f = new Fixture(); f.Streak(32); f.Frame(); f.Run(1.5f);
            f.Streak(0); f.Frame(); f.Run(2);
            var c = f.Scene.Find("HUD2D_note_containerp1");
            Same(c.Pos, Gh3HudLayout.CounterPos + Gh3HudLayout.OffscreenNoteStreakBarOff, "counter leaves the scorebox");
            Require(!c.IsAnimating, "counter still marked animating after its outgoing duration");
        });
        Test("digit-only async morph finishes with no script waiter", () => {
            using var f = new Fixture(); f.Streak(30); f.Frame(); f.Run(1.5f);
            f.Streak(31); f.Frame(); f.Run(0.5f);
            var d = f.Scene.Find("HUD2D_Note_Streak_Text_1p1");
            Near(d.Alpha, 1, "digit opacity");
            Same(d.Pos, Gh3HudLayout.CounterDigitBase + Gh3HudLayout.CounterDigitStep, "digit at rest");
        });
        Test("new morph samples the old endpoint before replacing its timer", () => {
            var e = new Gh3HudElement("test", null, 0); e.SetAlpha(0);
            e.Morph(Gh3Morph.Of(0.2f).WithAlpha(1), 0);
            e.Tick(100);
            e.Morph(Gh3Morph.Of(0.4f).WithScale(2), 200);
            Near(e.Alpha, 1, "old alpha reaches its endpoint before next setup");
        });
        Test("seek replaces old element clocks instead of rewinding under them", () => {
            using var f = new Fixture(); f.Run(2);
            var old = f.Scene.Find("HUD2D_rock_containerp1");
            old.Morph(Gh3Morph.Of(2).WithScale(3), f.Scheduler.NowMs);
            f.Gm.songTime = 5; f.Frame(); f.Run(0.5f);
            Require(!old.Alive, "old epoch's element survived the clock reset");
            Near(f.Scene.Find("HUD2D_rock_containerp1").Scale.x, 1, "new rock scale");
        });
        Test("ready consumed behind streak banner does not wedge the notification slot", () => {
            using var f = new Fixture(); f.Streak(50); f.Frame();
            Require(f.Scene.Exists("HUD_Note_Streak_Combo1"), "streak banner must occupy the slot");
            f.Sp(0.6f, false); f.Frame();
            f.Sp(0.6f, true); f.Frame(); f.Run(4);
            Require(!Field<bool>(f.Hud, "_starPowerReadyOn"), "ready flag was left set by the early exit");
            f.Sp(0, false); f.Frame(); f.Streak(100); f.Frame();
            Require(f.Scene.Exists("HUD_Note_Streak_Combo1"), "later streak banner is still blocked");
        });
        Test("SP repaint at streak 100 does not announce the milestone twice", () => {
            using var f = new Fixture(); f.Streak(100); f.Frame(); f.Run(4);
            Require(!f.Scene.Exists("HUD_Note_Streak_Combo1"), "first banner must have exited");
            f.Sp(0.6f, true); f.Frame(); f.Run(0.6f);
            Require(!f.Scene.Exists("HUD_Note_Streak_Combo1"), "UpdateNixie synthesized another streak event");
        });
        Test("killing a ready script releases its acquired flag", () => {
            using var f = new Fixture(); f.Sp(0.6f, false); f.Frame();
            Require(Field<bool>(f.Hud, "_starPowerReadyOn"), "ready script must own the slot");
            f.Scheduler.Kill("show_star_power_ready");
            Require(!Field<bool>(f.Hud, "_starPowerReadyOn"), "iterator finally was not run on Kill");
        });
        Test("reset disposes ready iterator before reconstructing scene", () => {
            using var f = new Fixture(); f.Sp(0.6f, false); f.Frame();
            f.Scheduler.Reset();
            Require(!Field<bool>(f.Hud, "_starPowerReadyOn"), "iterator finally was not run on Reset");
        });
        Test("pause freezes animation clock but still enforces visibility", () => {
            using var f = new Fixture(); f.Gm.isPaused = true;
            long now = f.Scheduler.NowMs; int count = f.Visibility.Reassertions;
            f.Frame(0.1f);
            Require(f.Scheduler.NowMs == now, "HUD clock advanced during pause");
            Require(f.Visibility.Reassertions == count + 1, "cached CH leaves were not re-hidden on this paused frame");
        });
        Test("right-click requests immediate discovery without resetting presentation", () => {
            using var f = new Fixture(); f.Gm.isPaused = true;
            var rock = f.Scene.Find("HUD2D_rock_containerp1");
            int count = f.Visibility.Discoveries; Input.RightDown = true; f.Frame(0.016f);
            Require(f.Visibility.Discoveries == count + 1, "no immediate rediscovery request");
            Require(ReferenceEquals(rock, f.Scene.Find("HUD2D_rock_containerp1")), "drag reset rebuilt the GH3 scene");
        });
        Test("same-rect projection invalidation rebuilds the mesh", () => {
            using var f = new Fixture(); f.Gm.isPaused = true;
            int uploads = f.Mesh.Uploads; f.Mesh.ProjectionDirty = true; f.Frame(0.016f);
            Require(f.Mesh.Uploads == uploads + 1, "controller ignored projection-only invalidation");
        });
        Test("engine replacement starts a new presentation epoch", () => {
            using var f = new Fixture();
            var old = f.Scene.Find("HUD2D_rock_containerp1");
            f.Player.engine = new() { Pointer = (IntPtr)2 };
            f.Frame(); Require(!old.Alive, "old engine's scene survives an engine swap");
        });
        Test("nixie switches to the SP variant only after the activation flash's UpdateNixie", () => {
            using var f = new Fixture(); f.Streak(12); f.Frame(); f.Run(0.5f);
            Require(f.Scene.Find("HUD2D_score_nixie_2ap1").Alpha == 1, "2a before activation");
            f.Sp(0.6f, true); f.Frame();          // CH doubles the multiplier at once
            f.Run(0.1f);
            Require(f.Scene.Find("HUD2D_score_nixie_2ap1").Alpha == 1 && f.Scene.Find("HUD2D_score_nixie_4bp1").Alpha == 0,
                "native driver keeps the old nixie until UpdateNixie");
            f.Run(0.3f);                          // 1 gameframe + 0.2 s flash, then UpdateNixie
            Require(f.Scene.Find("HUD2D_score_nixie_4bp1").Alpha == 1 && f.Scene.Find("HUD2D_score_nixie_2ap1").Alpha == 0,
                "4b after the flash");
            f.Sp(0f, false); f.Frame(); f.Frame(); // drain end -> UpdateNixie -> next pass
            Require(f.Scene.Find("HUD2D_score_nixie_2ap1").Alpha == 1, "back to 2a after SP ends");
        });
        foreach (PresentationStyle style in new[] { PresentationStyle.Gh3, PresentationStyle.Wormod })
        Test(style + " full SP fills draw the 64x16 art at the meter scale on big and small bulbs", () => {
            using var f = new Fixture(10, style);
            f.Sp(1f, false); f.Frame();
            // UpdateSPMeter 0x4230F0 replaces the create-time dims scale with an absolute (0.8 * small_bulb_scale, 3).
            var expected = new Vector2(64f * 0.8f * Gh3HudLayout.SmallBulbScale, 16f * 3f);
            for (int i = 1; i <= 6; i++)
            {
                var fill = f.Scene.Find($"HUD2D_rock_tube_{i}p1tube");
                Same(new Vector2(fill.Dims.x * fill.Scale.x, fill.Dims.y * fill.Scale.y), expected, $"bulb {i} fill size");
            }
        });
        Test("the same milestone announces again on a new streak", () => {
            using var f = new Fixture(); f.Streak(50); f.Frame();
            Require(f.Scene.Exists("HUD_Note_Streak_Combo1"), "first banner");
            f.Run(4); f.Streak(0); f.Frame(); f.Run(1);
            f.Streak(50); f.Frame();
            Require(f.Scene.Exists("HUD_Note_Streak_Combo1"), "second banner on the new streak");
        });
        foreach (bool overstrum in new[] { false, true })
        Test("FC label disappears after " + (overstrum ? "an overstrum" : "a missed note"), () => {
            using var f = new Fixture();
            Require(!f.Scene.Exists("dx_fc_hud"), "no label before the first hit");
            f.Streak(1); f.Frame(); f.Run(0.3f);
            var t = f.Scene.Find("dx_fc_hud");
            Same(t.Pos, Gh3HudLayout.FcLabelPos, "label rose into place"); Near(t.Alpha, 1, "label visible");
            if (overstrum) Gh3HudBreakHook.GhostCount++; else Gh3HudBreakHook.MissCount++;
            f.Streak(0); f.Frame(); f.Run(.3f);
            Near(t.Alpha, 0, "combo break did not hide the label");
            Same(t.Pos, Gh3HudLayout.FcLabelHiddenPos, "label back down");
            Require(!f.Scheduler.IsRunning("animate_dx_fc_glowburst"), "lost combo left its glow running");
            f.Streak(5); f.Frame(); f.Run(.5f);
            Near(t.Alpha, 0, "label returned during the broken run");
            Gh3HudBreakHook.Clear(); f.Streak(0); f.Gm.songTime = 0; f.Frame();
            f.Streak(1); f.Frame(); f.Run(.3f);
            Near(f.Scene.Find("dx_fc_hud").Alpha, 1, "fresh run did not restore eligibility");
        });
        Test("FC label stays absent after an early or pre-attachment overstrum", () => {
            using var f = new Fixture();
            Gh3HudBreakHook.GhostCount++; f.Frame();
            f.Streak(1); f.Frame(); f.Run(.3f);
            Require(!f.Scene.Exists("dx_fc_hud"), "early overstrum allowed a label");
            using var g = new Fixture(10, PresentationStyle.Gh3, p => {
                Gh3HudBreakHook.GhostCount = 1;
                p.engine.field_Public_Int32_1 = 40;
            });
            Require(!g.Scene.Exists("dx_fc_hud"), "mid-song attachment forgot the overstrum");
        });
        Test("WORMod needle uses normalized CH health without a second half", () => {
            using var f = new Fixture(10, PresentationStyle.Wormod);
            var needle = f.Scene.Find(WormodHudLayout.NeedleId + "p1");
            var glow = f.Scene.Find(WormodHudLayout.NeedleGlowId + "p1");
            f.Engine.field_Protected_Single_0 = 0.5f; f.Frame();
            Near(needle.Pos.x, -499.5f, "half-health needle x");
            Near(needle.Pos.y, 86.06f, "half-health needle y");
            Near(needle.Scale.x, 0.883f, "half-health needle scale");
            Same(glow.Rgba, WormodHudLayout.NeedleGlowYellow, "half-health needle tint");
            f.Engine.field_Protected_Single_0 = 1f; f.Frame();
            Near(needle.Pos.x, WormodHudLayout.NeedleCurveEnd.x, "full-health needle x");
            Near(needle.Pos.y, WormodHudLayout.NeedleCurveEnd.y, "full-health needle y");
            Near(needle.Scale.x, WormodHudLayout.NeedleScaleEnd, "full-health needle scale");
            Same(glow.Rgba, WormodHudLayout.NeedleGlowGreen, "full-health needle tint");
        });
        Test("WORMod star layers follow CH's count 0..7 and select the real 6/7 art", () => {
            using var f = new Fixture(10, PresentationStyle.Wormod);
            StarProgress progress = f.Gm.starProgress;
            var meter = f.Scene.Find(WormodHudLayout.StarMeterId + "p1");
            var tip = f.Scene.Find(WormodHudLayout.StarTipId + "p1");
            Require(f.Scene.Find("HUD2D_score_star_6p1").TextureName == "WiFi_bar6", "count 6 is not WiFi_bar6");
            Require(f.Scene.Find("HUD2D_score_star_7p1").TextureName == "WiFi_bar7", "count 7 is not WiFi_bar7");
            Require(f.Scene.Find("HUD2D_score_star_5p1").TextureName == "WiFi_bar5", "count 5 art changed");
            float z5 = f.Scene.Find("HUD2D_score_star_5p1").Z, z6 = f.Scene.Find("HUD2D_score_star_6p1").Z;
            float z7 = f.Scene.Find("HUD2D_score_star_7p1").Z;
            Require(z5 < z6 && z6 < z7 && z7 < meter.Z && z7 < tip.Z &&
                z7 < f.Scene.Find(WormodHudLayout.StarGlowId + "p1").Z, "6/7 layers are outside the star band");
            for (int count = 0; count <= 7; count++)
            {
                progress.field_Private_Int32_0 = count;
                progress.field_Private_Single_4 = 0.25f; // stale once count reaches the terminal 7
                f.Frame();
                for (int i = 0; i < ChStarStates; i++)
                    Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, i <= count ? 1f : 0f, $"count {count} layer {i}");
                float width = count == 7 ? WormodHudLayout.StarMeterWidth : 64f;
                Near(meter.Dims.x, width, $"count {count} strip width");
                Near(tip.Pos.x, WormodHudLayout.StarTipOrigin.x + width, $"count {count} tip");
            }
        });
        Test("WORMod stars rebuild on direct jumps and native resets", () => {
            using var f = new Fixture(10, PresentationStyle.Wormod);
            StarProgress progress = f.Gm.starProgress;
            var meter = f.Scene.Find(WormodHudLayout.StarMeterId + "p1");
            progress.field_Private_Int32_0 = 7; progress.field_Private_Single_4 = -1f; f.Frame();
            for (int i = 0; i <= 7; i++) Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, 1f, $"0->7 jump layer {i}");
            Near(meter.Dims.x, WormodHudLayout.StarMeterWidth, "0->7 jump is the full terminal bar");
            progress.field_Private_Int32_0 = 2; progress.field_Private_Single_4 = 0.5f; f.Frame();
            for (int i = 0; i <= 7; i++) Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, i <= 2 ? 1f : 0f, $"7->2 layer {i}");
            Near(meter.Dims.x, 128f, "7->2 strip width");
            progress.field_Private_Int32_0 = 0; progress.field_Private_Single_4 = -1f; f.Frame();
            for (int i = 0; i <= 7; i++) Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, i == 0 ? 1f : 0f, $"native rebuild layer {i}");
            Near(meter.Dims.x, 0f, "pre-update -1 fraction is an empty strip");
        });
        Test("WORMod paused reset reconstructs the star counter before play resumes", () => {
            using var f = new Fixture(10, PresentationStyle.Wormod);
            StarProgress progress = f.Gm.starProgress;
            progress.field_Private_Int32_0 = 3; progress.field_Private_Single_4 = 0.5f; f.Frame();
            f.Gm.isPaused = true; f.Frame();
            f.Gm.songTime = 1; progress.field_Private_Int32_0 = 1; progress.field_Private_Single_4 = 0.25f; f.Frame();
            var meter = f.Scene.Find(WormodHudLayout.StarMeterId + "p1");
            for (int i = 0; i <= 7; i++) Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, i <= 1 ? 1f : 0f, $"paused reset layer {i}");
            Near(meter.Dims.x, 64f, "paused reset kept the layout's full-width meter");
        });
        Test("WORMod stars hide without tearing down the HUD when the widget is gone or disputed", () => {
            using var f = new Fixture(10, PresentationStyle.Wormod);
            StarProgress progress = f.Gm.starProgress;
            var starMeter = f.Scene.Find(WormodHudLayout.StarMeterId + "p1");
            var completion = f.Scene.Find(WormodHudLayout.CompletionMeterId + "p1");
            progress.field_Private_Int32_0 = 4; progress.field_Private_Single_4 = 0.5f; f.Frame();
            f.Gm.scoreManager.starProgress = new StarProgress { Pointer = (IntPtr)7, field_Private_Int32_0 = 6 };
            f.Frame();
            Near(starMeter.Alpha, 0f, "disputed widget still drives the strip");
            for (int i = 0; i <= 7; i++) Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, 0f, $"disputed widget layer {i}");
            Near(completion.Alpha, 1f, "star failure hid completion");
            Require(!f.Hud.GetType().GetField("_disposed", InstancePrivate).GetValue(f.Hud).Equals(true), "star failure tore down the HUD");
            f.Gm.scoreManager.starProgress = progress; f.Frame();
            for (int i = 0; i <= 7; i++) Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, i <= 4 ? 1f : 0f, $"recovered layer {i}");
            progress.m_CachedPtr = IntPtr.Zero; f.Frame();
            Near(starMeter.Alpha, 0f, "destroyed widget still drives the strip");
            Near(completion.Alpha, 1f, "destroyed widget hid completion");
            progress.m_CachedPtr = (IntPtr)1; progress.field_Private_Int32_0 = 8; progress.field_Private_Int32_1 = 9; f.Frame();
            for (int i = 0; i <= 7; i++) Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, 0f, $"count 8 folded onto layer {i}");
            progress.field_Private_Int32_0 = 5; progress.field_Private_Int32_1 = 7; f.Frame();
            for (int i = 0; i <= 7; i++) Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, i <= 5 ? 1f : 0f, $"in-range recovery layer {i}");
            Near(starMeter.Alpha, 1f, "in-range recovery strip");
        });
        Test("WORMod duller dims misses at score zero but only positive-score ghosts", () => {
            using (var miss = new Fixture(10, PresentationStyle.Wormod))
            {
                var glow = miss.Scene.Find(WormodHudLayout.StarGlowId + "p1");
                var meter = miss.Scene.Find(WormodHudLayout.StarMeterId + "p1");
                Gh3HudBreakHook.MissCount++; miss.Frame();
                Near(glow.Alpha, 0f, "score-zero miss glow");
                Same(meter.Rgba, WormodHudLayout.DullerMeterRgba, "score-zero miss meter");
            }
            using (var ghost = new Fixture(10, PresentationStyle.Wormod))
            {
                var glow = ghost.Scene.Find(WormodHudLayout.StarGlowId + "p1");
                var meter = ghost.Scene.Find(WormodHudLayout.StarMeterId + "p1");
                Gh3HudBreakHook.GhostCount++; ghost.Frame();
                Near(glow.Alpha, 1f, "score-zero ghost glow");
                Same(meter.Rgba, new Color32(196, 169, 65, 255), "score-zero ghost meter");
                ghost.Engine.prop_Int32_2 = 50;
                Gh3HudBreakHook.GhostCount++; ghost.Frame();
                Near(glow.Alpha, 0f, "positive-score ghost glow");
                Same(meter.Rgba, WormodHudLayout.DullerMeterRgba, "positive-score ghost meter");
            }
        });
        Test("WORMod invalid star/completion inputs hide independently and recover without restoring duller", () => {
            using var f = new Fixture(10, PresentationStyle.Wormod);
            StarProgress progress = f.Gm.starProgress;
            Gh3HudBreakHook.MissCount++; f.Frame();
            var starMeter = f.Scene.Find(WormodHudLayout.StarMeterId + "p1");
            var starTip = f.Scene.Find(WormodHudLayout.StarTipId + "p1");
            var starGlow = f.Scene.Find(WormodHudLayout.StarGlowId + "p1");
            var completionMeter = f.Scene.Find(WormodHudLayout.CompletionMeterId + "p1");
            var completionTip = f.Scene.Find(WormodHudLayout.CompletionTipId + "p1");
            progress.field_Private_Int32_0 = -1; progress.field_Private_Int32_1 = -1;
            progress.field_Private_Single_4 = float.NaN; f.Frame();
            Near(starMeter.Alpha, 0f, "invalid stars hide strip");
            Near(starTip.Alpha, 0f, "invalid stars hide tip");
            Near(starGlow.Alpha, 0f, "invalid stars hide glow");
            for (int i = 0; i < ChStarStates; i++) Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, 0f, $"invalid stars hide segment {i}");
            Near(completionMeter.Alpha, 1f, "valid completion survives invalid stars");
            progress.field_Private_Int32_0 = 0; progress.field_Private_Int32_1 = 7;
            progress.field_Private_Single_4 = 0f; f.Frame();
            Near(starMeter.Alpha, 1f, "valid stars recover at the same value");
            Near(starTip.Alpha, 1f, "valid star tip recovers");
            for (int i = 0; i < ChStarStates; i++)
                Near(f.Scene.Find($"HUD2D_score_star_{i}p1").Alpha, i == 0 ? 1f : 0f, $"recovered zero-star layer {i}");
            Near(starGlow.Alpha, 0f, "duller remains after star recovery");
            Same(starMeter.Rgba, WormodHudLayout.DullerMeterRgba, "duller colour survives star recovery");
            f.Gm.songLength = double.NaN; f.Frame();
            Near(starMeter.Alpha, 1f, "valid stars survive invalid completion");
            Near(completionMeter.Alpha, 0f, "invalid completion hides strip");
            Near(completionTip.Alpha, 0f, "invalid completion hides tip");
            f.Gm.songLength = 300; f.Frame();
            Near(completionMeter.Alpha, 1f, "valid completion recovers");
            Same(starMeter.Rgba, WormodHudLayout.DullerMeterRgba, "duller colour survives completion recovery");
        });
        Test("WORMod practice keeps full completion/offscreen rock and enters at -1.4 over .2 seconds", () => {
            using var f = new Fixture(-1.4, PresentationStyle.Wormod, p => {
                GlobalVariables.instance.isPracticeEnabled = true;
                p.gameManager.songLength = 300;
                p.gameManager.practiceStartTime = 0;
                p.gameManager.practiceSectionStart = 3;
                p.gameManager.practiceStartTick = 100;
                p.gameManager.practiceEndTick = 200;
            });
            var rock = f.Scene.Find("HUD2D_rock_containerp1");
            var score = f.Scene.Find("HUD2D_score_containerp1");
            var completion = f.Scene.Find(WormodHudLayout.CompletionMeterId + "p1");
            Near(completion.Dims.x, WormodHudLayout.CompletionMeterWidth, "practice completion is full");
            Same(rock.Pos, WormodHudLayout.OffscreenRockPos, "practice rock stays offscreen");
            Same(score.Pos, WormodHudLayout.OffscreenScorePos, "practice score starts offscreen");
            f.Frame(0.1f);
            Same(rock.Pos, WormodHudLayout.OffscreenRockPos, "practice rock remains offscreen during entry");
            Near(score.Pos.x, (WormodHudLayout.OffscreenScorePos.x + WormodHudLayout.ScorePos.x) * 0.5f, "practice half-entry score x");
            Near(score.Pos.y, (WormodHudLayout.OffscreenScorePos.y + WormodHudLayout.ScorePos.y) * 0.5f, "practice half-entry score y");
            f.Frame(0.11f);
            Same(score.Pos, WormodHudLayout.ScorePos, "practice score reaches default after .2 seconds");
            Same(rock.Pos, WormodHudLayout.OffscreenRockPos, "practice rock remains offscreen after entry");
        });
        Test("WORMod practice range changes start a new presentation epoch", () => {
            using var f = new Fixture(5, PresentationStyle.Wormod, p => {
                GlobalVariables.instance.isPracticeEnabled = true;
                p.gameManager.songLength = 300;
                p.gameManager.practiceStartTime = 0;
                p.gameManager.practiceSectionStart = 3;
                p.gameManager.practiceStartTick = 100;
                p.gameManager.practiceEndTick = 200;
            });
            var oldScore = f.Scene.Find("HUD2D_score_containerp1");
            oldScore.Morph(Gh3Morph.Of(2f).WithScale(3f), f.Scheduler.NowMs);
            f.Gm.practiceStartTime = 1;
            f.Gm.practiceSectionStart = 4;
            f.Gm.practiceStartTick = 300;
            f.Gm.practiceEndTick = 500;
            f.Frame();
            Require(!oldScore.Alive, "practice range change kept the old scene epoch");
            Require(!ReferenceEquals(oldScore, f.Scene.Find("HUD2D_score_containerp1")), "practice range change did not rebuild scene");
            Same(f.Scene.Find("HUD2D_rock_containerp1").Pos, WormodHudLayout.OffscreenRockPos, "new practice rock is offscreen");
            Near(f.Scene.Find(WormodHudLayout.CompletionMeterId + "p1").Dims.x, WormodHudLayout.CompletionMeterWidth,
                "new practice epoch keeps full completion");
        });
        Console.WriteLine($"{_passed} runtime regression scenarios passed; {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
