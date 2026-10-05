using System.Collections.Generic;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Port of `career_hud_2d_elements` (guitar_hud_2d_career.q) and the builder rules
    /// of `create_2d_hud_elements` (guitar_hud_2d.q:5-337) for the ordinary 1P career /
    /// quickplay HUD. Everything here is authored 1280x720 data; the outer viewport
    /// transform is applied later by the renderer.
    /// </summary>
    internal static class Gh3HudLayout
    {
        // career_hud_2d_elements scalars (guitar_hud_2d_career.q:2-24).
        public static readonly Vector2 OffscreenRockPos = new(2000f, 610f);
        public static readonly Vector2 OffscreenScorePos = new(-500f, 560f);
        public static readonly Vector2 RockPos = new(1260f, 692f);
        public static readonly Vector2 ScorePos = new(300f, 650f);
        public static readonly Vector2 CounterPos = new(330f, 810f);
        public static readonly Vector2 OffscreenNoteStreakBarOff = new(0f, 800f);
        public const float Scale = 0.7f;
        public const float SmallBulbScale = 0.7f;
        public const float BigBulbScale = 1f;
        public const float ScoreFrameWidth = 175f;   // score_frame_width: rescale target once the score passes 5 characters

        // GH3 builds the highway from screen elements too: setup_highway (guitar_highway.q) gives
        // sidebar_left/right%p an explicit z_priority 3, and an explicit z is absolute, not parent-relative
        // (GH3.exe CScreenElement::SetZPriority 0x4FBD20 flags it, auto_set_z_priorities_recursive
        // 0x4FCA40 skips flagged elements). So HUD elements below 3, like WORMod's nixie (zoff 1),
        // draw under the highway sides.
        public const float HighwaySideZ = 3f;

        // Score text (guitar_hud_2d.q:290-309 + menu_setlist.q:displayText defaults).
        public static readonly Vector2 ScoreTextPos = new(222f, 70f);
        public const float ScoreTextScale = 1.1f;
        public const float ScoreTextZ = 20f;
        public const int ScoreFontSpacing = 5;
        public static readonly Vector2 DisplayTextShadowOffset = new(3f, 3f);

        // Counter digits (guitar_hud_2d.q:310-335): digit i (1..4) at (222,78)+i*(-37,0), z 25, center/center, noshadow.
        public static readonly Vector2 CounterDigitBase = new(222f, 78f);
        public static readonly Vector2 CounterDigitStep = new(-37f, 0f);
        public const float CounterDigitZ = 25f;
        public static readonly Color32 CounterDigit1Rgba = new(15, 15, 70, 200);
        public static readonly Color32 CounterDigitRgba = new(230, 230, 230, 200);

        // Notification hierarchy (guitar_hud.q:154-173, guitar_hud_2d.q:751-879, guitar_starpower.q:319-402).
        public static readonly Vector2 AlertBasePos = new(640f, 230f);       // hud_screen_elements[0].pos
        public static readonly Vector2 StreakPos = new(640f, 211f);
        public static readonly Vector2 GlowburstPos = AlertBasePos + new Vector2(0f, 7f);
        public static readonly Vector2 SpReadyPos = AlertBasePos - new Vector2(0f, 20f);
        public static readonly Vector2 LightningDims = new(800f, 100f);

        // FC label (GH3 Deluxe dx_fc_hud.q, adapted): text_a6 "PFC"/"FC" centred above the amp, rising 50
        // authored units over 0.2 s like the Deluxe label; Deluxe's own (340,440) left/top anchor overlaps
        // this layout's amp, whose top is at 650*0.7 = 455.
        public static readonly Vector2 FcLabelPos = new(300f, 398f);
        public static readonly Vector2 FcLabelHiddenPos = new(300f, 448f);
        public static readonly Color32 FcLabelRgba = new(240, 191, 116, 255);
        public static readonly Color32 FcLabelShadowRgba = new(204, 153, 102, 191);
        public static readonly Color32 FcGlowRgba = new(246, 188, 102, 255);
        public static readonly Color32[] FcGlowPulse = { new(250, 192, 110, 255), new(252, 196, 115, 255), new(255, 200, 120, 255), new(255, 204, 125, 255) };

        internal enum Kind { Container, Sprite }

        /// <summary>One `elements[]` entry. Fields mirror the script keys; absent keys use the builder defaults.</summary>
        internal sealed class Decl
        {
            public string Id;
            public Kind Kind;
            public string Parent;            // element_parent; null = HUD_2D_Container
            public string PosType;           // pos_type key for containers (rock_pos, score_pos, counter_pos, offscreen_*)
            public bool NoteStreakBar;       // adds offscreen_note_streak_bar_off
            public string Texture;
            public Vector2 PosOff;
            public Vector2? InitialPos;
            public Vector2? Dims;            // plain `dims`
            public Vector2? ElementDims;     // bulb `element_dims`, scaled by small/big bulb factor
            public bool SmallBulb;
            public float Z;                  // zoff
            public float Rot;
            public float Alpha = 1f;
            public Color32? Rgba;
            public Vector2 Just = JustLeftTop;
            public float SpriteScale = 1f;   // source `scale`: resize Dims after creation, never parent hierarchy scale
            public Gh3HudBlend Blend = Gh3HudBlend.Alpha;
            public bool Bulb;                // `container` sprite with tube/full children
            public string TubeTexture, TubeStarTexture; public Vector2 TubeDims, TubePosOff; public float TubeZ, TubeAlpha;
            public string FullTexture, FullStarTexture; public float FullZ, FullAlpha;
        }

        // Native just space (sub_4FBDD0): left/top = -1, center = 0, right/bottom = +1; numerics verbatim.
        // The anchor placed at pos is ((just + 1) / 2) * dims (compose 0x4FE8E0 uses the same (p+1)*0.5 form).
        public static readonly Vector2 JustLeftTop = new(-1f, -1f);
        public static readonly Vector2 JustCenterTop = new(0f, -1f);
        public static readonly Vector2 JustCenterCenter = new(0f, 0f);
        public static readonly Vector2 JustCenterBottom = new(0f, 1f);
        public static readonly Vector2 JustRightRight = new(1f, 1f);

        private static Decl Bulb(int index, float rot, bool small, float tubeY, float fullAlpha, bool initial)
        {
            return new Decl
            {
                Id = $"HUD2D_rock_tube_{index}", Kind = Kind.Sprite, Parent = $"HUD2D_bulb_container_{index}",
                Texture = "HUD_rock_tube", PosOff = new Vector2(0f, small ? -160f : -170f),
                InitialPos = initial ? new Vector2(0f, 0f) : null,
                ElementDims = new Vector2(64f, 128f), SmallBulb = small, Z = 0f, Just = JustCenterCenter, Bulb = true,
                TubeTexture = "HUD_rock_tube_glow_fill", TubeStarTexture = "HUD_rock_tube_glow_fill_b",
                TubeDims = new Vector2(64f, 16f), TubePosOff = new Vector2(0f, tubeY), TubeZ = 0.1f, TubeAlpha = 1f,
                FullTexture = "HUD_rock_tube_glow_full", FullStarTexture = "HUD_rock_tube_glow_full_b",
                FullZ = 0.2f, FullAlpha = fullAlpha,
            };
        }

        private static Decl BulbContainer(int index, float rot) => new()
        {
            Id = $"HUD2D_bulb_container_{index}", Kind = Kind.Container, Parent = "HUD2D_rock_container",
            PosOff = new Vector2(128f, 128f), Rot = rot,
        };

        private static Decl Lamp(string state, int index, string texture, float y, float z, float alpha) => new()
        {
            Id = $"HUD2D_score_light_{state}_{index}", Kind = Kind.Sprite, Parent = "HUD2D_score_container",
            Texture = texture, PosOff = new Vector2(0f, y), Z = z, Alpha = alpha,
        };

        private static Decl Nixie(string variant) => new()
        {
            Id = $"HUD2D_score_nixie_{variant}", Kind = Kind.Sprite, Parent = "HUD2D_score_container",
            Texture = $"HUD_score_nixie_{variant}", PosOff = new Vector2(70f, 90f), Z = 4f, Alpha = 0f,
        };

        /// <summary>Declarations in construction order (the z tie-break).</summary>
        public static readonly Decl[] Career =
        {
            new() { Id = "HUD2D_rock_container", Kind = Kind.Container, PosType = "offscreen_rock_pos" },
            new() { Id = "HUD2D_rock_glow", Kind = Kind.Sprite, Parent = "HUD2D_rock_container", Texture = "Char_Select_Hilite1",
                PosOff = new Vector2(-50f, -100f), Dims = new Vector2(350f, 350f), Rgba = new Color32(95, 205, 255, 255), Alpha = 0f, Z = -10f },
            new() { Id = "HUD2D_rock_body", Kind = Kind.Sprite, Parent = "HUD2D_rock_container", Texture = "HUD_rock_body", Z = 20f },
            new() { Id = "HUD2D_rock_BG_green", Kind = Kind.Sprite, Parent = "HUD2D_rock_body", Texture = "HUD_rock_BG_green", Z = 16f },
            new() { Id = "HUD2D_rock_BG_red", Kind = Kind.Sprite, Parent = "HUD2D_rock_body", Texture = "HUD_rock_BG_red", Z = 14f },
            new() { Id = "HUD2D_rock_BG_yellow", Kind = Kind.Sprite, Parent = "HUD2D_rock_body", Texture = "HUD_rock_BG_yellow", Z = 15f },
            // fastgh3 extension, not part of the original career layout.
            new() { Id = "HUD2D_rock_BG_nofail", Kind = Kind.Sprite, Parent = "HUD2D_rock_body", Texture = "HUD_rock_BG_nofail", Z = 13f, Alpha = 0f },
            new() { Id = "HUD2D_rock_lights_all", Kind = Kind.Sprite, Parent = "HUD2D_rock_body", Texture = "HUD_rock_lights_all", Z = 17f },
            new() { Id = "HUD2D_rock_lights_green", Kind = Kind.Sprite, Parent = "HUD2D_rock_body", Texture = "HUD_rock_lights_green",
                PosOff = new Vector2(128f, 0f), Z = 18f, Just = JustLeftTop, Alpha = 0f },
            new() { Id = "HUD2D_rock_lights_red", Kind = Kind.Sprite, Parent = "HUD2D_rock_body", Texture = "HUD_rock_lights_red",
                PosOff = new Vector2(0f, 0f), Z = 18f, Just = JustLeftTop, Alpha = 0f },
            new() { Id = "HUD2D_rock_lights_yellow", Kind = Kind.Sprite, Parent = "HUD2D_rock_body", Texture = "HUD_rock_lights_yellow",
                PosOff = new Vector2(128f, 0f), Z = 18f, Just = JustCenterTop, Alpha = 0f },
            new() { Id = "HUD2D_rock_needle", Kind = Kind.Sprite, Parent = "HUD2D_rock_body", Texture = "HUD_rock_needle",
                PosOff = new Vector2(132f, 165f), Z = 19f, Just = new Vector2(0.5f, 0.8f) },
            BulbContainer(1, -47.5f), Bulb(1, -47.5f, true, 40f, 0f, false),
            BulbContainer(2, -33f), Bulb(2, -33f, true, 40f, 0f, false),
            BulbContainer(3, -18.5f), Bulb(3, -18.5f, true, 40f, 0f, false),
            BulbContainer(4, 0f), Bulb(4, 0f, false, 32f, 1f, true),
            BulbContainer(5, 21f), Bulb(5, 21f, false, 32f, 1f, true),
            BulbContainer(6, 42f), Bulb(6, 42f, false, 32f, 1f, true),
            new() { Id = "HUD2D_score_container", Kind = Kind.Container, PosType = "offscreen_score_pos" },
            new() { Id = "HUD2D_score_body", Kind = Kind.Sprite, Parent = "HUD2D_score_container", Texture = "HUD_score_body", Z = 5f },
            new() { Id = "HUD2D_note_container", Kind = Kind.Container, PosType = "counter_pos", NoteStreakBar = true },
            new() { Id = "HUD2D_counter_body", Kind = Kind.Sprite, Parent = "HUD2D_note_container", Texture = "HUD_counter_body", Z = 9f },
            new() { Id = "HUD_counter_drum", Kind = Kind.Sprite, Parent = "HUD2D_note_container", Texture = "HUD_counter_drum", PosOff = new Vector2(4f, 40f), Z = 8f },
            new() { Id = "HUD2D_counter_drum_icon", Kind = Kind.Sprite, Parent = "HUD2D_note_container", Texture = "HUD_counter_drum_icon", PosOff = new Vector2(44f, 40f), Z = 26f },
            Lamp("unlit", 1, "HUD_score_light_0", 200f, 5f, 1f), Lamp("unlit", 2, "HUD_score_light_0", 170f, 5f, 1f),
            Lamp("unlit", 3, "HUD_score_light_0", 140f, 5f, 1f), Lamp("unlit", 4, "HUD_score_light_0", 110f, 5f, 1f),
            Lamp("unlit", 5, "HUD_score_light_0", 80f, 5f, 1f),
            Lamp("halflit", 1, "HUD_score_light_1", 200f, 5.1f, 0f), Lamp("halflit", 2, "HUD_score_light_1", 170f, 5.1f, 0f),
            Lamp("halflit", 3, "HUD_score_light_1", 140f, 5.1f, 0f), Lamp("halflit", 4, "HUD_score_light_1", 110f, 5.1f, 0f),
            Lamp("halflit", 5, "HUD_score_light_1", 80f, 5.1f, 0f),
            Lamp("allwaylit", 1, "HUD_score_light_2", 200f, 5.2f, 0f), Lamp("allwaylit", 2, "HUD_score_light_2", 170f, 5.2f, 0f),
            Lamp("allwaylit", 3, "HUD_score_light_2", 140f, 5.2f, 0f), Lamp("allwaylit", 4, "HUD_score_light_2", 110f, 5.2f, 0f),
            Lamp("allwaylit", 5, "HUD_score_light_2", 80f, 5.2f, 0f),
            Nixie("1a"), Nixie("2a"), Nixie("2b"), Nixie("3a"), Nixie("4a"), Nixie("4b"), Nixie("6b"), Nixie("8b"),
            new() { Id = "HUD2D_score_flash", Kind = Kind.Sprite, Parent = "HUD2D_score_container", Texture = "HUD_score_flash",
                Just = JustCenterCenter, PosOff = new Vector2(128f, 128f), Z = 20f, Alpha = 0f },
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
