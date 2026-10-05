// Deterministic checks for the pure GH3 HUD logic. Expected values come from the
// native/script findings (gh3.exe 1.31 database, GH3 PC 1.31 scripts, font containers),
// never from the code under test. Run: dotnet run -c Release --project tests/Gh3HudTests
using System;
using System.Collections.Generic;
using System.IO;
using ClonZones;
using UnityEngine;

internal static class Program
{
    private static int _failed, _passed;

    private static void Check(bool ok, string what)
    {
        if (ok) _passed++; else { _failed++; Console.WriteLine("FAIL " + what); }
    }

    private static void Near(float actual, float expected, string what, float tol = 1e-4f)
        => Check(Math.Abs(actual - expected) <= tol, $"{what}: expected {expected}, got {actual}");

    private sealed class Sink : IGh3HudQuadSink
    {
        public readonly List<(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color32 color, Gh3HudBlend blend)> Quads = new();
        public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float u0, float v0, float u1, float v1, Color32 color, Gh3HudBlend blend = Gh3HudBlend.Alpha)
            => Quads.Add((a, b, c, d, color, blend));
    }

    private static Gh3HudRegion Region(string name) => new(name.Contains("nixie") ? 128 : 64, name.Contains("nixie") ? 128 : 64, 0f, 0f, 1f, 1f);

    private static int Main()
    {
        Easing();
        MorphSemantics();
        Scheduler();
        Rules();
        Fonts();
        SceneCompose();
        Layout();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // FINDINGS §5 worked table: scale 1 -> 1.8 over 0.4 s.
    private static void Easing()
    {
        (Gh3Motion motion, float[] expected)[] rows =
        {
            (Gh3Motion.EaseOut, new[] { 1.35f, 1.60f, 1.75f, 1.80f }),
            (Gh3Motion.EaseIn, new[] { 1.05f, 1.20f, 1.45f, 1.80f }),
            (Gh3Motion.Smooth, new[] { 1.125f, 1.40f, 1.675f, 1.80f }),
            (Gh3Motion.Linear, new[] { 1.20f, 1.40f, 1.60f, 1.80f }),
        };
        foreach (var (motion, expected) in rows)
        {
            var e = new Gh3HudElement("e", null, 0);
            e.Morph(Gh3Morph.Of(0.4f, motion).WithScale(1.8f), 0);
            for (int i = 0; i < 4; i++)
            {
                e.Tick(100 * (i + 1));
                Near(e.Scale.x, expected[i], $"{motion} at {100 * (i + 1)} ms");
            }
            e.Tick(401);
            Near(e.Scale.x, 1.8f, $"{motion} snaps exactly at completion");
        }
    }

    private static void MorphSemantics()
    {
        // Omitted time = zero duration = snap.
        var e = new Gh3HudElement("e", null, 0);
        e.SetAlpha(0f);
        e.Morph(Gh3Morph.Of().WithAlpha(1f), 5);
        Near(e.Alpha, 1f, "omitted time snaps alpha");
        Check(!e.IsAnimating, "zero-duration morph is done immediately");

        // `gentle` is not a mapped motion: the timer keeps the previous mode (ease_in here).
        e = new Gh3HudElement("e", null, 0);
        e.Morph(Gh3Morph.Of(0.1f, Gh3Motion.EaseIn).WithScale(2f), 0);
        e.Tick(200);
        e.Morph(Gh3Morph.Of(0.4f, Gh3Motion.Gentle).WithScale(3f), 200);
        e.Tick(400);
        Near(e.Scale.x, 2f + 1f * 0.25f, "gentle keeps ease_in (t^2 at t=0.5)");
        // No motion key at all: also unchanged.
        e.Tick(600);
        e.Morph(Gh3Morph.Of(0.4f).WithScale(4f), 600);
        e.Tick(800);
        Near(e.Scale.x, 3f + 1f * 0.25f, "omitted motion keeps the previous mode");

        // relative pos adds to the pending target, not the on-screen point.
        e = new Gh3HudElement("e", null, 0);
        e.SetPos(new Vector2(330f, 750f));
        e.Morph(Gh3Morph.Of(1f).WithPos(new Vector2(330f, 810f)), 0);
        e.Tick(500);
        e.Morph(Gh3Morph.Of(0.1f).WithPos(new Vector2(0f, 10f), relative: true), 500);
        Check(e.PosTarget == new Vector2(330f, 820f), $"relative pos target {e.PosTarget}");

        // Deviation under test: a channel the morph does not name holds its current value.
        e = new Gh3HudElement("e", null, 0);
        e.SetAlpha(0f); e.SetScale(3f);
        e.Morph(Gh3Morph.Of(0.2f, Gh3Motion.EaseIn).WithScale(1f).WithAlpha(1f), 0);
        e.Tick(200);
        e.Morph(Gh3Morph.Of(0.4f, Gh3Motion.EaseOut).WithScale(1.8f).WithRot(3f), 200);
        e.Tick(300);
        Near(e.Alpha, 1f, "unnamed alpha stays at 1 during the pulse");
        Near(e.Scale.x, 1.35f, "named scale animates from its current value (ease_out at t=0.25)");

        // Rgba bytes truncate like the native (int)(float) lerp.
        e = new Gh3HudElement("e", null, 0);
        e.SetRgba(new Color32(0, 0, 0, 255));
        e.Morph(Gh3Morph.Of(1f).WithRgba(new Color32(255, 255, 255, 255)), 0);
        e.Tick(333);
        Check(e.Rgba.r == 84, $"rgba lerp truncates: {e.Rgba.r}");
    }

    private static IEnumerator<Gh3Wait> Steps(List<string> log, Gh3HudElement morphed)
    {
        log.Add("start");
        yield return Gh3Wait.Seconds(0.5f);
        log.Add("after 0.5s");
        yield return Gh3Wait.GameFrames(1);
        log.Add("after frame");
        yield return Gh3Wait.Morph(morphed);
        log.Add("after morph");
    }

    private static IEnumerator<Gh3Wait> Forever(List<string> log)
    {
        while (true) { log.Add("loop"); yield return Gh3Wait.Seconds(0.1f); }
    }

    private static void Scheduler()
    {
        var s = new Gh3HudScheduler();
        var log = new List<string>();
        var e = new Gh3HudElement("e", null, 0);
        e.Morph(Gh3Morph.Of(1.0f).WithAlpha(0f), 0);
        s.Spawn("steps", Steps(log, e));
        Check(log.Count == 1 && log[0] == "start", "spawnscriptnow runs to the first wait synchronously");
        s.Tick(499);
        Check(log.Count == 1, "0.5 s wait not satisfied at 499 ms");
        s.Tick(1);
        Check(log.Count == 2, "0.5 s wait satisfied at 500 ms");
        Check(log.Count == 2, "1 gameframe waits for the next tick");
        s.Tick(1);
        Check(log.Count == 3 && log[2] == "after frame", "gameframe wait resumes on the next tick");
        s.Tick(400); // now 901 ms, morph (1000 ms) still running
        Check(log.Count == 3, "blocking DoMorph still waiting");
        s.Tick(100); // 1001 ms
        Check(log.Count == 4 && log[3] == "after morph", "blocking DoMorph resumes when the timer is done");
        Check(s.Count == 0, "finished script removed");

        s.Reset();
        log.Clear();
        s.Spawn("loop", Forever(log), "id1");
        s.Spawn("loop", Forever(log), "id2");
        s.Tick(100); s.Tick(100);
        Check(log.Count == 6, $"two looping scripts ran ({log.Count})");
        s.KillId("id1");
        s.Tick(100);
        Check(log.Count == 7, "KillSpawnedScript id stops only that instance");
        s.Kill("loop");
        s.Tick(100);
        Check(log.Count == 7 && s.Count == 0, "KillSpawnedScript name stops the rest");
    }

    private static void Rules()
    {
        Check(Gh3HudRules.FlipDial(29) == 1 && Gh3HudRules.FlipDial(30) == 2 && Gh3HudRules.FlipDial(100) == 3 && Gh3HudRules.FlipDial(1000) == 4, "flip dial rule");
        Check(Gh3HudRules.IsMilestone(50) && !Gh3HudRules.IsMilestone(51) && Gh3HudRules.IsMilestone(100) && !Gh3HudRules.IsMilestone(150) && Gh3HudRules.IsMilestone(200) && !Gh3HudRules.IsMilestone(49), "milestones 50 then every 100");
        Gh3HudRules.Digits(12345, out int o, out int t, out int h, out int th);
        Check(o == 5 && t == 4 && h == 3 && th == 12, "drums for 12345 = 5,4,3,12");
        Gh3HudRules.Digits(5, out o, out t, out h, out th);
        Check(o == 5 && t == 0 && h == 0 && th == 0, "drums for 5 keep leading zeros");
        int[] streaks = { 0, 5, 9, 10, 11, 19, 20, 29, 30, 31, 50 };
        int[] fulls = { 0, 2, 4, 5, 0, 4, 5, 4, 5, 5, 5 };
        int[] halves = { -1, 2, 4, -1, -1, 4, -1, 4, -1, -1, -1 };
        for (int i = 0; i < streaks.Length; i++)
        {
            Gh3HudRules.Lamps(streaks[i], out int full, out int half);
            Check(full == fulls[i] && half == halves[i], $"lamps at streak {streaks[i]}: full {full} half {half}");
        }
        Check(Gh3HudRules.LampPalette(1, false) == "" && Gh3HudRules.LampPalette(2, false) == "" && Gh3HudRules.LampPalette(3, false) == "_green"
            && Gh3HudRules.LampPalette(4, false) == "_purple" && Gh3HudRules.LampPalette(8, true) == "_blue" && Gh3HudRules.LampPalette(5, false) == null, "lamp palettes");
        Near(Gh3HudRules.NeedleAngle(1f), 42f, "needle full");
        Near(Gh3HudRules.NeedleAngle(0.5f), 0f, "needle half");
        Near(Gh3HudRules.NeedleAngle(0f), -42f, "needle empty");
        Near(Gh3HudRules.NeedleAngle(-0.8f), -42f, "needle clamps no-fail negatives");
        Gh3HudRules.RockLayers(42f, out float bg, out float by, out float lg, out float ly, out float lr, out bool flash);
        Check(bg == 1f && by == 1f && lg == 1f && ly == 0f && lr == 0f && !flash, "rock layers full");
        Gh3HudRules.RockLayers(7f, out bg, out by, out lg, out ly, out lr, out flash);
        Check(Math.Abs(bg - 0.5f) < 1e-5f && by == 1f && lg == 0f && ly == 1f && !flash, "rock layers ramping green");
        Gh3HudRules.RockLayers(-7f, out bg, out by, out lg, out ly, out lr, out flash);
        Check(bg == 0f && Math.Abs(by - 0.5f) < 1e-5f && ly == 1f && lr == 0f && !flash, "rock layers yellow fading");
        Gh3HudRules.RockLayers(-31.5f, out bg, out by, out lg, out ly, out lr, out flash);
        Check(bg == 0f && by == 0f && ly == 0f && lr == 1f && flash, "rock layers red + flash at -31.5");
        Gh3HudRules.ScoreScale(5, 140f, out float sx, out float sy);
        Check(sx == 1.1f && sy == 1.1f, "score scale up to 5 chars");
        Gh3HudRules.ScoreScale(6, 168f, out sx, out sy);
        Near(sx, 175f / 168f, "score x fit at 6 chars"); Near(sy, 1f, "score y 1.0 at 6 chars");
    }

    private static Gh3HudFont LoadFont(string name)
    {
        string dir = @"C:\Games\CloneHero\Mods\ClonZones\fallback\hud\gh3";
        string metrics = Path.Combine(dir, name + ".font.txt");
        if (!File.Exists(metrics)) return null;
        // Page dims come from the `page` line itself; the PNG is not needed for metrics.
        int w = 0, h = 0;
        foreach (string line in File.ReadAllLines(metrics))
            if (line.StartsWith("page ")) { var t = line.Split(' '); w = int.Parse(t[1]); h = int.Parse(t[2]); }
        return Gh3HudFont.Parse(name, File.ReadAllText(metrics), w, h);
    }

    private static float Width(Gh3HudFont font, string text, float spacing) => font.Measure(text.ToCharArray(), text.Length, spacing).x;

    private static void Fonts()
    {
        var a9 = LoadFont("num_a9"); var a7 = LoadFont("num_a7"); var a6 = LoadFont("text_a6");
        if (a9 == null || a7 == null || a6 == null) { Console.WriteLine("SKIP fonts: fallback/hud/gh3 metrics not installed"); return; }
        // FontAndText findings §10.
        Near(Width(a9, "0", 5f), 28f, "num_a9 '0' spacing 5");
        Near(Width(a9, "12,345", 5f), 150f, "num_a9 '12,345' spacing 5");
        Near(Width(a9, "1000000", 5f), 196f, "num_a9 '1000000' spacing 5");
        Near(a9.LineHeight, 35f, "num_a9 lineheight");
        Near(Width(a7, "1", -1f), 10f, "num_a7 '1' keeps its 10 px advance (no SetFontNonProportionalNumbers)");
        Near(Width(a7, "0", -1f), 20f, "num_a7 '0'");
        Near(Width(a6, "50 Note Streak!", -1f), 361f, "text_a6 streak text");
        Near(Width(a6, "Star Power Ready", -1f), 393f, "text_a6 ready text");
        Check(a6.IsMapped('!') && a6.IsMapped('%') && a6.IsMapped(' ') && a9.IsMapped(',') && !a7.IsMapped('A'), "glyph coverage");
    }

    private static void SceneCompose()
    {
        var scene = new Gh3HudScene(Region);
        var root = scene.CreateContainer("root", null, Vector2.zero, 0f, Gh3HudLayout.Scale);
        var score = scene.CreateContainer("score", root, Gh3HudLayout.ScorePos);
        var nixie = scene.CreateSprite("nixie", score, "HUD_score_nixie_4a", new Vector2(70f, 90f), Gh3HudLayout.JustLeftTop, 4f, 1f, new Color32(255, 255, 255, 255));
        var body = scene.CreateSprite("body", score, "HUD_score_body", Vector2.zero, Gh3HudLayout.JustLeftTop, 5f, 1f, new Color32(255, 255, 255, 255));
        var flash = scene.CreateSprite("flash", score, "HUD_score_flash", new Vector2(128f, 128f), Gh3HudLayout.JustCenterCenter, 20f, 0.5f, new Color32(255, 255, 255, 255));
        var sink = new Sink();
        scene.Draw(sink);
        Check(sink.Quads.Count == 3, "three sprites drawn");
        // Draw order: z ascending (nixie 4, body 5, flash 20).
        var q0 = sink.Quads[0];
        Near(q0.a.x, (300f + 70f) * 0.7f, "nixie top-left x = (300+70)*0.7");
        Near(q0.a.y, (650f + 90f) * 0.7f, "nixie top-left y = (650+90)*0.7");
        Near(q0.c.x - q0.a.x, 128f * 0.7f, "nixie width scaled by the 0.7 parent");
        var q2 = sink.Quads[2];
        Near((q2.a.x + q2.c.x) * 0.5f, (300f + 128f) * 0.7f, "center/center flash centred at pos");
        Check(q2.color.a == 127, $"alpha 0.5 * 255 truncates to 127 ({q2.color.a})");

        // Parent rotation: clockwise on the y-down screen (0x4FE8E0 matrix).
        scene = new Gh3HudScene(Region);
        var rock = scene.CreateContainer("rock", null, new Vector2(1000f, 600f));
        var bulb = scene.CreateContainer("bulb", rock, new Vector2(128f, 128f), 90f);
        var tube = scene.CreateSprite("tube", bulb, "HUD_rock_tube", new Vector2(0f, -160f), Gh3HudLayout.JustCenterCenter, 0f, 1f, new Color32(255, 255, 255, 255));
        sink = new Sink(); scene.Draw(sink);
        var q = sink.Quads[0];
        // local (0,-160) rotated by +90: wx = px + lx cos - ly sin = 1128 + 160, wy = 728 + 0.
        Near((q.a.x + q.c.x) * 0.5f, 1128f + 160f, "child pos rotated clockwise by the parent", 1e-3f);
        Near((q.a.y + q.c.y) * 0.5f, 728f, "child pos rotated clockwise by the parent (y)", 1e-3f);

        // Alpha multiplies down the chain; ties keep construction order.
        scene = new Gh3HudScene(Region);
        var dim = scene.CreateContainer("dim", null, Vector2.zero);
        dim.SetAlpha(0.5f);
        scene.CreateSprite("a", dim, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 5f, 0.5f, new Color32(255, 255, 255, 200));
        scene.CreateSprite("b", dim, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 4f, 1f, new Color32(255, 255, 255, 255));
        scene.CreateSprite("c", dim, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 5f, 1f, new Color32(255, 255, 255, 255));
        sink = new Sink(); scene.Draw(sink);
        Check(sink.Quads[0].color.a == 127 && sink.Quads[1].color.a == 50 && sink.Quads[2].color.a == 127, "z sort with construction-order ties and alpha chain (b, a, c)");

        // GH3 sidebars are z 3: anything below goes to the under-the-highway-sides sink, the rest
        // (tie at 3 included) stays over it, each side in the same z order as a single sink.
        scene = new Gh3HudScene(Region);
        scene.CreateSprite("over_tie", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, Gh3HudLayout.HighwaySideZ, 1f, new Color32(255, 255, 255, 3));
        scene.CreateSprite("nixie", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 1f, 1f, new Color32(255, 255, 255, 1));
        scene.CreateSprite("glow", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, -20f, 1f, new Color32(255, 255, 255, 2));
        scene.CreateSprite("body", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 3.2f, 1f, new Color32(255, 255, 255, 4));
        var under = new Sink(); var over = new Sink();
        scene.Draw(under, over);
        Check(under.Quads.Count == 2 && under.Quads[0].color.a == 2 && under.Quads[1].color.a == 1,
            "z below the highway sides goes under them, in z order (glow -20, nixie 1)");
        Check(over.Quads.Count == 2 && over.Quads[0].color.a == 3 && over.Quads[1].color.a == 4,
            "z at or above the sides stays over them (tie 3, body 3.2)");
        sink = new Sink(); scene.Draw(sink);
        Check(sink.Quads.Count == 4 && sink.Quads[0].color.a == 2 && sink.Quads[1].color.a == 1 && sink.Quads[2].color.a == 3 && sink.Quads[3].color.a == 4,
            "one sink still gets every quad in plain z order");
 
        // Source blend declarations stay in z-ordered emission order, including tied overlapping z.
        scene = new Gh3HudScene(Region);
        scene.CreateSprite("alpha_before", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop,
            7f, 0.5f, new Color32(255, 255, 255, 255));
        var additive = scene.CreateSprite("additive", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop,
            7f, 0.25f, new Color32(255, 255, 255, 255));
        additive.Blend = Gh3HudBlend.Add;
        scene.CreateSprite("alpha_after", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop,
            7f, 1f, new Color32(255, 255, 255, 255));
        sink = new Sink(); scene.Draw(sink);
        Check(sink.Quads.Count == 3 &&
              sink.Quads[0].blend == Gh3HudBlend.Alpha &&
              sink.Quads[1].blend == Gh3HudBlend.Add &&
              sink.Quads[2].blend == Gh3HudBlend.Alpha &&
              sink.Quads[0].color.a == 127 && sink.Quads[1].color.a == 63 && sink.Quads[2].color.a == 255,
              "interleaved alpha/add/alpha preserves tied z order and exact alpha");

        // Text: right/right justification lays the string out to the left of pos; glyph top follows the emitter.
        string metrics = "page 64 64\nlineheight 35\nspacewidth 4\nyorigin 0\npre 0\npost 0\nglyph 0 0 0 23 30 0 0 0\n";
        var font = Gh3HudFont.Parse("t", metrics, 64, 64);
        scene = new Gh3HudScene(Region);
        var text = scene.CreateText("score", null, font, "00", new Vector2(222f, 70f), Gh3HudLayout.JustRightRight, 20f, 1f, new Color32(255, 255, 255, 255), new Vector2(1.1f, 1.1f));
        text.FontSpacing = 5f;
        sink = new Sink(); scene.Draw(sink);
        Check(sink.Quads.Count == 2, "two glyph quads, no shadow");
        Near(sink.Quads[0].a.x, 222f - 56f * 1.1f, "first glyph left = pos - measured*scale");
        Near(sink.Quads[1].a.x, 222f - 56f * 1.1f + 28f * 1.1f, "second glyph advances by (width + spacing) * scale");
        Near(sink.Quads[0].a.y, 70f - 35f * 1.1f + (35f - 30f) * 1.1f + 1.25f, "glyph top = origin + (lineheight - (h - yoff) - yorigin)*sy + 1.25");
        text.Shadow = true; text.ShadowOffset = new Vector2(3f, 3f); text.ShadowRgba = new Color32(0, 0, 0, 255);
        sink = new Sink(); scene.Draw(sink);
        Check(sink.Quads.Count == 4 && sink.Quads[0].color.r == 0 && sink.Quads[0].a.x == sink.Quads[2].a.x + 3f, "shadow pass first, offset unscaled");

        // DestroyScreenElement removes descendants.
        scene = new Gh3HudScene(Region);
        var c1 = scene.CreateContainer("c1", null, Vector2.zero);
        scene.CreateSprite("s1", c1, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 1f, 1f, new Color32(255, 255, 255, 255));
        scene.Destroy(c1);
        Check(!scene.Exists("s1") && scene.Count == 0, "destroy removes the subtree");

        // Removing an earlier subtree, replacing an id or clearing the scene shifts the element
        // list; every child must still compose against its own parent, and equal z must keep
        // construction order (not list position).
        scene = new Gh3HudScene(Region);
        var early = scene.CreateContainer("early", null, new Vector2(900f, 900f));
        scene.CreateSprite("early_leaf", early, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 1f, 1f, new Color32(255, 255, 255, 255));
        var owner = scene.CreateContainer("owner", null, new Vector2(100f, 50f), 0f, 2f);
        var inner = scene.CreateContainer("inner", owner, new Vector2(10f, 5f));
        scene.CreateSprite("leaf", inner, "x", new Vector2(1f, 1f), Gh3HudLayout.JustLeftTop, 2f, 1f, new Color32(255, 255, 255, 255));
        scene.Destroy(early);
        sink = new Sink(); scene.Draw(sink);
        Check(sink.Quads.Count == 1, "a removed earlier subtree no longer draws");
        Near(sink.Quads[0].a.x, 100f + (10f + 1f) * 2f, "nested child composes against its own parent after an earlier subtree is removed (x)");
        Near(sink.Quads[0].a.y, 50f + (5f + 1f) * 2f, "nested child composes against its own parent after an earlier subtree is removed (y)");
        var swapped = scene.CreateContainer("owner", null, new Vector2(300f, 20f));
        scene.CreateSprite("leaf2", swapped, "x", new Vector2(4f, 0f), Gh3HudLayout.JustLeftTop, 3f, 1f, new Color32(255, 255, 255, 255));
        sink = new Sink(); scene.Draw(sink);
        Check(sink.Quads.Count == 1 && !scene.Exists("inner") && !scene.Exists("leaf"), "replacing an id removes the old subtree");
        Near(sink.Quads[0].a.x, 304f, "replacement composes from its own position");
        scene = new Gh3HudScene(Region);
        var gone = scene.CreateSprite("gone", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 1f, 1f, new Color32(1, 1, 1, 255));
        scene.CreateSprite("tieA", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 5f, 1f, new Color32(10, 10, 10, 255));
        scene.CreateSprite("tieB", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 5f, 1f, new Color32(20, 20, 20, 255));
        scene.Destroy(gone);
        scene.CreateSprite("tieA", null, "x", Vector2.zero, Gh3HudLayout.JustLeftTop, 5f, 1f, new Color32(30, 30, 30, 255));
        sink = new Sink(); scene.Draw(sink);
        Check(sink.Quads.Count == 2 && sink.Quads[0].color.r == 20 && sink.Quads[1].color.r == 30, "equal z keeps construction order after compaction and replacement");
        scene.Clear();
        var again = scene.CreateContainer("again", null, new Vector2(7f, 9f));
        scene.CreateSprite("again_leaf", again, "x", new Vector2(1f, 1f), Gh3HudLayout.JustLeftTop, 1f, 1f, new Color32(255, 255, 255, 255));
        sink = new Sink(); scene.Draw(sink);
        Check(sink.Quads.Count == 1 && sink.Quads[0].a.x == 8f && sink.Quads[0].a.y == 10f, "a cleared scene composes fresh elements from the start");

        // GH3 PC POT padding: a 164x164 image draws 164/256 of its quad from the
        // top-left, while a center/center just still pivots on the full 164 dims.
        scene = new Gh3HudScene(name => name == "npot" ? new Gh3HudRegion(164, 164, 0f, 0f, 1f, 1f) : Region(name));
        var star = scene.CreateSprite("star", null, "npot", new Vector2(500f, 400f), Gh3HudLayout.JustCenterCenter, 0f, 1f, new Color32(255, 255, 255, 255));
        sink = new Sink(); scene.Draw(sink);
        q = sink.Quads[0];
        Near(q.a.x, 500f - 82f, "npot quad keeps the full-dims pivot");
        Near(q.c.x - q.a.x, 164f * 164f / 256f, "npot art covers raw/pow2 of the quad (x)");
        Near(q.c.y - q.a.y, 164f * 164f / 256f, "npot art covers raw/pow2 of the quad (y)");
        star.Scale = new Vector2(1.4f, 1.4f);
        sink = new Sink(); scene.Draw(sink);
        Near(sink.Quads[0].c.x - sink.Quads[0].a.x, 164f * 164f / 256f * 1.4f, "npot coverage scales with the element", 1e-3f);
    }

    private static void Layout()
    {
        int bulbs = 0, containers = 0;
        foreach (var d in Gh3HudLayout.Career) { if (d.Bulb) bulbs++; if (d.Kind == Gh3HudLayout.Kind.Container) containers++; }
        Check(bulbs == 6 && containers == 9, $"6 bulbs, 9 containers ({bulbs}, {containers})");
        Check(Gh3HudLayout.PosTypeValue("rock_pos") == new Vector2(1260f, 692f) && Gh3HudLayout.PosTypeValue("counter_pos") == new Vector2(330f, 810f), "pos_type values");
        Check(Gh3HudLayout.SpReadyPos == new Vector2(640f, 210f) && Gh3HudLayout.GlowburstPos == new Vector2(640f, 237f), "notification positions derive from (640,230)");
    }
}
