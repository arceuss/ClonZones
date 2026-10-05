namespace ClonZones
{
    // GH3 highway layout for the single supported height, 350.
    // Source: NotClon/src/renderer.cpp:5817-5851 (row table), 5864-5878 (yAtRow, edges),
    // and NotClon/src/gh3_tables.h index 188. No Unity code here, plain math only.
    internal static class Gh3HighwayLayout
    {
        public const float CenterX = 640f;
        public const float Playline = 655f;
        public const float Height = 350f;
        public const float TopY = 305f; // Playline - Height
        public const float TopWidth = 160f;
        public const float BottomWidth = 512f; // TopWidth * (1 + 2.2f)
        public const float Fade = 30f; // highway_fade, authored fade-band height

        // NotClon/src/gh3_tables.h, idx = (int)Height - 162 = 188.
        // HEIGHT_PERSP_FACT[188] = 1.000886f.
        // HEIGHT_PERSP_EXP[188] = 1.001541f.
        public const float PerspFact = 1.000886f;
        public const float PerspExp = 1.001541f;

        // Fretbar scale endpoints from renderer.cpp:5826-5827.
        // FRETBAR_S0 = 0.15f, FRETBAR_S1 = S0 * (1 + 2.2f) = 0.48f.
        public const float FretbarS0 = 0.15f;
        public const float FretbarS1 = 0.48f;

        public const int RowCount = 1152;
        public const int StrikelineRow = 1024;

        // Accumulated row positions, rowY[0..1152]. Row 1024 is the strikeline
        // up to float drift. Built once with float math to match NotClon.
        public static readonly float[] RowY = BuildRowY();

        private static float[] BuildRowY()
        {
            float[] rows = new float[RowCount + 1];
            float[] rnd = new float[RowCount];
            rnd[0] = 1.0f;
            for (int i = 1; i < RowCount; ++i)
                rnd[i] = System.MathF.Pow(rnd[i - 1] * PerspFact, PerspExp);
            float sum = 0.0f;
            for (int i = 0; i < StrikelineRow; ++i)
                sum += rnd[i];
            float norm = 1.0f / sum;
            for (int i = 0; i < RowCount; ++i)
                rnd[i] *= norm;
            rows[0] = TopY;
            for (int i = 1; i <= RowCount; ++i)
                rows[i] = rows[i - 1] + Height * rnd[i - 1];
            return rows;
        }

        // NotClon yAtRow lambda: clamp, then lerp between the two nearby rows.
        public static float YAtRow(float row)
        {
            if (row <= 0f)
                return RowY[0];
            if (row >= RowCount)
                return RowY[RowCount];
            int r = (int)row;
            if (r >= RowCount)
                r = RowCount - 1;
            return RowY[r] + (RowY[r + 1] - RowY[r]) * (row - r);
        }

        // the original table stops at the authored far edge. longer CH highways need a
        // continuation, not a wider depth normalization. match its first slope and approach
        // the same width-zero horizon; reversing the recurrence eventually crosses it.
        public static float ExtendedYAtRow(float row)
        {
            if (row >= 0f) return YAtRow(row);
            float gap = TopWidth * Height / (BottomWidth - TopWidth);
            float slope = RowY[1] - RowY[0];
            return TopY - gap + gap / (1f - row * slope / gap);
        }

        // Inverse of YAtRow for the bridge: binary search, then lerp in the span.
        public static float RowAtY(float y)
        {
            if (y <= RowY[0])
                return 0f;
            if (y >= RowY[RowCount])
                return RowCount;
            int lo = 0;
            int hi = RowCount;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (RowY[mid] <= y)
                    lo = mid;
                else
                    hi = mid;
            }
            float span = RowY[lo + 1] - RowY[lo];
            if (span <= 0f)
                return lo;
            return lo + (y - RowY[lo]) / span;
        }

        private static float GeoAt(float y)
        {
            return (y - TopY) / Height;
        }

        public static float WidthAtY(float y)
        {
            float g = GeoAt(y);
            return TopWidth + (BottomWidth - TopWidth) * g;
        }

        public static float LaneX(int lane, float y)
        {
            float g = GeoAt(y);
            float topStep = TopWidth / 5f;
            float botStep = BottomWidth / 5f;
            float sx = (CenterX - TopWidth * 0.5f) + topStep * 0.5f + topStep * lane;
            float ex = (CenterX - BottomWidth * 0.5f) + botStep * 0.5f + botStep * lane;
            return sx + (ex - sx) * g;
        }

        public static float BarScale(float y)
        {
            float g = GeoAt(y);
            return FretbarS0 + (FretbarS1 - FretbarS0) * g;
        }

        // GH3's note-entry fade. Every GH3 highway vertex shader that takes m_startFade/m_endFade
        // (animated gem sprites, plain sprites, the highway and WhammyBar in
        // DATA/FXFILES/MaterialLibrary.bin.xen) computes smoothstep(saturate((y - end) / (start - end)))
        // with end = the far edge and start = end + highway_fade. belowTop is y - end.
        public static float EntryFade(float belowTop)
        {
            float t = belowTop / Fade;
            if (!(t > 0f)) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }

        // Screen-uv y (0 at the bottom of the camera viewport, 1 at its top) of an authored row,
        // given where the playline lands in viewport pixels. CH's track-fade shader compares its
        // _FadeParams against this value per pixel.
        public static float ScreenUvY(float y, float playlinePixelY, float pixelsPerUnit, float viewportY, float viewportHeight)
        {
            return (playlinePixelY + (Playline - y) * pixelsPerUnit - viewportY) / viewportHeight;
        }
    }
}
