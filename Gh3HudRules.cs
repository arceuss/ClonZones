namespace ClonZones
{
    /// <summary>
    /// Pure presentation rules recovered from the native 2D updater (UpdateScoreFastPerFrame
    /// 0x42F6D0 and its streak driver, gh3.exe 1.31). Inputs are Clone Hero's authoritative
    /// values; outputs are what the HUD shows. Kept free of Unity types so they are unit-testable.
    /// </summary>
    internal static class Gh3HudRules
    {
        /// <summary>Counter housing is shown at 25 and hidden below it.</summary>
        public const int CounterShowStreak = 25;

        /// <summary>sub_422BC0: which drum flips on a streak change (1 = ones .. 4 = thousands).</summary>
        public static int FlipDial(int streak) =>
            streak % 1000 == 0 ? 4 : streak % 100 == 0 ? 3 : streak % 10 != 0 ? 1 : 2;

        /// <summary>0x422E8A: streak notice at 50, then every 100.</summary>
        public static bool IsMilestone(int streak) => streak == 50 || (streak > 50 && streak % 100 == 0);

        /// <summary>0x422FB9: four drums, thousands is the full quotient (12345 -> "12" on the last drum).</summary>
        public static void Digits(int s, out int ones, out int tens, out int hundreds, out int thousands)
        {
            ones = s - 10 * (s / 10); tens = s / 10 - 10 * (s / 100); hundreds = s / 100 - 10 * (s / 1000); thousands = s / 1000;
        }

        /// <summary>Lamp level: v44 decade rule; full = trunc(v44/2), half on odd v44, all off at v44 &lt;= 1.</summary>
        public static void Lamps(int streak, out int full, out int half)
        {
            int v44 = streak > 30 ? 10 : streak > 20 ? streak - 20 : streak > 10 ? streak - 10 : streak;
            if (v44 <= 1) { full = 0; half = -1; return; }
            full = (int)(v44 * 0.5f);
            half = (v44 & 1) != 0 && full < 5 ? full : -1;
        }

        /// <summary>Palette suffix for the score lamps: SP -> blue; 1,2 -> base; 3 -> green; 4 -> purple; other -> keep (null).</summary>
        public static string LampPalette(int mult, bool spUsed)
        {
            if (spUsed) return "_blue";
            if (mult == 1 || mult == 2) return "";
            if (mult == 3) return "_green";
            if (mult == 4) return "_purple";
            return null;
        }

        /// <summary>Health branch: needle degrees, clockwise positive, +42 full / -42 empty (health clamped to the dial).</summary>
        public static float NeedleAngle(float health01)
        {
            float h = health01 < 0f ? 0f : health01 > 1f ? 1f : health01;
            return (1f - h) * -84f + 42f;
        }

        public const float BandDegrees = 14f, FlashDegrees = 31.5f;

        /// <summary>Rock meter layer alphas for a needle angle (bands at 14 degrees, red flash at or below -31.5).</summary>
        public static void RockLayers(float a, out float bgGreen, out float bgYellow, out float lightsGreen, out float lightsYellow, out float lightsRed, out bool flash)
        {
            const float B = BandDegrees;
            if (a > 0f) { bgGreen = a / B < 1f ? a / B : 1f; bgYellow = 1f; lightsGreen = a >= B ? 1f : 0f; lightsYellow = a < B ? 1f : 0f; lightsRed = 0f; }
            else if (a == 0f) { bgGreen = 0f; bgYellow = 1f; lightsGreen = 0f; lightsYellow = 1f; lightsRed = 0f; }
            else if (a > -B) { bgGreen = 0f; bgYellow = 1f + a / B; lightsGreen = 0f; lightsYellow = 1f; lightsRed = 0f; }
            else { bgGreen = 0f; bgYellow = 0f; lightsGreen = 0f; lightsYellow = 0f; lightsRed = 1f; }
            flash = a <= -FlashDegrees;
        }

        /// <summary>Score text scale: 1.1 uniform up to 5 characters, then fit the frame width horizontally at height 1.0.</summary>
        public static void ScoreScale(int characters, float measuredWidth, out float sx, out float sy)
        {
            if (characters > 5) { sx = Gh3HudLayout.ScoreFrameWidth / measuredWidth; sy = 1f; }
            else { sx = sy = Gh3HudLayout.ScoreTextScale; }
        }
    }
}
