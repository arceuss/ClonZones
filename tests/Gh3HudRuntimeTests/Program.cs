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
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, InstancePrivate).GetValue(o);
    private static void Require(bool condition, string text) { if (!condition) throw new Exception(text); }
    private static void Near(float a, float b, string text) => Require(Math.Abs(a - b) < 0.001f, $"{text}: {a} != {b}");
    private static void Same(Vector2 a, Vector2 b, string text) { Near(a.x, b.x, text + " x"); Near(a.y, b.y, text + " y"); }
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
        public Fixture(double songTime = 10)
        {
            Time.unscaledTime = 0; Input.RightDown = Input.RightUp = false;
            Gm.songTime = songTime;
            typeof(Gh3HudController).GetField("_log", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, new MelonLogger.Instance());
            Hud = (Gh3HudController)typeof(Gh3HudController).GetConstructor(InstancePrivate, null,
                new[] { typeof(BasePlayer), typeof(Shader) }, null).Invoke(new object[] { Player, new Shader() });
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

    private static int Main()
    {
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
        Test("the same milestone announces again on a new streak", () => {
            using var f = new Fixture(); f.Streak(50); f.Frame();
            Require(f.Scene.Exists("HUD_Note_Streak_Combo1"), "first banner");
            f.Run(4); f.Streak(0); f.Frame(); f.Run(1);
            f.Streak(50); f.Frame();
            Require(f.Scene.Exists("HUD_Note_Streak_Combo1"), "second banner on the new streak");
        });
        Console.WriteLine($"{_passed} runtime regression scenarios passed; {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
