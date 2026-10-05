using System;
using ClonZones;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        try
        {
            for (int quarter = 0; quarter <= Gh3HighwayLayout.RowCount * 4; quarter++)
            {
                float row = quarter * .25f;
                Require(Gh3HighwayLayout.ExtendedYAtRow(row) == Gh3HighwayLayout.YAtRow(row),
                    "length extension changed the original GH3 row projection");
            }

            float top = Gh3HighwayLayout.ExtendedYAtRow(0f);
            Require(top == Gh3HighwayLayout.TopY, "extension does not join the authored far edge");
            float leftSlope = top - Gh3HighwayLayout.ExtendedYAtRow(-1f);
            float rightSlope = Gh3HighwayLayout.YAtRow(1f) - top;
            Require(MathF.Abs(leftSlope - rightSlope) < .0002f,
                "note travel changes slope abruptly at the original far edge");

            float previousY = top;
            float previousWidth = Gh3HighwayLayout.TopWidth;
            float edgeSlope = (Gh3HighwayLayout.BottomWidth - Gh3HighwayLayout.TopWidth)
                / (2f * Gh3HighwayLayout.Height);
            // Covers up to nine times the reference depth, beyond the captured 100/170% pair.
            for (int row = -1; row >= -8192; row--)
            {
                float y = Gh3HighwayLayout.ExtendedYAtRow(row);
                float width = Gh3HighwayLayout.WidthAtY(y);
                Require(float.IsFinite(y) && y < previousY,
                    "longer highways stop extending or reverse their depth order");
                Require(width > 0f && width < previousWidth,
                    "extended highway crosses its vanishing point or stops narrowing");
                float actualSlope = (Gh3HighwayLayout.BottomWidth - width)
                    / (2f * (Gh3HighwayLayout.Playline - y));
                Require(MathF.Abs(actualSlope - edgeSlope) < .000001f,
                    "length extension changes the highway edge angle");
                previousY = y;
                previousWidth = width;
            }
            foreach (float speed in new[] { .8f, 1f, 1.25f })
            {
                double[] chartBeats = { 0, .5, 1, 1.5, 2, 2.5, 2.9, 3.3, 3.7, 4.1, 4.5, 4.9 };
                double[] times = Array.ConvertAll(chartBeats, time => time / speed);
                int[] weights = new int[times.Length];
                var tempos = new[] {
                    new Gh3BeatTimeline.Tempo(0, 0, 120),
                    new Gh3BeatTimeline.Tempo(960, 2.5 / speed, 150)
                };
                var timeline = new Gh3BeatTimeline(times, weights, new long[] { 0, 768, 1344 },
                    tempos, 192, 192 * (double)speed);
                Require(timeline.Bars.Length == times.Length * 2 - 1,
                    $"speed {speed}: duplicate or missing beatlines");
                for (int i = 0; i < timeline.Bars.Length; i++)
                {
                    double expected = i % 2 == 0 ? times[i / 2] : (times[i / 2] + times[i / 2 + 1]) * .5;
                    Require(Math.Abs(timeline.Bars[i].Time - expected) < 1e-7,
                        $"speed {speed}: eighth-note grid drifted at bar {i}");
                    Require(timeline.Bars[i].Synthetic == (i % 2 != 0),
                        $"speed {speed}: synthesized a second copy of a native beat");
                }
            }

            // Projected heads fade per pixel in CH's "Sprite Track FadeIn" shader; ribbons fade per
            // vertex. Both must enter through the same band with the same curve, or a sustain's
            // ribbon shows up ahead of its head. The model below is the shader's far/close fade
            // (Clone Hero_Data/globalgamemanagers.assets shader path ID 12, D3D11 pixel programs
            // with TRACKFADE_ON): u = 1 - screenUvY, v3 = 1 - _FadeParams.
            foreach ((float playline, float pixels, float viewportY, float viewportHeight) in new[] {
                (96.7f, 1.3993f, 0f, 1024f), (560f, .7f, 512f, 512f) })
            {
                float topY = 267.09f;
                float farEnd = Gh3HighwayLayout.ScreenUvY(topY, playline, pixels, viewportY, viewportHeight);
                float farStart = Gh3HighwayLayout.ScreenUvY(topY + Gh3HighwayLayout.Fade, playline, pixels, viewportY, viewportHeight);
                // close band as CH wrote it in the captured run (off-screen below the playline).
                float closeEnd = -.13429779f, closeStart = -.21680081f;
                Require(farEnd != 0f && farStart != 0f, "a zero _FadeParams component switches the shader to its default band");
                for (float y = topY - 20f; y <= topY + 60f; y += .25f)
                {
                    float u = 1f - Gh3HighwayLayout.ScreenUvY(y, playline, pixels, viewportY, viewportHeight);
                    float far = Math.Clamp((u - (1f - farStart)) / ((1f - farEnd) - (1f - farStart)), 0f, 1f);
                    float close = Math.Clamp((u - (1f - closeStart)) / ((1f - closeEnd) - (1f - closeStart)), 0f, 1f);
                    float head = (1f - far * far * (3f - 2f * far)) * (close * close * (3f - 2f * close));
                    float ribbon = Gh3HighwayLayout.EntryFade(y - topY);
                    Require(MathF.Abs(head - ribbon) < .0005f,
                        $"head and ribbon disagree at {y - topY:0.##} below the far edge: {head} != {ribbon}");
                    if (y <= topY) Require(head == 0f, "a head is visible above the projected far edge");
                    if (y >= topY + Gh3HighwayLayout.Fade + .01f) Require(head == 1f, "a head is still faded below the band");
                }
            }
            Console.WriteLine("5 passed: reference rows, continuous join, nonfolding fixed-angle extension, speed-correct beat grid across tempo and meter changes, head/ribbon entry fade agreement.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
