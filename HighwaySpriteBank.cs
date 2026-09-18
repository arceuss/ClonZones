using System;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Loads the GH3 highway/field art from the asset root Core selected and keeps
    /// it alive for the process lifetime, matching the note/fret/flame/sustain banks.
    /// The GH3 field renderer owns all geometry, scaling and colour; this bank only
    /// decodes textures and reports what is present.
    /// </summary>
    /// <remarks>
    /// Alpha semantics: these five PNGs carry authored straight (non-premultiplied)
    /// alpha, so they are uploaded untouched. No black keying, unlike
    /// <see cref="FlameSpriteBank"/>/<see cref="SustainFxBank"/>, whose sources ship
    /// fully opaque (one distinct alpha value) and therefore have to synthesise
    /// coverage from luminance. Measured on GH3+ Default, which is byte-identical to
    /// NotClon's engine art:
    ///   fretbar_small  1024x16  14 distinct alpha values, 62.5% cleared
    ///   fretbar_medium 1024x16  transparent margin is white RGB at alpha 0
    ///   fretbar_large  1024x16  93.5% opaque, 34 partial-alpha pixels
    ///   string         32x512   236 distinct alpha values, opaque core is dark grey
    ///   sidebar2d      32x512   248 distinct alpha values, 12.1% partial alpha
    /// alpha = max(r,g,b) would turn every cleared pixel of the bars opaque white
    /// (fretbar_small: all 10240 transparent pixels are bright) and would erase the
    /// lane string, whose visible core averages RGB 70 (mean alpha 50.7 -> 12.8, and
    /// 396 currently visible pixels would go fully clear).
    ///
    /// Filtering follows NotClon's GH3 board loads (renderer.cpp: "plain display-space
    /// 2D art ... no srgb, no flip, no mipmaps" with repeat off): bilinear, clamp, no
    /// mip chain. Bars shrink to 0.15 scale at the horizon, so if far-row shimmer ever
    /// shows up, a mip chain is the knob to turn, and it is a deliberate parity
    /// deviation rather than an oversight.
    ///
    /// Theme packs re-author this art at other sizes (Rainbow Zones and Drihscol ship
    /// fretbar_medium/large at 1024x28 and sidebar2d at 400x512), so a size mismatch
    /// warns and still loads. Field geometry must derive UVs and quad aspect from
    /// each texture's own width/height, never from the GH3 authored numbers below.
    /// </remarks>
    internal static class HighwaySpriteBank
    {
        private const string HighwayDir = "Highway";

        // GH3 PC authored source sizes (sys_fretbar_*, sys_string01, sys_sidebar2D).
        // Used only to flag re-authored theme art in the log.
        private const int FretbarWidth = 1024;
        private const int FretbarHeight = 16;
        private const int StripWidth = 32;
        private const int StripHeight = 512;

        private static Texture2D _small;
        private static Texture2D _medium;
        private static Texture2D _large;
        private static Texture2D _string;
        private static Texture2D _sidebar;

        /// <summary>Weak beat and synthesized eighth bars (`sys_fretbar_small`).</summary>
        public static Texture2D Small => _small;

        /// <summary>Ordinary beat bars (`sys_fretbar_medium`).</summary>
        public static Texture2D Medium => _medium;

        /// <summary>Measure bars (`sys_fretbar_large`).</summary>
        public static Texture2D Large => _large;

        /// <summary>Single lane string (`sys_string01`), drawn once per lane.</summary>
        public static Texture2D String => _string;

        /// <summary>Left sidebar (`sys_sidebar2D`); the right side mirrors it.</summary>
        public static Texture2D Sidebar => _sidebar;

        /// <summary>True once <see cref="LoadAll"/> has run against a usable asset root.</summary>
        public static bool IsLoaded { get; private set; }

        /// <summary>
        /// True when at least one bar weight has art. Each weight is independent:
        /// check <see cref="Small"/>/<see cref="Medium"/>/<see cref="Large"/> before
        /// drawing that class instead of substituting another weight's texture.
        /// </summary>
        public static bool HasFretbarArt => _small != null || _medium != null || _large != null;

        public static bool HasStringArt => _string != null;

        public static bool HasSidebarArt => _sidebar != null;

        /// <summary>
        /// Decodes the highway art once. Every asset is optional: a missing or
        /// undecodable file leaves its property null and warns, so the renderer can
        /// drop that layer and leave the vanilla visual in place. Safe to call again;
        /// later calls keep the textures already loaded.
        /// </summary>
        public static void LoadAll(string assetRoot, MelonLogger.Instance log)
        {
            if (IsLoaded)
                return;

            if (string.IsNullOrEmpty(assetRoot))
            {
                log.Warning("[ClonZones] HighwaySpriteBank: no asset root; GH3 field art unavailable.");
                return;
            }

            try
            {
                _small = TryLoad(assetRoot, "fretbar_small.png", "weak/eighth fretbars", FretbarWidth, FretbarHeight, log);
                _medium = TryLoad(assetRoot, "fretbar_medium.png", "beat fretbars", FretbarWidth, FretbarHeight, log);
                _large = TryLoad(assetRoot, "fretbar_large.png", "measure fretbars", FretbarWidth, FretbarHeight, log);
                _string = TryLoad(assetRoot, "string.png", "lane strings", StripWidth, StripHeight, log);
                _sidebar = TryLoad(assetRoot, "sidebar2d.png", "sidebars", StripWidth, StripHeight, log);

                IsLoaded = true;
                log.Msg("[ClonZones] HighwaySpriteBank ready: " +
                    $"small={Describe(_small)}, medium={Describe(_medium)}, large={Describe(_large)}, " +
                    $"string={Describe(_string)}, sidebar={Describe(_sidebar)}.");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] HighwaySpriteBank.LoadAll fatal: {ex}");
            }
        }

        private static Texture2D TryLoad(
            string assetRoot,
            string fileName,
            string layer,
            int authoredWidth,
            int authoredHeight,
            MelonLogger.Instance log)
        {
            var full = Path.Combine(assetRoot, HighwayDir, fileName);
            if (!File.Exists(full))
            {
                log.Warning($"[ClonZones] HighwaySpriteBank: {HighwayDir}/{fileName} missing; GH3 {layer} stay off and the vanilla layer is left alone.");
                return null;
            }

            Texture2D tex = null;
            try
            {
                var bytes = File.ReadAllBytes(full);
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes))
                    throw new InvalidDataException($"Unity could not decode {HighwayDir}/{fileName}.");

                if (tex.width <= 0 || tex.height <= 0)
                    throw new InvalidDataException($"{HighwayDir}/{fileName} decoded to {tex.width}x{tex.height}.");

                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = $"clonzones_highway_{Path.GetFileNameWithoutExtension(fileName)}_tex";
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

                if (tex.width != authoredWidth || tex.height != authoredHeight)
                {
                    log.Warning($"[ClonZones] HighwaySpriteBank: {HighwayDir}/{fileName} is {tex.width}x{tex.height}, GH3 authored {authoredWidth}x{authoredHeight}. " +
                        "Re-authored theme art is supported; the field uses the texture's own size.");
                }

                log.Msg($"[ClonZones] Loaded {HighwayDir}/{fileName} → highway {layer} ({tex.width}x{tex.height}).");
                return tex;
            }
            catch (Exception ex)
            {
                log.Warning($"[ClonZones] HighwaySpriteBank: {HighwayDir}/{fileName} unusable ({ex.Message}); GH3 {layer} stay off.");
                Discard(tex, log);
                return null;
            }
        }

        private static void Discard(Texture2D tex, MelonLogger.Instance log)
        {
            if (tex == null)
                return;

            try
            {
                UnityEngine.Object.Destroy(tex);
            }
            catch (Exception ex)
            {
                log.Warning($"[ClonZones] HighwaySpriteBank: could not release a half-decoded texture ({ex.Message}).");
            }
        }

        private static string Describe(Texture2D tex) =>
            tex != null ? $"{tex.width}x{tex.height}" : "missing";
    }
}
