using System;
using System.IO;

namespace ClonZones
{
    internal static class FlameAssetPaths
    {
        // Shared FX remains an intentional theme override for the current zone
        // converter. A style-specific override wins; fallback never crosses styles.
        public static string Resolve(string theme, string fallback, PresentationStyle style, string filename)
        {
            string name = style == PresentationStyle.Wormod ? "wormod" : "gh3";
            string scoped = Path.Combine(theme, "FX", name, filename);
            if (File.Exists(scoped)) return scoped;
            string shared = Path.Combine(theme, "FX", filename);
            if (File.Exists(shared)) return shared;
            string bundled = Path.Combine(fallback, "fx", name, filename);
            if (File.Exists(bundled)) return bundled;
            throw new FileNotFoundException($"Flame asset missing: FX/{name}/{filename}, FX/{filename}, or fallback/fx/{name}/{filename}.");
        }
    }

}
