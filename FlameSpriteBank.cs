using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    internal sealed class FlameFxAssets
    {
        internal readonly struct Image
        {
            public readonly int X, Y, Width, Height;
            public Image(int x, int y, int width, int height) { X=x; Y=y; Width=width; Height=height; }
        }
        public Texture2D Atlas;
        public Shader Shader;
        public Image Normal, Star, Spark;
        public string SourceDescription;
        public bool IsAlive => Atlas != null && Shader != null;
    }

    // Name retained for Core's existing entry point. This bank now owns untouched
    // sheets in one atlas, not alpha-keyed Sprite objects or CH animator clones.
    internal static class FlameSpriteBank
    {
        private const int Gutter = 2;
        private static string _root;
        private static MelonLogger.Instance _log;
        private static readonly Dictionary<string, FlameFxAssets> Banks = new(StringComparer.OrdinalIgnoreCase);
        private sealed class Decoded
        {
            public Color32[] Pixels;
            public int Width, Height;
            public string Path;
        }
        public static string AssetRoot => _root;
        public static void LoadAll(string assetRoot, MelonLogger.Instance log)
        {
            // Defer loading until a real camera/player exists and the requested
            // style is known. No speculative GH3 bank decides WORMod readiness.
            _root = assetRoot; _log = log;
        }
        public static FlameFxAssets Get(PresentationStyle style)
        {
            string key = (_root ?? "") + "|" + style;
            if (Banks.TryGetValue(key, out FlameFxAssets ready) && ready.IsAlive) return ready;
            if (string.IsNullOrEmpty(_root)) throw new InvalidOperationException("Flame asset root not configured.");
            Shader shader = Shader.Find("Legacy Shaders/Particles/Additive");
            if (shader == null)
                throw new InvalidOperationException("Legacy Shaders/Particles/Additive unavailable; no alpha-key fallback is visually equivalent.");
            string mods = Path.GetDirectoryName(typeof(Core).Assembly.Location);
            string fallback = Path.Combine(mods ?? "", "ClonZones", "fallback");
            Decoded[] images = {
                Load(FlameAssetPaths.Resolve(_root, fallback, style, "note_hit.png"), true),
                Load(FlameAssetPaths.Resolve(_root, fallback, style, "note_hit_blue.png"), true),
                Load(FlameAssetPaths.Resolve(_root, fallback, style, "particle_spark.png"), false)
            };
            int limit = SystemInfo.maxTextureSize;
            int width = 1, minWidth = 0;
            foreach (Decoded image in images) minWidth = Math.Max(minWidth, checked(image.Width + Gutter*2));
            while (width < minWidth) width = checked(width * 2);
            if (width > limit) throw new InvalidDataException("Flame sheet exceeds the device's atlas limit; refusing to resize it.");
            FlameFxAssets.Image[] regions;
            int height;
            while (true)
            {
                regions = new FlameFxAssets.Image[images.Length];
                int x = 0, y = 0, rowHeight = 0;
                for (int i = 0; i < images.Length; ++i)
                {
                    Decoded im = images[i]; int cw = im.Width + Gutter*2, ch = im.Height + Gutter*2;
                    if (x + cw > width) { y += rowHeight; x = rowHeight = 0; }
                    regions[i] = new FlameFxAssets.Image(x + Gutter, y + Gutter, im.Width, im.Height);
                    x += cw; rowHeight = Math.Max(rowHeight, ch);
                }
                height = 1;
                while (height < y + rowHeight) height = checked(height * 2);
                if (height <= limit) break;
                if (width > limit / 2) throw new InvalidDataException("Flame atlas cannot fit without resizing; native flames retained.");
                width *= 2;
            }
            Color32[] pixels = new Color32[checked(width * height)];
            for (int i = 0; i < images.Length; ++i)
            {
                Decoded im = images[i]; FlameFxAssets.Image r = regions[i];
                // Whole-sheet gutters only. Do not isolate cells: sampling at a
                // frame edge must still see the original neighbouring texels.
                for (int y = -Gutter; y < im.Height + Gutter; ++y)
                    for (int x = -Gutter; x < im.Width + Gutter; ++x)
                        pixels[(r.Y+y)*width+r.X+x] = im.Pixels[Math.Clamp(y,0,im.Height-1)*im.Width+Math.Clamp(x,0,im.Width-1)];
            }
            Texture2D atlas = null;
            try
            {
                atlas = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
                {
                    name = "clonzones_flames_" + style, filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset
                };
                atlas.SetPixels32(pixels); atlas.Apply(false, true);
                ready = new FlameFxAssets
                {
                    Atlas = atlas, Shader = shader, Normal = regions[0], Star = regions[1], Spark = regions[2],
                    SourceDescription = images[0].Path + " | " + images[1].Path + " | " + images[2].Path
                };
                Banks[key] = ready;
                _log?.Msg($"[ClonZones] {style} NoteFX assets: {width}x{height} additive atlas; {ready.SourceDescription}");
                return ready;
            }
            catch { if (atlas != null) UnityEngine.Object.Destroy(atlas); throw; }
        }
        private static Decoded Load(string path, bool animated)
        {
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path))) throw new InvalidDataException("Cannot decode " + path);
                int w = texture.width, h = texture.height;
                if (w <= 0 || h <= 0) throw new InvalidDataException("Empty flame texture: " + path);
                Color32[] pixels = texture.GetPixels32();
                if (animated && (w % 4 != 0 || h % 4 != 0))
                {
                    bool contributes = false;
                    foreach (Color32 p in pixels)
                        if (p.a != 0 && (p.r != 0 || p.g != 0 || p.b != 0)) { contributes = true; break; }
                    if (contributes) throw new InvalidDataException("Expected a complete 4x4 flame sheet: " + path);
                    // An intentionally black/transparent 1x1 contributes nothing
                    // under additive blending. Keep it instead of showing fallback.
                }
                return new Decoded { Width = w, Height = h, Pixels = pixels, Path = path };
            }
            finally { if (texture != null) UnityEngine.Object.Destroy(texture); }
        }
    }
}
