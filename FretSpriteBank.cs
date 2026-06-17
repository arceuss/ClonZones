using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Loads GH3-style fret sprites once and keeps them alive for the process lifetime.
    /// </summary>
    internal static class FretSpriteBank
    {
        private static readonly Dictionary<string, Sprite> _sprites = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Sprite[]> _frames = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] Lanes = { "green", "red", "yellow", "blue", "orange" };
        private static readonly string[] Kinds = { "mid", "lip", "head", "head_lit", "down" };

        // v1.1 yellow head reference (`head_3`) has a 157x68 content bbox.
        // `spr_newtargets_head_strip6_2` has a 106x46 content bbox, so the match
        // is ~148.1% scale. Runtime screenshots showed that was ~2 px too small,
        // so use 650 PPU for a slight size bump without resizing source assets.
        // Apply the measured vertical correction via sprite pivot, not renderer transforms,
        // so gameplay objects/flames/sparks keep their vanilla positions.
        private const float FretPpu = 650f;
        private const float FretPivotYPixels = 25f;

        public static bool IsReady { get; private set; }
        public static bool HasAnimatedFrames { get; private set; }
        public static int MaxFrameCount { get; private set; } = 1;

        public static void LoadAll(string assetRoot, MelonLogger.Instance log)
        {
            try
            {
                foreach (var lane in Lanes)
                {
                    foreach (var kind in Kinds)
                    {
                        TryLoad(assetRoot, $"Frets/{lane}/{kind}.png", $"{lane}/{kind}", log);
                    }
                }

                int expected = Lanes.Length * Kinds.Length;
                IsReady = _sprites.Count == expected;
                HasAnimatedFrames = _frames.Count > 0;
                MaxFrameCount = 1;
                foreach (var frames in _frames.Values)
                {
                    if (frames != null && frames.Length > MaxFrameCount)
                        MaxFrameCount = frames.Length;
                }
                if (!IsReady)
                    log.Warning($"[ClonZones] FretSpriteBank loaded {_sprites.Count}/{expected} required sprites; fret replacement disabled to avoid partial hidden layers.");
                else
                    log.Msg($"[ClonZones] FretSpriteBank ready: {_sprites.Count} key(s), animated={HasAnimatedFrames}, maxFrames={MaxFrameCount}.");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] FretSpriteBank.LoadAll fatal: {ex}");
            }
        }

        public static Sprite Get(string lane, string kind)
        {
            if (_sprites.TryGetValue($"{lane}/{kind}", out var sprite))
                return sprite;

            // Compatibility with the v1.1 semantic name while current asset files are
            // still named head_lit.png.
            if (string.Equals(kind, "head_light", StringComparison.OrdinalIgnoreCase))
                return _sprites.TryGetValue($"{lane}/head_lit", out sprite) ? sprite : null;

            return null;
        }

        public static Sprite[] GetFrames(string lane, string kind)
        {
            if (_frames.TryGetValue($"{lane}/{kind}", out var frames))
                return frames;

            if (string.Equals(kind, "head_light", StringComparison.OrdinalIgnoreCase))
                return _frames.TryGetValue($"{lane}/head_lit", out frames) ? frames : null;

            return null;
        }


        private static void TryLoad(string assetRoot, string relPath, string key, MelonLogger.Instance log)
        {
            var full = Path.Combine(assetRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return;

            try
            {
                var tex = LoadTexture(full, key);
                var frames = MakeSprites(tex, key);
                _sprites[key] = frames[0];
                if (frames.Length > 1)
                    _frames[key] = frames;
                log.Msg($"[ClonZones] Loaded {relPath} → fret/{key}{(frames.Length > 1 ? $" ({frames.Length} frame sheet)" : string.Empty)}");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] Failed {relPath}: {ex.Message}");
            }
        }

        private static Texture2D LoadTexture(string path, string name)
        {
            var bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes))
                throw new InvalidDataException("Unity could not decode image data.");
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = $"clonzones_fret_{name}_tex";
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return tex;
        }

        private static Sprite[] MakeSprites(Texture2D tex, string name)
        {
            // FastGH3-style animated frets are 512x256 sheets laid out as 4x4
            // frames. Static fret PNGs remain a single full-image sprite.
            if (tex.width == 512 && tex.height == 256)
            {
                const int cols = 4;
                const int rows = 4;
                int frameWidth = tex.width / cols;
                int frameHeight = tex.height / rows;
                var frames = new Sprite[cols * rows];
                int index = 0;
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < cols; x++)
                    {
                        var rect = new Rect(x * frameWidth, tex.height - ((y + 1) * frameHeight), frameWidth, frameHeight);
                        frames[index] = MakeSprite(tex, name, rect, frameHeight, index);
                        index++;
                    }
                }
                return frames;
            }

            return new[] { MakeSprite(tex, name, new Rect(0, 0, tex.width, tex.height), tex.height, -1) };
        }

        private static Sprite MakeSprite(Texture2D tex, string name, Rect rect, int frameHeight, int frameIndex)
        {
            var s = Sprite.Create(
                tex,
                rect,
                new Vector2(0.5f, FretPivotYPixels / frameHeight),
                FretPpu,
                0,
                SpriteMeshType.FullRect);
            s.name = frameIndex >= 0 ? $"clonzones_fret_{name}_{frameIndex:00}" : $"clonzones_fret_{name}";
            s.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return s;
        }
    }
}
