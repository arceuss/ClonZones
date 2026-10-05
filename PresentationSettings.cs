using System;
using System.IO;

namespace ClonZones
{
    internal enum PresentationStyle { Gh3, Wormod }

    internal readonly struct PresentationSettings
    {
        public readonly PresentationStyle HudStyle;
        public readonly PresentationStyle FlameStyle;

        public PresentationSettings(PresentationStyle hudStyle, PresentationStyle flameStyle)
        {
            HudStyle = hudStyle;
            FlameStyle = flameStyle;
        }

        // snapshot at gameplay entry. changing a file must not replace an active scene's assets.
        public static PresentationSettings Read(string assetRoot, Action<string> warning)
        {
            PresentationStyle hud = PresentationStyle.Gh3, flame = PresentationStyle.Gh3;
            if (string.IsNullOrEmpty(assetRoot)) return new PresentationSettings(hud, flame);
            string path = Path.Combine(assetRoot, "settings.ini");
            if (!File.Exists(path)) return new PresentationSettings(hud, flame);
            try
            {
                bool inPresentation = false;
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inPresentation = line.Equals("[presentation]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inPresentation) continue;
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = line.Substring(0, equals).Trim();
                    bool isHud = key.Equals("hud_style", StringComparison.OrdinalIgnoreCase);
                    bool isFlame = key.Equals("flame_style", StringComparison.OrdinalIgnoreCase);
                    if (!isHud && !isFlame) continue;
                    string value = line.Substring(equals + 1).Trim();
                    PresentationStyle style;
                    if (value.Equals("gh3", StringComparison.OrdinalIgnoreCase)) style = PresentationStyle.Gh3;
                    else if (value.Equals("wormod", StringComparison.OrdinalIgnoreCase)) style = PresentationStyle.Wormod;
                    else
                    {
                        warning?.Invoke($"{path}: invalid {key} '{value}'; expected gh3 or wormod, keeping the previous value.");
                        continue;
                    }
                    if (isHud) hud = style;
                    else flame = style;
                }
            }
            catch (IOException error)
            {
                warning?.Invoke($"couldn't read {path}: {error.Message}; keeping GH3 presentation.");
                return new PresentationSettings(PresentationStyle.Gh3, PresentationStyle.Gh3);
            }
            catch (UnauthorizedAccessException error)
            {
                warning?.Invoke($"couldn't read {path}: {error.Message}; keeping GH3 presentation.");
                return new PresentationSettings(PresentationStyle.Gh3, PresentationStyle.Gh3);
            }
            return new PresentationSettings(hud, flame);
        }
    }
}
