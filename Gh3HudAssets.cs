using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Loads the original GH3 HUD art plus the three bitmap fonts once and packs every
    /// image into a single atlas so the whole HUD (sprites, nixies, lamps, glyphs,
    /// effects) can be one mesh sorted strictly by z priority. The GH3 draw order
    /// interleaves textures freely (counter icon z=26 above num_a7 digits z=25 above
    /// the drum z=8), so one material per source texture could not reproduce it.
    /// </summary>
    /// <remarks>
    /// Every file is looked up in the selected theme's Hud/ first and then in
    /// Mods/ClonZones/fallback/hud, per file, so a theme can override any single image or
    /// font. Fonts are the editable pair `<name>.png` + `<name>.font.txt` produced by
    /// tools/gh3_font_export.py from the original .fnt.xen. The PNGs are the decoded
    /// originals with their transparent padding; nothing is cropped or recoloured. Atlas
    /// cells get a 2 texel edge-extended gutter so bilinear sampling at a quad edge behaves
    /// like the original clamp addressing.
    /// </remarks>
    internal static class Gh3HudAssets
    {
        public const string HudDir = "Hud";
        private const int Gutter = 2;
        private const int AtlasWidth = 2048;
        /// <summary>Relative to the Mods/ClonZones folder that holds the themes: `Mods/ClonZones/fallback/hud`.</summary>
        public const string FallbackDir = "fallback/hud";

        /// <summary>Every texture the career single-player layout and notifications name.</summary>
        public static readonly string[] ImageNames =
        {
            "Char_Select_Hilite1",
            "HUD_counter_body", "HUD_counter_drum", "HUD_counter_drum_icon",
            "HUD_lightning_01", "HUD_lightning_03", "HUD_lightning_05", "HUD_lightning_07",
            "HUD_rock_BG_green", "HUD_rock_BG_red", "HUD_rock_BG_yellow", "HUD_rock_body",
            "HUD_rock_lights_all", "HUD_rock_lights_green", "HUD_rock_lights_red", "HUD_rock_lights_yellow",
            "HUD_rock_needle", "HUD_rock_tube",
            "HUD_rock_tube_glow_fill", "HUD_rock_tube_glow_fill_b", "HUD_rock_tube_glow_full", "HUD_rock_tube_glow_full_b",
            "HUD_score_body", "HUD_score_flash",
            "HUD_score_light_0", "HUD_score_light_0_blue", "HUD_score_light_0_green", "HUD_score_light_0_purple",
            "HUD_score_light_1", "HUD_score_light_1_blue", "HUD_score_light_1_green", "HUD_score_light_1_purple",
            "HUD_score_light_2", "HUD_score_light_2_blue", "HUD_score_light_2_green", "HUD_score_light_2_purple",
            "HUD_score_nixie_1a", "HUD_score_nixie_2a", "HUD_score_nixie_2b", "HUD_score_nixie_3a",
            "HUD_score_nixie_4a", "HUD_score_nixie_4b", "HUD_score_nixie_6b", "HUD_score_nixie_8b",
        };

        /// <summary>Authored GH3 dimensions, used only to flag re-authored art in the log.</summary>
        private static readonly Dictionary<string, (int w, int h)> AuthoredDims = new()
        {
            ["Char_Select_Hilite1"] = (64, 64),
            ["HUD_counter_body"] = (256, 128), ["HUD_counter_drum"] = (256, 64), ["HUD_counter_drum_icon"] = (64, 64),
            ["HUD_lightning_01"] = (256, 128), ["HUD_lightning_03"] = (256, 128), ["HUD_lightning_05"] = (256, 128), ["HUD_lightning_07"] = (256, 128),
            ["HUD_rock_BG_green"] = (256, 256), ["HUD_rock_BG_red"] = (256, 256), ["HUD_rock_BG_yellow"] = (256, 256), ["HUD_rock_body"] = (256, 256),
            ["HUD_rock_lights_all"] = (256, 128), ["HUD_rock_lights_green"] = (128, 128), ["HUD_rock_lights_red"] = (128, 128), ["HUD_rock_lights_yellow"] = (128, 128),
            ["HUD_rock_needle"] = (16, 128), ["HUD_rock_tube"] = (64, 128),
            ["HUD_rock_tube_glow_fill"] = (64, 16), ["HUD_rock_tube_glow_fill_b"] = (64, 16), ["HUD_rock_tube_glow_full"] = (64, 128), ["HUD_rock_tube_glow_full_b"] = (64, 128),
            ["HUD_score_body"] = (256, 256), ["HUD_score_flash"] = (128, 128),
        };

        public static readonly string[] FontNames = { "num_a9", "num_a7", "text_a6" };

        private static readonly Dictionary<string, Gh3HudRegion> Regions = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Gh3HudFont> Fonts = new(StringComparer.Ordinal);

        public static Texture2D Atlas { get; private set; }
        public static bool IsLoaded { get; private set; }
        /// <summary>True only when every image and every font decoded; the HUD fails closed otherwise.</summary>
        public static bool IsComplete { get; private set; }
        public static string MissingSummary { get; private set; } = "";

        public static Gh3HudRegion Region(string name) => Regions.TryGetValue(name, out var region) ? region : default;
        public static Gh3HudFont Font(string name) => Fonts.TryGetValue(name, out var font) ? font : null;

        public static void LoadAll(string assetRoot, MelonLogger.Instance log)
        {
            if (IsLoaded) return;
            IsLoaded = true;
            if (string.IsNullOrEmpty(assetRoot))
            {
                log.Warning("[ClonZones] Gh3HudAssets: no asset root; GH3 HUD unavailable.");
                return;
            }
            try
            {
                string themeDir = Path.Combine(assetRoot, HudDir);
                string parent = Path.GetDirectoryName(assetRoot.TrimEnd('\\', '/'));
                string fallbackDir = parent != null ? Path.Combine(parent, FallbackDir) : null;
                bool anyDir = Directory.Exists(themeDir) || (fallbackDir != null && Directory.Exists(fallbackDir));
                if (!anyDir)
                {
                    MissingSummary = "no Hud folder";
                    log.Warning($"[ClonZones] Gh3HudAssets: neither {themeDir} nor {fallbackDir} exists; vanilla HUD retained.");
                    return;
                }
                int fromTheme = 0;
                string Locate(string file)
                {
                    string themed = Path.Combine(themeDir, file);
                    if (File.Exists(themed)) { fromTheme++; return themed; }
                    if (fallbackDir != null)
                    {
                        string shared = Path.Combine(fallbackDir, file);
                        if (File.Exists(shared)) return shared;
                    }
                    return themed;
                }

                var missing = new List<string>();
                var bitmaps = new List<(string name, Gh3HudBitmap bitmap)>();
                foreach (string name in ImageNames)
                {
                    var bitmap = TryLoadPng(Locate(name + ".png"), name, log);
                    if (bitmap == null) { missing.Add(name + ".png"); continue; }
                    bitmaps.Add((name, bitmap));
                }
                var fontBitmaps = new List<(Gh3HudFont font, Gh3HudBitmap bitmap)>();
                foreach (string name in FontNames)
                {
                    string metricsPath = Locate(name + ".font.txt");
                    if (!File.Exists(metricsPath)) { missing.Add(name + ".font.txt"); continue; }
                    Gh3HudBitmap page = TryLoadPng(Locate(name + ".png"), name, log);
                    if (page == null) { missing.Add(name + ".png"); continue; }
                    try
                    {
                        Gh3HudFont font = Gh3HudFont.Parse(name, File.ReadAllText(metricsPath), page.Width, page.Height);
                        fontBitmaps.Add((font, page));
                        Fonts[name] = font;
                        log.Msg($"[ClonZones] Loaded font {name}: {font.GlyphCount} glyphs, page {page.Width}x{page.Height}, lineheight {font.LineHeight}.");
                    }
                    catch (Exception ex)
                    {
                        missing.Add(name + ".font.txt");
                        log.Warning($"[ClonZones] Gh3HudAssets: {name}.font.txt unusable ({ex.Message}).");
                    }
                }

                var packer = new Packer(AtlasWidth);
                var texelRegions = new List<(string name, Gh3HudRegion texels)>();
                foreach (var (name, bitmap) in bitmaps)
                    texelRegions.Add((name, packer.Add(bitmap)));
                var fontRegions = new List<(Gh3HudFont font, Gh3HudRegion texels)>();
                foreach (var (font, page) in fontBitmaps)
                    fontRegions.Add((font, packer.Add(page)));

                Atlas = packer.Build("clonzones_gh3_hud_atlas");
                foreach (var (name, texels) in texelRegions)
                    Regions[name] = ToUv(texels, Atlas.height);
                foreach (var (font, texels) in fontRegions)
                    font.AssignAtlas(ToUv(texels, Atlas.height));
                IsComplete = missing.Count == 0;
                MissingSummary = string.Join(", ", missing);
                if (IsComplete)
                    log.Msg($"[ClonZones] Gh3HudAssets ready: {bitmaps.Count} images, {fontBitmaps.Count} fonts in {Atlas.width}x{Atlas.height} atlas ({fromTheme} from theme {HudDir}/, rest from {FallbackDir}).");
                else
                    log.Warning($"[ClonZones] Gh3HudAssets incomplete ({missing.Count} missing: {MissingSummary}); vanilla HUD retained.");
            }
            catch (Exception ex)
            {
                IsComplete = false;
                MissingSummary = ex.Message;
                log.Error($"[ClonZones] Gh3HudAssets.LoadAll fatal: {ex}");
            }
        }


        private static Gh3HudBitmap TryLoadPng(string path, string name, MelonLogger.Instance log)
        {
            if (!File.Exists(path))
            {
                log.Warning($"[ClonZones] Gh3HudAssets: {HudDir}/{name}.png missing.");
                return null;
            }
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path)))
                    throw new InvalidDataException("Unity could not decode the PNG");
                if (AuthoredDims.TryGetValue(name, out var authored) && (tex.width != authored.w || tex.height != authored.h))
                    log.Warning($"[ClonZones] Gh3HudAssets: {name}.png is {tex.width}x{tex.height}, GH3 authored {authored.w}x{authored.h}; sprite dims follow the script, UVs cover the whole image.");
                // Unity hands rows bottom-up; flip once into the top-down convention the packer expects.
                Color32[] src = tex.GetPixels32();
                int w = tex.width, h = tex.height;
                var dst = new Color32[src.Length];
                for (int y = 0; y < h; y++)
                    Array.Copy(src, (h - 1 - y) * w, dst, y * w, w);
                return new Gh3HudBitmap(w, h, dst);
            }
            catch (Exception ex)
            {
                log.Warning($"[ClonZones] Gh3HudAssets: {HudDir}/{name}.png unusable ({ex.Message}).");
                return null;
            }
            finally
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }

        /// <summary>
        /// Shelf packer: rows of decreasing height, left to right. Plenty for ~50
        /// rectangles; the atlas height is rounded up to a power of two at build.
        /// </summary>
        private sealed class Packer
        {
            private readonly int _width;
            private readonly List<(Gh3HudBitmap bitmap, int x, int y)> _placed = new();
            private int _cursorX, _cursorY, _shelfHeight, _usedHeight;

            public Packer(int width) { _width = width; }

            public Gh3HudRegion Add(Gh3HudBitmap bitmap)
            {
                int cellW = bitmap.Width + Gutter * 2, cellH = bitmap.Height + Gutter * 2;
                if (cellW > _width) throw new InvalidDataException($"image {bitmap.Width} wide exceeds the atlas");
                if (_cursorX + cellW > _width)
                {
                    _cursorY += _shelfHeight; _cursorX = 0; _shelfHeight = 0;
                }
                int x = _cursorX + Gutter, y = _cursorY + Gutter;
                _placed.Add((bitmap, x, y));
                _cursorX += cellW;
                if (cellH > _shelfHeight) _shelfHeight = cellH;
                _usedHeight = _cursorY + _shelfHeight;
                // V is flipped at build time once the final height is known; store texel coords now.
                return new Gh3HudRegion(bitmap.Width, bitmap.Height, x, y, x + bitmap.Width, y + bitmap.Height);
            }

            public Texture2D Build(string name)
            {
                int height = Mathf.NextPowerOfTwo(Math.Max(_usedHeight, 1));
                var pixels = new Color32[_width * height];
                foreach (var (bitmap, x, y) in _placed)
                {
                    int w = bitmap.Width, h = bitmap.Height;
                    for (int row = -Gutter; row < h + Gutter; row++)
                    {
                        int srcRow = Math.Clamp(row, 0, h - 1);
                        // Unity rows are bottom-up; top-down source row `y+row` lands on atlas row height-1-(y+row).
                        int dstRow = height - 1 - (y + row);
                        int dstBase = dstRow * _width + x;
                        int srcBase = srcRow * w;
                        for (int col = -Gutter; col < w + Gutter; col++)
                            pixels[dstBase + col] = bitmap.Pixels[srcBase + Math.Clamp(col, 0, w - 1)];
                    }
                }
                var tex = new Texture2D(_width, height, TextureFormat.RGBA32, false, false)
                {
                    name = name,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                };
                tex.SetPixels32(pixels);
                tex.Apply(false, true);
                FinalHeight = height;
                return tex;
            }

            public int FinalHeight { get; private set; }
        }

        /// <summary>Convert a texel-space region from <see cref="Packer.Add"/> into UVs for the built atlas.</summary>
        private static Gh3HudRegion ToUv(Gh3HudRegion texels, int atlasHeight)
        {
            float w = AtlasWidth, h = atlasHeight;
            // V runs bottom-up in Unity; texel rows were stored top-down.
            return new Gh3HudRegion(texels.Width, texels.Height,
                texels.U0 / w, (h - texels.V1) / h, texels.U1 / w, (h - texels.V0) / h);
        }
    }
}
