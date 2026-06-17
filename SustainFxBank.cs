using System;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Loads GH3 sustain_hold as the replacement for Clone Hero's sustain_spark_anim.
    /// </summary>
    internal static class SustainFxBank
    {
        private const int FrameW = 128;
        private const int FrameH = 128;
        public const int TileX = 4;
        public const int TileY = 4;

        private const float FallbackSparkPpu = 400f;
        private const float DefaultPivotYOffsetPixels = -15f;
        private static float _pivotYOffsetPixels = DefaultPivotYOffsetPixels;
        private static Vector2 FallbackPivot => new Vector2(0.5f, 0.5f + (_pivotYOffsetPixels / FrameH));

        private static Texture2D _texture;
        private static Sprite[] _frames;

        public static bool IsReady => _texture != null && _frames != null && _frames.Length > 0;
        public static Texture2D Texture => _texture;
        public static Sprite[] Frames => _frames;

        public static void LoadAll(string assetRoot, MelonLogger.Instance log)
        {
            try
            {
                _pivotYOffsetPixels = LoadPivotYOffsetPixels(assetRoot, log);
                var full = Path.Combine(assetRoot, "FX", "sustain_hold.png");
                if (!File.Exists(full))
                {
                    log.Msg("[ClonZones] SustainFxBank: FX/sustain_hold.png missing; sustain sparks unchanged.");
                    return;
                }

                var bytes = File.ReadAllBytes(full);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes))
                    throw new InvalidDataException("Unity could not decode FX/sustain_hold.png.");

                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = "clonzones_sustain_hold_tex";
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                KeyBlackToAlpha(tex);

                _texture = tex;
                _frames = CreateFrames(tex);
                log.Msg($"[ClonZones] SustainFxBank ready: {tex.width}x{tex.height} ({_frames.Length} sustain_spark frame(s)).");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] SustainFxBank.LoadAll fatal: {ex}");
            }
        }

        public static Sprite[] CreateFramesLike(Sprite template)
        {
            if (_texture == null)
                return null;

            float ppu = FallbackSparkPpu;
            Vector2 pivot = FallbackPivot;

            if (template != null)
            {
                var rect = template.rect;
                if (template.pixelsPerUnit > 0f)
                    ppu = template.pixelsPerUnit;
                if (rect.width > 0f && rect.height > 0f)
                {
                    pivot = new Vector2(
                        template.pivot.x / rect.width,
                        (template.pivot.y / rect.height) + (_pivotYOffsetPixels / rect.height));
                }
            }

            return CreateFrames(_texture, ppu, pivot);
        }

        private static Sprite[] CreateFrames(Texture2D tex)
        {
            return CreateFrames(tex, FallbackSparkPpu, FallbackPivot);
        }

        private static Sprite[] CreateFrames(Texture2D tex, float ppu, Vector2 pivot)
        {
            int cols = Math.Max(1, tex.width / FrameW);
            int rows = Math.Max(1, tex.height / FrameH);
            var frames = new Sprite[cols * rows];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    int y = tex.height - (r + 1) * FrameH;
                    var s = Sprite.Create(tex, new Rect(c * FrameW, y, FrameW, FrameH), pivot, ppu, 0, SpriteMeshType.FullRect);
                    s.name = $"clonzones_sustain_hold_{idx}";
                    s.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    frames[idx] = s;
                }
            }
            return frames;
        }

        private static float LoadPivotYOffsetPixels(string assetRoot, MelonLogger.Instance log)
        {
            string path = Path.Combine(assetRoot, "settings.ini");
            if (!File.Exists(path))
                return DefaultPivotYOffsetPixels;

            try
            {
                bool inSustainFx = false;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                        continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inSustainFx = string.Equals(line.Substring(1, line.Length - 2).Trim(), "sustain_fx", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inSustainFx)
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    var key = line.Substring(0, eq).Trim();
                    if (!string.Equals(key, "pivot_y_offset_pixels", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var valueText = line.Substring(eq + 1).Split(';')[0].Split('#')[0].Trim();
                    if (float.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
                    {
                        log.Msg($"[ClonZones] Sustain FX pivot_y_offset_pixels={value} from settings.ini");
                        return value;
                    }

                    log.Warning($"[ClonZones] Invalid sustain_fx.pivot_y_offset_pixels in settings.ini: {valueText}");
                    return DefaultPivotYOffsetPixels;
                }
            }
            catch (Exception ex)
            {
                log.Warning($"[ClonZones] Could not read settings.ini: {ex.Message}");
            }

            return DefaultPivotYOffsetPixels;
        }

        private static void KeyBlackToAlpha(Texture2D tex)
        {
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                var p = px[i];
                byte a = p.r > p.g ? p.r : p.g;
                if (p.b > a) a = p.b;
                px[i] = new Color32(p.r, p.g, p.b, a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
        }
    }
}
