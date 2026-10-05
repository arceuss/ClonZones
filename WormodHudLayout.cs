using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// The active GH3PC WoR Mod Installer 1P career HUD table. Values come from
    /// guitar_hud_2d_career.qb (career_hud_2d_elements, 23 scalars and 70
    /// declarations), not from the older GH3 table. Numeric-only element ids are
    /// given stable local names below; their source checksums stay in the comments.
    /// </summary>
    internal static class WormodHudLayout
    {
        // The source table's 1P career bank, plus every palette selected by the
        // native multiplier path and the four lightning notification sprites.
        // WiFi_bar6/7 are ClonZones additions for CH's 6th and 7th star (see StarImages).
        public static readonly string[] ImageNames =
        {
            "Char_Select_Hilite1",
            "HUD_rock_body", "HUD_rock_BG_green", "HUD_rock_BG_red", "HUD_rock_BG_yellow",
            "HUD_rock_lights_all", "HUD_rock_lights_green", "HUD_rock_lights_red", "HUD_rock_lights_yellow",
            "HUD_rock_needle", "HUD_rock_tube",
            "HUD_rock_tube_glow_fill", "HUD_rock_tube_glow_fill_b",
            "HUD_rock_tube_glow_full", "HUD_rock_tube_glow_full_b",
            "GH3_Pause_Bunny_Flame1", "GH3_Pause_Bunny_Flame2",
            "HUD_counter_body", "HUD_counter_drum", "HUD_counter_drum_icon",
            "HUD_score_body", "HUD_score_flash",
            "HUD_score_light_0", "HUD_score_light_0_blue", "HUD_score_light_0_green", "HUD_score_light_0_purple",
            "HUD_score_light_1", "HUD_score_light_1_blue", "HUD_score_light_1_green", "HUD_score_light_1_purple",
            "HUD_score_light_2", "HUD_score_light_2_blue", "HUD_score_light_2_green", "HUD_score_light_2_purple",
            "HUD_score_nixie_1a", "HUD_score_nixie_2a", "HUD_score_nixie_2b", "HUD_score_nixie_3a",
            "HUD_score_nixie_4a", "HUD_score_nixie_4b", "HUD_score_nixie_6b", "HUD_score_nixie_8b",
            "GH3_Pause_Bunny_Flame3", "GH3_Pause_Bunny_Flame4", "GH3_Pause_Bunny_Flame5", "GH3_Pause_Bunny_Flame6",
            "wifi_bar0", "WiFi_bar1", "WiFi_bar2", "WiFi_bar3", "WiFi_bar4", "WiFi_bar5", "WiFi_bar6", "WiFi_bar7",
            "HUD_lightning_01", "HUD_lightning_03", "HUD_lightning_05", "HUD_lightning_07",
        };

        // career_hud_2d_elements scalars, float32 values as authored.
        public static readonly Vector2 OffscreenRockPos = new(450f, -1000f);
        public static readonly Vector2 OffscreenScorePos = new(1368f, 1500f);
        public static readonly Vector2 RockPos = new(450f, 692f);
        public static readonly Vector2 ScorePos = new(1368f, 768f);
        public static readonly Vector2 CounterPos = new(1365f, 820f);
        public static readonly Vector2 OffscreenNoteStreakBarOff = new(0f, 800f);
        public const float Scale = 0.699999988079071f;
        public const float SmallBulbScale = 0.699999988079071f;
        public const float BigBulbScale = 0.800000011920929f;
        public const float ScoreFrameWidth = 175f;

        public static readonly Vector2 ScoreTextPos = new(222f, 70f);
        public const float ScoreTextScale = 1.100000023841858f;
        public const float ScoreTextZ = 20f;
        public const int ScoreFontSpacing = 5;
        public static readonly Vector2 DisplayTextShadowOffset = new(3f, 3f);

        public static readonly Vector2 CounterDigitBase = new(222f, 78f);
        public static readonly Vector2 CounterDigitStep = new(-37f, 0f);
        public const float CounterDigitZ = 25f;
        public static readonly Color32 CounterDigitRgba = new(230, 230, 230, 200);

        public static readonly Vector2 AlertBasePos = new(640f, 230f);
        public static readonly Vector2 JustCenterTop = new(0f, -1f);
        public static readonly Color32 NotificationRgba = new(210, 210, 210, 250);
        public static readonly Color32 NotificationShadowRgba = new(0, 0, 0, 255);

        // Custom rock script globals and authored output colours.
        // Gh3HudSnapshot.Health is already the source needle's normalized 0..1
        // value; the QB's 0.5 * raw [0,2] conversion is done by the bridge.
        public const float NeedleCurve = 0.5849999785423279f; // #"0xb8d68ee1"
        public static readonly Vector2 NeedleCurveStart = new(-558f, 182f); // #"0xfbec6698"
        public static readonly Vector2 NeedleCurveEnd = new(-458f, 18f);   // #"0xf81f86d7"
        public const float NeedleScaleStart = 1f;                           // #"0xfb0d3c99"
        public const float NeedleScaleEnd = 0.800000011920929f;             // #"0x2ce68ed8"
        public const float HealthPoorMedium = 0.6665999889373779f;
        public const float HealthMediumGood = 1.333299994468689f;
        public static readonly Color32 NeedleGlowGreen = new(60, 255, 60, 255);   // #"0xb6ac2f81"
        public static readonly Color32 NeedleGlowYellow = new(255, 255, 48, 255); // #"0xdcf09d3c"
        public static readonly Color32 NeedleGlowRed = new(255, 71, 68, 255);     // #"0x8fb6f5f6"
        public static readonly Color32 DullerMeterRgba = new(175, 175, 175, 255); // #"0x4ca1aa8d"

        // update_star_meter globals.
        public const float StarMeterWidth = 256f;       // #"0xb9828f7c"
        public const float StarMeterHeight = 6.5f;      // #"0xd5189206"
        public static readonly Vector2 StarTipOrigin = new(-3f, 91f); // #"0x1d41c861"
        public const float CompletionMeterWidth = 267f;  // #"0xd22c1054"
        public const float CompletionMeterHeight = 6f;  // #"0xe0c69463"
        public static readonly Vector2 CompletionTipOrigin = new(-6f, 35f); // #"0x9b53cde3"

        // Numeric-only source ids recovered from the active table and star script.
        public const string NeedleId = "HUD2D_rock_needle_custom";       // #"0xcccf93ce"
        public const string NeedleGlowId = "HUD2D_rock_needle_glow";     // #"0x42b328e7"
        public const string NeedleGlow2Id = "HUD2D_rock_needle_glow2";   // #"0xf6fb9573"
        public const string StarMeterId = "HUD2D_score_star_meter";       // #"0x0500c035"
        public const string StarBackingId = "HUD2D_score_star_backing";   // #"0xddc6125f"
        public const string StarTipId = "HUD2D_score_star_tip";            // #"0x4ae39f0b"
        public const string CompletionMeterId = "HUD2D_score_completion_meter"; // #"0x95a37ec5"
        public const string CompletionBackingId = "HUD2D_score_completion_backing"; // #"0x3693c461"
        public const string CompletionTipId = "HUD2D_score_completion_tip"; // #"0x766026d8"
        public const string StarContainerId = "HUD2D_score_star_container"; // #"0xf1b083bb"
        public const string StarGlowId = "HUD2D_score_star_glow";          // #"0x3a74478c"

        // One complete star image per displayed count (same 164x164 canvas and alpha,
        // only the numeral differs). The installer authors 0..5; CH's StarProgress counts
        // to 7 (.ctor 0x1802E0990), so 6 and 7 use the FNT-glyph rebuilds from
        // WiFiBars_FNT_pixel_matched. Index = CH star count.
        public static readonly string[] StarImages =
        {
            "wifi_bar0", "WiFi_bar1", "WiFi_bar2", "WiFi_bar3", "WiFi_bar4", "WiFi_bar5", "WiFi_bar6", "WiFi_bar7",
        };
        public static readonly string[] StarIds =
        {
            "HUD2D_score_star_0", "HUD2D_score_star_1", "HUD2D_score_star_2", "HUD2D_score_star_3",
            "HUD2D_score_star_4", "HUD2D_score_star_5", "HUD2D_score_star_6", "HUD2D_score_star_7",
        };

        internal sealed class NotificationDecl
        {
            public string Id;
            public string Text;
            public Vector2 Pos;
            public float Scale = 1f;
            public bool Shadow;
            public Vector2 ShadowOffset;
            public Color32 ShadowRgba;
        }

        // guitar_hud.qb hud_screen_elements, all 12 active installer entries.
        public static readonly NotificationDecl[] Notifications =
        {
            new() { Id = "star_power_ready_text", Text = "Star Power Ready", Pos = new Vector2(640f, 230f), Shadow = true, ShadowOffset = new Vector2(2f, 2f), ShadowRgba = NotificationShadowRgba },
            new() { Id = "double_notes_text", Text = "Double Notes!", Pos = new Vector2(640f, 300f), Scale = 0.699999988079071f },
            new() { Id = "difficulty_up_text", Text = "Difficulty Up!", Pos = new Vector2(640f, 300f), Scale = 0.699999988079071f },
            new() { Id = "lefty_notes_text", Text = "Lefty Notes!", Pos = new Vector2(640f, 300f), Scale = 0.699999988079071f },
            new() { Id = "broken_string_text", Text = "Broken String!", Pos = new Vector2(640f, 300f), Scale = 0.699999988079071f },
            new() { Id = "whammy_attack_text", Text = "Whammy!", Pos = new Vector2(640f, 300f), Scale = 0.699999988079071f },
            new() { Id = "lightning_text", Text = "Amp Overload!", Pos = new Vector2(640f, 300f), Scale = 0.699999988079071f },
            new() { Id = "steal_text", Text = "JACKED!", Pos = new Vector2(640f, 300f), Scale = 0.699999988079071f },
            new() { Id = "steal1_text", Text = "THIEF!", Pos = new Vector2(640f, 300f), Scale = 0.699999988079071f },
            new() { Id = "steal2_text", Text = "Nothing to steal...", Pos = new Vector2(640f, 300f), Scale = 0.699999988079071f },
            new() { Id = "coop_raise_axe", Text = "Tilt guitar to trigger", Pos = new Vector2(640f, 300f), Scale = 0.699999988079071f, Shadow = true, ShadowOffset = new Vector2(2f, 2f), ShadowRgba = NotificationShadowRgba },
            new() { Id = "coop_raise_axe_cont", Text = "Star Power", Pos = new Vector2(640f, 300f), Scale = 1.100000023841858f, Shadow = true, ShadowOffset = new Vector2(2f, 2f), ShadowRgba = NotificationShadowRgba },
        };

        private static Gh3HudLayout.Decl Bulb(int index, bool small, float tubeY, float fullAlpha)
        {
            return new Gh3HudLayout.Decl
            {
                Id = $"HUD2D_rock_tube_{index}", Kind = Gh3HudLayout.Kind.Sprite, Parent = $"HUD2D_bulb_container_{index}",
                Texture = "HUD_rock_tube", PosOff = new Vector2(0f, small ? -160f : -150f),
                InitialPos = small ? new Vector2(0f, 0f) : null,
                ElementDims = new Vector2(64f, 128f), SmallBulb = small, Just = Gh3HudLayout.JustCenterCenter, Bulb = true,
                TubeTexture = "HUD_rock_tube_glow_fill", TubeStarTexture = "HUD_rock_tube_glow_fill_b",
                // the installer's tube.element_dims are (860,270)/(755,100) for bulbs 1/3. that only sizes
                // the never-shown empty tube: explicit dims become scale = dims/texture with the base reset
                // to the texture (CSpriteElement::SetProperties 0x4FB580), and the first UpdateSPMeter write
                // replaces that scale. so the fill is the same 64x16 art as the GH3 HUD and the bulb scale drops out too.
                // using the table value drew a 385x648 glow.
                TubeDims = new Vector2(64f, 16f),
                TubePosOff = new Vector2(0f, tubeY), TubeZ = 3.0999999046325684f, TubeAlpha = 1f,
                FullTexture = "HUD_rock_tube_glow_full", FullStarTexture = "HUD_rock_tube_glow_full_b",
                FullZ = 8.199999809265137f, FullAlpha = fullAlpha,
            };
        }

        private static Gh3HudLayout.Decl BulbContainer(int index, Vector2 pos) => new()
        {
            Id = $"HUD2D_bulb_container_{index}", Kind = Gh3HudLayout.Kind.Container, Parent = "HUD2D_rock_container",
            PosOff = pos, Rot = -28f,
        };

        private static Gh3HudLayout.Decl Sprite(string id, string parent, string texture, Vector2 pos, float z, float alpha = 1f,
            Vector2? dims = null, Vector2? just = null, Color32? rgba = null, float scale = 1f, Gh3HudBlend blend = Gh3HudBlend.Alpha) => new()
        {
            Id = id, Kind = Gh3HudLayout.Kind.Sprite, Parent = parent, Texture = texture, PosOff = pos,
            Z = z, Alpha = alpha, Dims = dims, Just = just ?? Gh3HudLayout.JustLeftTop, Rgba = rgba,
            SpriteScale = scale, Blend = blend,
        };

        private static Gh3HudLayout.Decl Lamp(string state, int index, string texture, Vector2 pos, float z, float alpha) => Sprite(
            $"HUD2D_score_light_{state}_{index}", "HUD2D_score_container", texture, pos, z, alpha);

        private static Gh3HudLayout.Decl Nixie(string variant) => Sprite(
            $"HUD2D_score_nixie_{variant}", "HUD2D_score_container", $"HUD_score_nixie_{variant}", new Vector2(-278f, -230f), 1f, 0f);

        /// <summary>
        /// Source order is unchanged: 70 declarations from the active career struct, plus
        /// the two ClonZones star layers (6, 7) that continue its 20+count z pattern below
        /// the meter (28), tip (29) and glow (30).
        /// </summary>
        public static readonly Gh3HudLayout.Decl[] Career =
        {
            new() { Id = "HUD2D_rock_container", Kind = Gh3HudLayout.Kind.Container, PosType = "offscreen_rock_pos" },
            Sprite("HUD2D_rock_glow", "HUD2D_rock_container", "Char_Select_Hilite1", new Vector2(650f, -70f), -20f, 0f, new Vector2(350f, 350f), rgba: new Color32(95, 205, 255, 255)),
            Sprite("HUD2D_rock_body", "HUD2D_rock_container", "HUD_rock_body", new Vector2(643f, -56f), 3.200000047683716f),
            Sprite("HUD2D_rock_BG_green", "HUD2D_rock_body", "HUD_rock_BG_green", new Vector2(-615f, 6f), 3.200000047683716f),
            Sprite("HUD2D_rock_BG_red", "HUD2D_rock_body", "HUD_rock_BG_red", new Vector2(-615f, 6f), 3f),
            Sprite("HUD2D_rock_BG_yellow", "HUD2D_rock_body", "HUD_rock_BG_yellow", new Vector2(-615f, 6f), 3.0999999046325684f),
            Sprite("HUD2D_rock_lights_all", "HUD2D_rock_body", "HUD_rock_lights_all", Vector2.zero, 3f),
            Sprite("HUD2D_rock_lights_green", "HUD2D_rock_body", "HUD_rock_lights_green", new Vector2(-509f, 0f), 18f, 0f, just: Gh3HudLayout.JustLeftTop),
            Sprite("HUD2D_rock_lights_red", "HUD2D_rock_body", "HUD_rock_lights_red", new Vector2(-574f, 120f), 7f, 0f, just: Gh3HudLayout.JustLeftTop),
            Sprite("HUD2D_rock_lights_yellow", "HUD2D_rock_body", "HUD_rock_lights_yellow", new Vector2(-474f, 52f), 18f, 0f, just: Gh3HudLayout.JustCenterTop),
            Sprite("HUD2D_rock_needle", "HUD2D_rock_body", "HUD_rock_needle", new Vector2(900f, -900f), 19f, 0f, just: new Vector2(0.5f, 0.800000011920929f)),
            Sprite(NeedleId, "HUD2D_rock_body", "HUD_rock_needle", new Vector2(-450f, 120f), 20f),
            Sprite(NeedleGlowId, NeedleId, "GH3_Pause_Bunny_Flame1", Vector2.zero, 19f, blend: Gh3HudBlend.Add),
            Sprite(NeedleGlow2Id, NeedleId, "GH3_Pause_Bunny_Flame2", Vector2.zero, 19f, 0.20000000298023224f, blend: Gh3HudBlend.Add),
            BulbContainer(1, new Vector2(867.6500244140625f, 269.5f)),
            Bulb(1, false, 40f, 0f),
            BulbContainer(2, new Vector2(851.5f, 240f)),
            Bulb(2, false, 40f, 0f),
            BulbContainer(3, new Vector2(834f, 208.5f)),
            Bulb(3, false, 40f, 0f),
            BulbContainer(4, new Vector2(748.25f, 50f)),
            Bulb(4, true, 32f, 1f),
            BulbContainer(5, new Vector2(733f, 25f)),
            Bulb(5, true, 32f, 1f),
            BulbContainer(6, new Vector2(716.7999877929688f, -4.5f)),
            Bulb(6, true, 32f, 1f),
            new() { Id = "HUD2D_score_container", Kind = Gh3HudLayout.Kind.Container, PosType = "offscreen_score_pos" },
            Sprite("HUD2D_score_body", "HUD2D_score_container", "HUD_score_body", new Vector2(-30f, -9f), 5f),
            new() { Id = "HUD2D_note_container", Kind = Gh3HudLayout.Kind.Container, PosType = "counter_pos", NoteStreakBar = true },
            Sprite("HUD2D_counter_body", "HUD2D_note_container", "HUD_counter_body", Vector2.zero, 4f),
            Sprite("HUD_counter_drum", "HUD2D_note_container", "HUD_counter_drum", new Vector2(4f, 40f), 8f),
            Sprite("HUD2D_counter_drum_icon", "HUD2D_note_container", "HUD_counter_drum_icon", new Vector2(44f, 40f), 26f),
            Lamp("unlit", 1, "HUD_score_light_0", new Vector2(-235f, -165f), 5f, 1f),
            Lamp("unlit", 2, "HUD_score_light_0", new Vector2(-245f, -182f), 5f, 1f),
            Lamp("unlit", 3, "HUD_score_light_0", new Vector2(-255f, -199f), 5f, 1f),
            Lamp("unlit", 4, "HUD_score_light_0", new Vector2(-265f, -216f), 5f, 1f),
            Lamp("unlit", 5, "HUD_score_light_0", new Vector2(-275f, -233f), 5f, 1f),
            Lamp("halflit", 1, "HUD_score_light_1", new Vector2(-235f, -165f), 5.099999904632568f, 0f),
            Lamp("halflit", 2, "HUD_score_light_1", new Vector2(-245f, -182f), 5.099999904632568f, 0f),
            Lamp("halflit", 3, "HUD_score_light_1", new Vector2(-255f, -199f), 5.099999904632568f, 0f),
            Lamp("halflit", 4, "HUD_score_light_1", new Vector2(-265f, -216f), 5.099999904632568f, 0f),
            Lamp("halflit", 5, "HUD_score_light_1", new Vector2(-275f, -233f), 5.099999904632568f, 0f),
            Lamp("allwaylit", 1, "HUD_score_light_2", new Vector2(-235f, -165f), 5.199999809265137f, 0f),
            Lamp("allwaylit", 2, "HUD_score_light_2", new Vector2(-245f, -182f), 5.199999809265137f, 0f),
            Lamp("allwaylit", 3, "HUD_score_light_2", new Vector2(-255f, -199f), 5.199999809265137f, 0f),
            Lamp("allwaylit", 4, "HUD_score_light_2", new Vector2(-265f, -216f), 5.199999809265137f, 0f),
            Lamp("allwaylit", 5, "HUD_score_light_2", new Vector2(-275f, -233f), 5.199999809265137f, 0f),
            Nixie("1a"), Nixie("2a"), Nixie("2b"), Nixie("3a"), Nixie("4a"), Nixie("4b"), Nixie("6b"), Nixie("8b"),
            Sprite("HUD2D_score_flash", "HUD2D_score_container", "HUD_score_flash", new Vector2(128f, 128f), 20f, 0f, just: Gh3HudLayout.JustCenterCenter),
            Sprite(StarMeterId, "HUD2D_score_container", "GH3_Pause_Bunny_Flame3", new Vector2(-12f, 84f), 28f, dims: new Vector2(256f, 6f), just: new Vector2(-1f, 0f), rgba: new Color32(196, 169, 65, 255)),
            Sprite(StarBackingId, "HUD2D_score_container", "GH3_Pause_Bunny_Flame5", new Vector2(-13f, 89f), 3f, dims: new Vector2(256f, 12f), just: new Vector2(-1f, 0f), scale: 1.7000000476837158f),
            Sprite(StarTipId, "HUD2D_score_container", "GH3_Pause_Bunny_Flame6", Vector2.zero, 29f, just: new Vector2(0.699999988079071f, 0.5f), scale: 0.8999999761581421f, blend: Gh3HudBlend.Add),
            Sprite(CompletionMeterId, "HUD2D_score_container", "GH3_Pause_Bunny_Flame3", new Vector2(-17f, 28f), 4f, dims: new Vector2(267f, 6f), just: new Vector2(-1f, 0f), rgba: new Color32(70, 125, 196, 255)),
            Sprite(CompletionBackingId, "HUD2D_score_container", "GH3_Pause_Bunny_Flame5", new Vector2(-17f, 33f), 3f, dims: new Vector2(267f, 12f), just: new Vector2(-1f, 0f), scale: 1.649999976158142f),
            Sprite(CompletionTipId, "HUD2D_score_container", "GH3_Pause_Bunny_Flame6", Vector2.zero, 6f, just: new Vector2(0.699999988079071f, 0.5f), scale: 0.8999999761581421f, blend: Gh3HudBlend.Add),
            new() { Id = StarContainerId, Kind = Gh3HudLayout.Kind.Container, Parent = "HUD2D_score_container", PosOff = new Vector2(220f, 15f) },
            Sprite(StarIds[0], StarContainerId, StarImages[0], new Vector2(-20f, -28f), 20f, 1f, scale: 1.399999976158142f),
            Sprite(StarIds[1], StarContainerId, StarImages[1], new Vector2(-20f, -28f), 21f, 0f, scale: 1.399999976158142f),
            Sprite(StarIds[2], StarContainerId, StarImages[2], new Vector2(-20f, -28f), 22f, 0f, scale: 1.399999976158142f),
            Sprite(StarIds[3], StarContainerId, StarImages[3], new Vector2(-20f, -28f), 23f, 0f, scale: 1.399999976158142f),
            Sprite(StarIds[4], StarContainerId, StarImages[4], new Vector2(-20f, -28f), 24f, 0f, scale: 1.399999976158142f),
            Sprite(StarIds[5], StarContainerId, StarImages[5], new Vector2(-20f, -28f), 25f, 0f, scale: 1.399999976158142f),
            Sprite(StarIds[6], StarContainerId, StarImages[6], new Vector2(-20f, -28f), 26f, 0f, scale: 1.399999976158142f),
            Sprite(StarIds[7], StarContainerId, StarImages[7], new Vector2(-20f, -28f), 27f, 0f, scale: 1.399999976158142f),
            Sprite(StarGlowId, StarContainerId, "GH3_Pause_Bunny_Flame4", new Vector2(-20f, -28f), 30f, 1f, scale: 1.399999976158142f),
        };

        public static Vector2 PosTypeValue(string posType) => posType switch
        {
            "offscreen_rock_pos" => OffscreenRockPos,
            "offscreen_score_pos" => OffscreenScorePos,
            "rock_pos" => RockPos,
            "score_pos" => ScorePos,
            "counter_pos" => CounterPos,
            _ => Vector2.zero,
        };
    }
}
