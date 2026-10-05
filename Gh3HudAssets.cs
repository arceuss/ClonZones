using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// One owned GH3 HUD asset bank. Every attach gets a fresh bank so atlas regions,
    /// fonts, source roots, and generations cannot leak between styles or themes.
    /// </summary>
    /// <remarks>
    /// Images resolve from the selected theme's hud/&lt;style&gt;/ directory and then
    /// the same-style fallback. Fonts resolve as page/metrics pairs from one root.
    /// The GH3 shelf packer and 2048-wide atlas are intentionally unchanged.
    /// </remarks>
    internal sealed class Gh3HudAssets : IDisposable
    {
        public const string HudDir = HudAssetPaths.HudDirectory;
        public const string FallbackDir = "fallback/hud";
        private const int Gutter = 2;
        private const int AtlasWidth = 2048;

        /// <summary>Every texture the career single-player layout and notifications name.</summary>
        public static readonly string[] ImageNames =
        {
            "Char_Select_Hilite1",
            "HUD_counter_body", "HUD_counter_drum", "HUD_counter_drum_icon",
            "HUD_lightning_01", "HUD_lightning_03", "HUD_lightning_05", "HUD_lightning_07",
            "HUD_rock_BG_green", "HUD_rock_BG_red", "HUD_rock_BG_yellow", "HUD_rock_BG_nofail", "HUD_rock_body",
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
            ["HUD_rock_BG_green"] = (256, 256), ["HUD_rock_BG_red"] = (256, 256), ["HUD_rock_BG_yellow"] = (256, 256), ["HUD_rock_BG_nofail"] = (256, 256), ["HUD_rock_body"] = (256, 256),
            ["HUD_rock_lights_all"] = (256, 128), ["HUD_rock_lights_green"] = (128, 128), ["HUD_rock_lights_red"] = (128, 128), ["HUD_rock_lights_yellow"] = (128, 128),
            ["HUD_rock_needle"] = (16, 128), ["HUD_rock_tube"] = (64, 128),
            ["HUD_rock_tube_glow_fill"] = (64, 16), ["HUD_rock_tube_glow_fill_b"] = (64, 16), ["HUD_rock_tube_glow_full"] = (64, 128), ["HUD_rock_tube_glow_full_b"] = (64, 128),
            ["HUD_score_body"] = (256, 256), ["HUD_score_flash"] = (128, 128),
        };

        public static readonly string[] FontNames = { "num_a9", "num_a7", "text_a6" };

        private readonly Dictionary<string, Gh3HudRegion> _regions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Gh3HudFont> _fonts = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<string> _imageNames;
        private readonly IReadOnlyList<string> _fontNames;
        private readonly MelonLogger.Instance _log;
        private bool _disposed;

        public HudAssetIdentity Identity { get; }
        public PresentationStyle Style => Identity.Style;
        public string AssetRoot => Identity.AssetRoot;
        public string SourceRoot => Identity.AssetRoot;
        public long Generation => Identity.Generation;
        public Texture2D Atlas { get; private set; }
        public bool IsLoaded { get; private set; }
        public bool IsDisposed => _disposed;
        /// <summary>True only when every requested image and every font decoded.</summary>
        public bool IsComplete { get; private set; }
        public string MissingSummary { get; private set; } = "";

        public Gh3HudAssets(
            PresentationStyle style,
            string assetRoot,
            long generation,
            IReadOnlyList<string> imageNames,
            IReadOnlyList<string> fontNames,
            MelonLogger.Instance log)
        {
            Identity = new HudAssetIdentity(style, assetRoot, generation);
            _imageNames = imageNames ?? Array.Empty<string>();
            _fontNames = fontNames ?? Array.Empty<string>();
            _log = log;
            LoadAll();
        }

        public Gh3HudRegion Region(string name) =>
            !_disposed && _regions.TryGetValue(name, out Gh3HudRegion region) ? region : default;

        public Gh3HudFont Font(string name) =>
            !_disposed && _fonts.TryGetValue(name, out Gh3HudFont font) ? font : null;

        private void LoadAll()
        {
            if (IsLoaded || _disposed) return;
            IsLoaded = true;
            if (string.IsNullOrEmpty(AssetRoot))
            {
                MissingSummary = "no asset root";
                Warn("Gh3HudAssets: no asset root; GH3 HUD unavailable.");
                return;
            }

            try
            {
                string themeDir = HudAssetPaths.ThemeHudDirectory(AssetRoot, Style);
                string fallbackDir = HudAssetPaths.FallbackHudDirectory(AssetRoot, Style);
                bool anyDir = Directory.Exists(themeDir) || (fallbackDir != null && Directory.Exists(fallbackDir));
                if (!anyDir)
                {
                    MissingSummary = "no namespaced hud folder";
                    Warn($"Gh3HudAssets: neither {themeDir} nor {fallbackDir} exists; vanilla HUD retained.");
                    return;
                }

                int fromTheme = 0;
                var missing = new List<string>();
                var bitmaps = new List<(string name, Gh3HudBitmap bitmap)>();
                foreach (string name in _imageNames)
                {
                    HudImageResolution resolution = HudAssetPaths.ResolveImage(AssetRoot, Style, name);
                    if (!resolution.Exists)
                    {
                        missing.Add(name + ".png");
                        Warn($"Gh3HudAssets: {StyleDirectory()}/{name}.png missing in theme and same-style fallback.");
                        continue;
                    }

                    Gh3HudBitmap bitmap = TryLoadPng(resolution.Path, name);
                    if (bitmap == null)
                    {
                        // ResolveImage intentionally selected the present theme file
                        // first; a corrupt present image must not fall through.
                        missing.Add(name + ".png");
                        continue;
                    }
                    if (resolution.Source == HudAssetSource.Theme) fromTheme++;
                    bitmaps.Add((name, bitmap));
                }

                var fontBitmaps = new List<(Gh3HudFont font, Gh3HudBitmap bitmap)>();
                foreach (string name in _fontNames)
                {
                    HudFontPairResolution pair = HudAssetPaths.ResolveFontPair(AssetRoot, Style, name);
                    if (pair.ThemeWasIncomplete)
                    {
                        Warn(pair.IsFallback
                            ? $"Gh3HudAssets: {StyleDirectory()}/{name} font override is incomplete; using the complete same-style fallback pair."
                            : $"Gh3HudAssets: {StyleDirectory()}/{name} font override is incomplete and no complete same-style fallback pair exists.");
                    }
                    if (!pair.IsComplete)
                    {
                        missing.Add(name + ".font pair");
                        Warn($"Gh3HudAssets: {StyleDirectory()}/{name}.png + {name}.font.txt do not form a complete same-root pair.");
                        continue;
                    }

                    Gh3HudBitmap page = TryLoadPng(pair.PagePath, name);
                    if (page == null)
                    {
                        missing.Add(name + ".png");
                        continue;
                    }
                    try
                    {
                        Gh3HudFont font = Gh3HudFont.Parse(name, File.ReadAllText(pair.MetricsPath), page.Width, page.Height);
                        fontBitmaps.Add((font, page));
                        _fonts[name] = font;
                        if (pair.Source == HudAssetSource.Theme) fromTheme++;
                        Msg($"Loaded font {name}: {font.GlyphCount} glyphs, page {page.Width}x{page.Height}, lineheight {font.LineHeight}.");
                    }
                    catch (Exception ex)
                    {
                        missing.Add(name + ".font.txt");
                        Warn($"Gh3HudAssets: {name}.font.txt unusable ({ex.Message}).");
                    }
                }

                if (bitmaps.Count != 0 || fontBitmaps.Count != 0)
                {
                    int width = AtlasWidth;
                    int limit = SystemInfo.maxTextureSize;
                    if (Style == PresentationStyle.Wormod)
                    {
                        foreach ((string name, Gh3HudBitmap bitmap) in bitmaps)
                            width = Math.Max(width, checked(bitmap.Width + Gutter * 2));
                        foreach ((Gh3HudFont font, Gh3HudBitmap page) in fontBitmaps)
                            width = Math.Max(width, checked(page.Width + Gutter * 2));
                        width = Mathf.NextPowerOfTwo(width);
                    }
                    if (width > limit)
                        throw new InvalidDataException($"HUD atlas needs width {width}; device texture limit is {limit}");

                    var texelRegions = new List<(string name, Gh3HudRegion texels)>(bitmaps.Count);
                    var fontRegions = new List<(Gh3HudFont font, Gh3HudRegion texels)>(fontBitmaps.Count);
                    Packer packer;
                    while (true)
                    {
                        packer = new Packer(width);
                        texelRegions.Clear();
                        fontRegions.Clear();
                        foreach ((string name, Gh3HudBitmap bitmap) in bitmaps)
                            texelRegions.Add((name, packer.Add(bitmap)));
                        foreach ((Gh3HudFont font, Gh3HudBitmap page) in fontBitmaps)
                            fontRegions.Add((font, packer.Add(page)));
                        if (packer.Height <= limit) break;
                        // Keep GH3's UV layout fixed. WOR sheets may need wider shelves,
                        // but never resize the source images to make them fit.
                        if (Style == PresentationStyle.Gh3 || width >= limit)
                            throw new InvalidDataException($"HUD atlas needs {width}x{packer.Height}; device texture limit is {limit}");
                        width = Math.Min(checked(width * 2), limit);
                    }
                    Atlas = packer.Build($"clonzones_{StyleDirectory()}_hud_atlas");
                    foreach ((string name, Gh3HudRegion texels) in texelRegions)
                        _regions[name] = ToUv(texels, Atlas.width, Atlas.height);
                    foreach ((Gh3HudFont font, Gh3HudRegion texels) in fontRegions)
                        font.AssignAtlas(ToUv(texels, Atlas.width, Atlas.height));
                }

                IsComplete = Atlas != null && missing.Count == 0;
                MissingSummary = Atlas == null && missing.Count == 0 ? "empty HUD asset list" : string.Join(", ", missing);
                if (IsComplete)
                    Msg($"Gh3HudAssets ready: {bitmaps.Count} images, {fontBitmaps.Count} fonts in {Atlas?.width ?? 0}x{Atlas?.height ?? 0} atlas ({fromTheme} from theme {HudAssetPaths.HudDirectory}/{StyleDirectory()}/, rest from {FallbackDir}/{StyleDirectory()}/).");
                else
                    Warn($"Gh3HudAssets incomplete ({missing.Count} missing or unusable: {MissingSummary}); vanilla HUD retained.");
            }
            catch (Exception ex)
            {
                IsComplete = false;
                MissingSummary = ex.Message;
                ReleaseLoadedResources();
                Error($"Gh3HudAssets.LoadAll fatal: {ex}");
            }
        }

        private Gh3HudBitmap TryLoadPng(string path, string name)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Warn($"Gh3HudAssets: {StyleDirectory()}/{name}.png missing.");
                return null;
            }

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path)))
                    throw new InvalidDataException("Unity could not decode the PNG");
                if (Style == PresentationStyle.Gh3 && AuthoredDims.TryGetValue(name, out (int w, int h) authored) &&
                    (tex.width != authored.w || tex.height != authored.h))
                    Warn($"Gh3HudAssets: {name}.png is {tex.width}x{tex.height}, GH3 authored {authored.w}x{authored.h}; sprite dims follow the script, UVs cover the whole image.");

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
                Warn($"Gh3HudAssets: {path} unusable ({ex.Message}); readiness fails closed.");
                return null;
            }
            finally
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }

        private string StyleDirectory() => HudAssetPaths.StyleDirectory(Style);
        private void Msg(string message) => _log?.Msg("[ClonZones] " + message);
        private void Warn(string message) => _log?.Warning("[ClonZones] " + message);
        private void Error(string message) => _log?.Error("[ClonZones] " + message);

        private void ReleaseLoadedResources()
        {
            if (Atlas != null) UnityEngine.Object.Destroy(Atlas);
            Atlas = null;
            _regions.Clear();
            _fonts.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsComplete = false;
            ReleaseLoadedResources();
        }

        /// <summary>
        /// Shelf packer: rows of decreasing height, left to right. The 2048-wide
        /// GH3 sheet and insertion order are part of phase-B parity.
        /// </summary>
        private sealed class Packer
        {
            private readonly int _width;
            private readonly List<(Gh3HudBitmap bitmap, int x, int y)> _placed = new();
            private int _cursorX, _cursorY, _shelfHeight, _usedHeight;

            public Packer(int width) { _width = width; }
            public int Height => Mathf.NextPowerOfTwo(Math.Max(_usedHeight, 1));

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
                return new Gh3HudRegion(bitmap.Width, bitmap.Height, x, y, x + bitmap.Width, y + bitmap.Height);
            }

            public Texture2D Build(string name)
            {
                int height = Height;
                var pixels = new Color32[_width * height];
                foreach ((Gh3HudBitmap bitmap, int x, int y) in _placed)
                {
                    int w = bitmap.Width, h = bitmap.Height;
                    for (int row = -Gutter; row < h + Gutter; row++)
                    {
                        int srcRow = Math.Clamp(row, 0, h - 1);
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
                try
                {
                    tex.SetPixels32(pixels);
                    tex.Apply(false, true);
                    return tex;
                }
                catch
                {
                    UnityEngine.Object.Destroy(tex);
                    throw;
                }
            }
        }

        /// <summary>Convert a texel-space region from <see cref="Packer.Add"/> into UVs.</summary>
        private static Gh3HudRegion ToUv(Gh3HudRegion texels, int atlasWidth, int atlasHeight)
        {
            float w = atlasWidth, h = atlasHeight;
            return new Gh3HudRegion(texels.Width, texels.Height,
                texels.U0 / w, (h - texels.V1) / h, texels.U1 / w, (h - texels.V0) / h);
        }
    }
}
