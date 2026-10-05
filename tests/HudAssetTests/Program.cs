using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ClonZones;

internal static class Program
{
    private static int _passed, _failed;

    private static void Check(bool condition, string message)
    {
        if (condition) _passed++;
        else
        {
            _failed++;
            Console.WriteLine("FAIL " + message);
        }
    }

    private static string TempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "clonzones-hud-asset-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Touch(string path, byte[] bytes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, bytes ?? new byte[] { 1 });
    }

    private static string Image(string root, PresentationStyle style, string name)
    {
        return Path.Combine(root, "hud", style == PresentationStyle.Gh3 ? "gh3" : "wormod", name + ".png");
    }

    private static string Fallback(string root, PresentationStyle style, string name)
    {
        return Path.Combine(Directory.GetParent(root).FullName, "fallback", "hud",
            style == PresentationStyle.Gh3 ? "gh3" : "wormod", name);
    }

    private static void SameNameIsolation()
    {
        string root = TempRoot();
        try
        {
            string gh3 = Image(root, PresentationStyle.Gh3, "shared");
            string wormod = Image(root, PresentationStyle.Wormod, "shared");
            Touch(gh3, new byte[] { 3 }); Touch(wormod, new byte[] { 7 });
            HudImageResolution g = HudAssetPaths.ResolveImage(root, PresentationStyle.Gh3, "shared");
            HudImageResolution w = HudAssetPaths.ResolveImage(root, PresentationStyle.Wormod, "shared");
            Check(g.Source == HudAssetSource.Theme && g.Path == gh3, "GH3 same-name image resolves in GH3 namespace");
            Check(w.Source == HudAssetSource.Theme && w.Path == wormod, "WORMod same-name image resolves in WORMod namespace");
            Check(g.Path != w.Path, "same-name images do not share a global path");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ImageFallbackPolicy()
    {
        string root = TempRoot();
        try
        {
            // A present, corrupt theme file wins selection and is never hidden by fallback.
            string theme = Image(root, PresentationStyle.Gh3, "corrupt");
            string fallback = Fallback(root, PresentationStyle.Gh3, "corrupt");
            Touch(theme, new byte[] { 0x00, 0x01 }); Touch(fallback, new byte[] { 0x89, 0x50 });
            HudImageResolution selected = HudAssetPaths.ResolveImage(root, PresentationStyle.Gh3, "corrupt");
            Check(selected.Source == HudAssetSource.Theme && selected.Path == theme, "present corrupt theme image remains selected");

            // A flat image and a different style are never candidates for WORMod.
            Touch(Path.Combine(root, "hud", "flat-only.png"));
            Touch(Image(root, PresentationStyle.Gh3, "only-gh3"));
            HudImageResolution missing = HudAssetPaths.ResolveImage(root, PresentationStyle.Wormod, "only-gh3");
            Check(missing.Source == HudAssetSource.Missing, "missing WORMod image does not cross-fallback to GH3 or flat Hud");

            string wormodFallback = Fallback(root, PresentationStyle.Wormod, "fallback-only.png");
            Touch(wormodFallback);
            HudImageResolution fallbackSelected = HudAssetPaths.ResolveImage(root, PresentationStyle.Wormod, "fallback-only");
            Check(fallbackSelected.Source == HudAssetSource.Fallback && fallbackSelected.Path == wormodFallback,
                "WORMod image uses only the same-style fallback");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void FontPairPolicy()
    {
        string root = TempRoot();
        try
        {
            string themePage = Image(root, PresentationStyle.Gh3, "num_a9");
            string themeMetrics = Path.ChangeExtension(themePage, ".font.txt");
            string fallbackPage = Fallback(root, PresentationStyle.Gh3, "num_a9.png");
            string fallbackMetrics = Fallback(root, PresentationStyle.Gh3, "num_a9.font.txt");
            Touch(themePage); Touch(fallbackPage); Touch(fallbackMetrics);
            HudFontPairResolution fallbackPair = HudAssetPaths.ResolveFontPair(root, PresentationStyle.Gh3, "num_a9");
            Check(fallbackPair.IsComplete && fallbackPair.IsFallback, "incomplete theme font falls back as a complete pair");
            Check(fallbackPair.ThemeWasIncomplete, "incomplete theme font override is observable");
            Check(fallbackPair.PagePath == fallbackPage && fallbackPair.MetricsPath == fallbackMetrics,
                "font fallback does not mix theme page with fallback metrics");

            Touch(themeMetrics);
            HudFontPairResolution themePair = HudAssetPaths.ResolveFontPair(root, PresentationStyle.Gh3, "num_a9");
            Check(themePair.Source == HudAssetSource.Theme && themePair.PagePath == themePage && themePair.MetricsPath == themeMetrics,
                "complete theme font pair wins as one unit");

            HudFontPairResolution otherStyle = HudAssetPaths.ResolveFontPair(root, PresentationStyle.Wormod, "num_a9");
            Check(!otherStyle.IsComplete, "font pair does not cross styles");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void IdentityGeneration()
    {
        HudAssetIdentity first = new(PresentationStyle.Gh3, "C:/Themes/A", 1);
        HudAssetIdentity same = new(PresentationStyle.Gh3, "c:/themes/a", 1);
        HudAssetIdentity style = new(PresentationStyle.Wormod, "C:/Themes/A", 1);
        HudAssetIdentity generation = new(PresentationStyle.Gh3, "C:/Themes/A", 2);
        Check(first == same, "asset identity compares roots without losing style/generation");
        Check(first != style && first != generation, "style and generation isolate asset identities");
    }

    private static void PresentationSettingsCases()
    {
        string root = TempRoot();
        try
        {
            string path = Path.Combine(root, "settings.ini");
            foreach ((string hud, string flame, PresentationStyle expectedHud, PresentationStyle expectedFlame) in new[]
            {
                ("gh3", "gh3", PresentationStyle.Gh3, PresentationStyle.Gh3),
                ("gh3", "wormod", PresentationStyle.Gh3, PresentationStyle.Wormod),
                ("wormod", "gh3", PresentationStyle.Wormod, PresentationStyle.Gh3),
                ("wormod", "wormod", PresentationStyle.Wormod, PresentationStyle.Wormod),
            })
            {
                File.WriteAllText(path, $"[presentation]\nhud_style = {hud}\nflame_style = {flame}\n");
                PresentationSettings settings = PresentationSettings.Read(root, _ => { });
                Check(settings.HudStyle == expectedHud && settings.FlameStyle == expectedFlame,
                    $"settings combination {hud}/{flame} is preserved");
            }

            File.WriteAllText(path, "[other]\nhud_style = wormod\n\n[presentation]\nunknown = wormod\n");
            List<string> warnings = new();
            PresentationSettings defaults = PresentationSettings.Read(root, warnings.Add);
            Check(defaults.HudStyle == PresentationStyle.Gh3 && defaults.FlameStyle == PresentationStyle.Gh3,
                "missing presentation keys retain GH3 defaults");
            Check(warnings.Count == 0, "unrelated sections and keys are ignored");

            File.WriteAllText(path, "[presentation]\nhud_style = wormod\nflame_style = invalid\n");
            warnings.Clear();
            PresentationSettings invalid = PresentationSettings.Read(root, warnings.Add);
            Check(invalid.HudStyle == PresentationStyle.Wormod && invalid.FlameStyle == PresentationStyle.Gh3 && warnings.Count == 1,
                "invalid style keeps the prior/default value and reports a warning");
        }
        finally { Directory.Delete(root, true); }
    }

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "..", ".."));

    // IHDR width/height of a PNG, or (-1,-1) when the file isn't one.
    private static (int width, int height) PngSize(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (b.Length < 24) return (-1, -1);
        for (int i = 0; i < signature.Length; i++) if (b[i] != signature[i]) return (-1, -1);
        if (b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return (-1, -1);
        return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
    }

    // CH counts stars 0..7. Every count needs its own mandatory image, and the two the
    // installer lacks ship with the mod: the exact supplied FNT rebuilds on wifi_bar0's
    // 164x164 canvas, deployed where the WORMod fallback lookup finds them.
    private static void WormodStarAssets()
    {
        string[] expectedImages = { "wifi_bar0", "WiFi_bar1", "WiFi_bar2", "WiFi_bar3", "WiFi_bar4", "WiFi_bar5", "WiFi_bar6", "WiFi_bar7" };
        Check(WormodHudLayout.StarImages.Length == 8 && WormodHudLayout.StarIds.Length == 8, "WORMod star table covers counts 0..7");
        var mandatory = new HashSet<string>(WormodHudLayout.ImageNames, StringComparer.Ordinal);
        var textures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Gh3HudLayout.Decl d in WormodHudLayout.Career) textures[d.Id] = d.Texture;
        for (int count = 0; count < expectedImages.Length; count++)
        {
            string id = $"HUD2D_score_star_{count}";
            Check(textures.TryGetValue(id, out string texture) && texture == expectedImages[count],
                $"star count {count} layer draws {expectedImages[count]}");
            Check(mandatory.Contains(expectedImages[count]), $"{expectedImages[count]} is part of the WORMod readiness set");
        }

        string shipped = Path.Combine(RepoRoot(), "assets", "fallback", "hud", "wormod");
        var supplied = new Dictionary<string, string>
        {
            ["WiFi_bar6"] = "0ce1f0b8196d88a0850ab78b889f466c9fd096c851e3ec769740291b091d046b",
            ["WiFi_bar7"] = "d7b75c6a1595ce88a3052858f2a0261b939511e2873b0bab5d236dd360432991",
        };
        string root = TempRoot();
        try
        {
            string theme = Path.Combine(root, "Theme");
            Directory.CreateDirectory(theme);
            foreach ((string name, string hash) in supplied)
            {
                string path = Path.Combine(shipped, name + ".png");
                bool exists = File.Exists(path);
                Check(exists, $"{name}.png ships under assets/fallback/hud/wormod");
                if (!exists) continue;
                Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() == hash,
                    $"{name}.png is the supplied WiFiBars_FNT_pixel_matched file");
                Check(PngSize(path) == (164, 164), $"{name}.png is a 164x164 PNG like wifi_bar0");
                // Deployed layout: <Mods>/ClonZones/<theme> beside <Mods>/ClonZones/fallback.
                string deployed = Fallback(theme, PresentationStyle.Wormod, name + ".png");
                Touch(deployed, File.ReadAllBytes(path));
                HudImageResolution resolved = HudAssetPaths.ResolveImage(theme, PresentationStyle.Wormod, name);
                Check(resolved.Source == HudAssetSource.Fallback && resolved.Path == deployed,
                    $"{name} resolves through the WORMod fallback");
            }
            string themed = Image(theme, PresentationStyle.Wormod, "WiFi_bar7");
            Touch(themed, new byte[] { 7 });
            Check(HudAssetPaths.ResolveImage(theme, PresentationStyle.Wormod, "WiFi_bar7").Path == themed,
                "a theme's own WiFi_bar7 still overrides the shipped one");
        }
        finally { Directory.Delete(root, true); }
    }

    private static int Main()
    {
        SameNameIsolation();
        ImageFallbackPolicy();
        FontPairPolicy();
        IdentityGeneration();
        PresentationSettingsCases();
        WormodStarAssets();
        Console.WriteLine($"{_passed} HUD asset/settings checks passed; {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
