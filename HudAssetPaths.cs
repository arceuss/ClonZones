using System;
using System.IO;

namespace ClonZones
{
    /// <summary>Which root supplied a resolved HUD asset.</summary>
    internal enum HudAssetSource
    {
        Missing,
        Theme,
        Fallback,
    }

    /// <summary>
    /// The source identity of one HUD bank. Generation is assigned by the controller
    /// for every attach, so a rebuilt scene can never reuse an old style/root bank.
    /// </summary>
    internal readonly struct HudAssetIdentity : IEquatable<HudAssetIdentity>
    {
        public readonly PresentationStyle Style;
        public readonly string AssetRoot;
        public readonly long Generation;

        public HudAssetIdentity(PresentationStyle style, string assetRoot, long generation)
        {
            Style = style;
            AssetRoot = assetRoot ?? string.Empty;
            Generation = generation;
        }

        public bool Equals(HudAssetIdentity other) =>
            Style == other.Style && Generation == other.Generation &&
            string.Equals(AssetRoot, other.AssetRoot, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => obj is HudAssetIdentity other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(
            Style, StringComparer.OrdinalIgnoreCase.GetHashCode(AssetRoot ?? string.Empty), Generation);
        public static bool operator ==(HudAssetIdentity left, HudAssetIdentity right) => left.Equals(right);
        public static bool operator !=(HudAssetIdentity left, HudAssetIdentity right) => !left.Equals(right);
    }

    /// <summary>One image's selected-style source, without decoding or caching it.</summary>
    internal readonly struct HudImageResolution
    {
        public readonly string Path;
        public readonly string ThemePath;
        public readonly string FallbackPath;
        public readonly HudAssetSource Source;

        public HudImageResolution(string path, string themePath, string fallbackPath, HudAssetSource source)
        {
            Path = path;
            ThemePath = themePath;
            FallbackPath = fallbackPath;
            Source = source;
        }

        public bool Exists => Source != HudAssetSource.Missing;
        public bool IsFallback => Source == HudAssetSource.Fallback;
    }

    /// <summary>
    /// A font is selected as one page/metrics pair. ThemeWasIncomplete records that
    /// the selected theme attempted an override but the complete same-style fallback
    /// pair was retained instead.
    /// </summary>
    internal readonly struct HudFontPairResolution
    {
        public readonly string PagePath;
        public readonly string MetricsPath;
        public readonly string ThemePagePath;
        public readonly string ThemeMetricsPath;
        public readonly string FallbackPagePath;
        public readonly string FallbackMetricsPath;
        public readonly HudAssetSource Source;
        public readonly bool ThemeWasIncomplete;

        public HudFontPairResolution(
            string pagePath,
            string metricsPath,
            string themePagePath,
            string themeMetricsPath,
            string fallbackPagePath,
            string fallbackMetricsPath,
            HudAssetSource source,
            bool themeWasIncomplete)
        {
            PagePath = pagePath;
            MetricsPath = metricsPath;
            ThemePagePath = themePagePath;
            ThemeMetricsPath = themeMetricsPath;
            FallbackPagePath = fallbackPagePath;
            FallbackMetricsPath = fallbackMetricsPath;
            Source = source;
            ThemeWasIncomplete = themeWasIncomplete;
        }

        public bool IsComplete => Source != HudAssetSource.Missing &&
            !string.IsNullOrEmpty(PagePath) && !string.IsNullOrEmpty(MetricsPath);
        public bool IsFallback => Source == HudAssetSource.Fallback;
    }

    /// <summary>
    /// Pure path and pair policy for namespaced HUD assets. It deliberately never
    /// probes a flat Hud directory or the other presentation style.
    /// </summary>
    internal static class HudAssetPaths
    {
        public const string HudDirectory = "hud";
        public const string FallbackDirectory = "fallback";

        public static string StyleDirectory(PresentationStyle style) => style switch
        {
            PresentationStyle.Gh3 => "gh3",
            PresentationStyle.Wormod => "wormod",
            _ => throw new ArgumentOutOfRangeException(nameof(style), style, "unknown presentation style"),
        };

        public static string ThemeHudDirectory(string assetRoot, PresentationStyle style) =>
            string.IsNullOrEmpty(assetRoot) ? null : Path.Combine(assetRoot, HudDirectory, StyleDirectory(style));

        public static string FallbackHudDirectory(string assetRoot, PresentationStyle style)
        {
            string parent = ParentDirectory(assetRoot);
            return parent == null ? null : Path.Combine(parent, FallbackDirectory, HudDirectory, StyleDirectory(style));
        }

        public static HudImageResolution ResolveImage(string assetRoot, PresentationStyle style, string imageName)
        {
            string themeDir = ThemeHudDirectory(assetRoot, style);
            string themePath = themeDir == null ? null : Path.Combine(themeDir, imageName + ".png");
            string fallbackDir = FallbackHudDirectory(assetRoot, style);
            string fallbackPath = fallbackDir == null ? null : Path.Combine(fallbackDir, imageName + ".png");
            if (themePath != null && File.Exists(themePath))
                return new HudImageResolution(themePath, themePath, fallbackPath, HudAssetSource.Theme);
            if (fallbackPath != null && File.Exists(fallbackPath))
                return new HudImageResolution(fallbackPath, themePath, fallbackPath, HudAssetSource.Fallback);
            return new HudImageResolution(null, themePath, fallbackPath, HudAssetSource.Missing);
        }

        public static string ResolveImagePath(string assetRoot, PresentationStyle style, string imageName) =>
            ResolveImage(assetRoot, style, imageName).Path;

        public static HudFontPairResolution ResolveFontPair(string assetRoot, PresentationStyle style, string fontName)
        {
            string themeDir = ThemeHudDirectory(assetRoot, style);
            string fallbackDir = FallbackHudDirectory(assetRoot, style);
            string themePage = themeDir == null ? null : Path.Combine(themeDir, fontName + ".png");
            string themeMetrics = themeDir == null ? null : Path.Combine(themeDir, fontName + ".font.txt");
            string fallbackPage = fallbackDir == null ? null : Path.Combine(fallbackDir, fontName + ".png");
            string fallbackMetrics = fallbackDir == null ? null : Path.Combine(fallbackDir, fontName + ".font.txt");

            bool themePageExists = themePage != null && File.Exists(themePage);
            bool themeMetricsExists = themeMetrics != null && File.Exists(themeMetrics);
            bool fallbackPageExists = fallbackPage != null && File.Exists(fallbackPage);
            bool fallbackMetricsExists = fallbackMetrics != null && File.Exists(fallbackMetrics);

            // A complete theme pair wins as a unit. If either file exists without its
            // mate, retain a complete same-style fallback pair rather than mixing roots.
            if (themePageExists && themeMetricsExists)
                return new HudFontPairResolution(themePage, themeMetrics, themePage, themeMetrics,
                    fallbackPage, fallbackMetrics, HudAssetSource.Theme, false);
            if (fallbackPageExists && fallbackMetricsExists)
                return new HudFontPairResolution(fallbackPage, fallbackMetrics, themePage, themeMetrics,
                    fallbackPage, fallbackMetrics, HudAssetSource.Fallback,
                    themePageExists || themeMetricsExists);

            // No complete pair exists. Preserve whichever paths are present in the
            // diagnostic record so the loader can report the exact missing mate.
            return new HudFontPairResolution(
                themePageExists ? themePage : (fallbackPageExists ? fallbackPage : null),
                themeMetricsExists ? themeMetrics : (fallbackMetricsExists ? fallbackMetrics : null),
                themePage, themeMetrics, fallbackPage, fallbackMetrics,
                HudAssetSource.Missing, themePageExists || themeMetricsExists);
        }

        public static bool TryResolveFontPair(string assetRoot, PresentationStyle style, string fontName,
            out string pagePath, out string metricsPath, out bool usedFallback)
        {
            HudFontPairResolution pair = ResolveFontPair(assetRoot, style, fontName);
            pagePath = pair.PagePath;
            metricsPath = pair.MetricsPath;
            usedFallback = pair.IsFallback;
            return pair.IsComplete;
        }

        private static string ParentDirectory(string assetRoot)
        {
            if (string.IsNullOrEmpty(assetRoot)) return null;
            string trimmed = assetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0) return null;
            return Path.GetDirectoryName(trimmed);
        }
    }
}
