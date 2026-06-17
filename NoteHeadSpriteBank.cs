using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Loads GH3-style note-head sprites once, keeps them alive for the process
    /// lifetime, and tracks which sprite pointers belong to this mod.
    ///
    /// Does NOT store vanilla sprite pointers.
    /// Does NOT destroy or reload per song or scene.
    /// </summary>
    internal static class NoteHeadSpriteBank
    {
        private static readonly Dictionary<string, Sprite[]> _sprites = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsReady { get; private set; }

        private static readonly string[] Lanes = { "green", "red", "yellow", "blue", "orange" };

        private const float NormalPpu = 720f;
        private const float PhrasePpu = 568.9f;

        /// <summary>
        /// Loads all note-head sprites from assetRoot. Safe to call once during mod init.
        /// </summary>
        public static void LoadAll(string assetRoot, MelonLogger.Instance log)
        {
            try
            {
                // Per-lane closed notes use 128x64 frames. Static 128x64 PNGs
                // stay single-frame; larger valid grids animate automatically.
                foreach (var lane in Lanes)
                {
                    TryLoadAutoFrameSheet(assetRoot, $"Notes/{lane}/normal.png",   $"{lane}/normal",   NormalPpu, log);
                    TryLoadAutoFrameSheet(assetRoot, $"Notes/{lane}/hopo.png",     $"{lane}/hopo",     NormalPpu, log);
                    TryLoadAutoFrameSheet(assetRoot, $"Notes/{lane}/tap.png",      $"{lane}/tap",      NormalPpu, log);
                    TryLoadGridSheet(  assetRoot, $"Notes/{lane}/star_normal.png",   $"{lane}/star_normal",   PhrasePpu, log, 4, 4, 128);
                    TryLoadGridSheet(  assetRoot, $"Notes/{lane}/star_hopo.png",     $"{lane}/star_hopo",     PhrasePpu, log, 4, 4, 128);
                    TryLoadTapStarPowerSheet(assetRoot, $"Notes/{lane}/tap_starpower.png", $"{lane}/tap_starpower", PhrasePpu, log);
                }

                // Open notes are wider than closed fret gems. Treat the whole open
                // sweep as 512x64 animation frames so static normal/HOPO opens use
                // the same scale as SP opens.
                TryLoadFrameSheet(assetRoot, "Notes/open/normal.png",      "open/normal",      PhrasePpu, log, 512, 64);
                TryLoadFrameSheet(assetRoot, "Notes/open/hopo.png",        "open/hopo",        PhrasePpu, log, 512, 64);
                TryLoadFrameSheet(assetRoot, "Notes/open/star_normal.png", "open/star_normal", PhrasePpu, log, 512, 64);
                TryLoadFrameSheet(assetRoot, "Notes/open/star_hopo.png",   "open/star_hopo",   PhrasePpu, log, 512, 64);
                // Overlapping active SP over a non-phrase open note uses the active-SP
                // open textures. Overlapping SP phrases use the phrase_open assets.
                TryLoadFrameSheet(assetRoot, "Notes/generic/star_power_open.png",      "active_open_normal", PhrasePpu, log, 512, 64);
                TryLoadFrameSheet(assetRoot, "Notes/generic/star_power_open_hopo.png", "active_open_hopo",   PhrasePpu, log, 512, 64);
                TryLoadFrameSheet(assetRoot, "Notes/generic/phrase_open.png",          "active_phrase_open_normal", PhrasePpu, log, 512, 64);
                TryLoadFrameSheet(assetRoot, "Notes/generic/phrase_open_hopo.png",     "active_phrase_open_hopo",   PhrasePpu, log, 512, 64);

                // Generic active-SP variants (not lane-specific). These are also
                // closed-note 128x64 assets, so allow optional animation sheets.
                TryLoadAutoFrameSheet(assetRoot, "Notes/generic/star_power.png",      "active_star_normal",        NormalPpu, log);
                TryLoadAutoFrameSheet(assetRoot, "Notes/generic/star_power_hopo.png", "active_star_hopo",          NormalPpu, log);
                TryLoadAutoFrameSheet(assetRoot, "Notes/generic/star_power_tap.png",  "active_tap_starpower",      NormalPpu, log);
                TryLoadGridSheet(  assetRoot, "Notes/generic/phrase_strum.png",    "active_phrase_star_normal", PhrasePpu, log, 4, 4, 128);
                TryLoadGridSheet(  assetRoot, "Notes/generic/phrase_hopo.png",     "active_phrase_star_hopo",   PhrasePpu, log, 4, 4, 128);
                TryLoadTapStarPowerSheet(assetRoot, "Notes/generic/phrase_tap.png",      "active_phrase_tap_starpower", PhrasePpu, log);

                IsReady = true;
                log.Msg($"[ClonZones] NoteHeadSpriteBank ready: {_sprites.Count} key(s).");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] NoteHeadSpriteBank.LoadAll fatal: {ex}");
            }
        }


        /// <summary>
        /// Returns the sprite for the given key and animation frame (0-indexed).
        /// Returns null if key is missing — callers must fall back to vanilla.
        /// </summary>
        public static Sprite Get(string key, int frame = 0)
        {
            if (!_sprites.TryGetValue(key, out var frames) || frames == null || frames.Length == 0)
                return null;
            return frames[Math.Abs(frame) % frames.Length];
        }

        /// <summary>
        /// Resolves the frame array for a key in a single lookup. Returns false if
        /// the key is missing or empty — callers must fall back to vanilla.
        /// </summary>
        public static bool TryGetFrames(string key, out Sprite[] frames)
        {
            return _sprites.TryGetValue(key, out frames) && frames != null && frames.Length > 0;
        }

        public static int GetFrameCount(string key)
        {
            return _sprites.TryGetValue(key, out var frames) && frames != null
                ? frames.Length
                : 0;
        }

        // ─── Private loaders ───────────────────────────────────────────────────


        private static void TryLoadAutoFrameSheet(
            string assetRoot, string relPath, string key, float ppu, MelonLogger.Instance log, int frameW = 128, int frameH = 64)
        {
            var full = AbsPath(assetRoot, relPath);
            if (!File.Exists(full)) return;

            try
            {
                var tex = LoadTexture(full, key);
                bool validSheet = (tex.width > frameW || tex.height > frameH)
                                  && tex.width % frameW == 0
                                  && tex.height % frameH == 0;

                if (validSheet)
                {
                    var frames = MakeFrameSheet(tex, key, ppu, frameW, frameH, out int cols, out int rows);
                    Register(key, frames);
                    log.Msg($"[ClonZones] Loaded {relPath} → {key} ({frames.Length} frame(s), {cols}x{rows} grid of {frameW}x{frameH}, auto)");
                    return;
                }

                var sprite = MakeSprite(tex, new Rect(0, 0, tex.width, tex.height), ppu, key);
                Register(key, new[] { sprite });
                log.Msg($"[ClonZones] Loaded {relPath} → {key}");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] Failed {relPath}: {ex.Message}");
            }
        }

        private static void TryLoadGridSheet(
            string assetRoot, string relPath, string key, float ppu, MelonLogger.Instance log, int gridCols, int gridRows, int baseFrameW)
        {
            var full = AbsPath(assetRoot, relPath);
            if (!File.Exists(full)) return;

            try
            {
                var tex = LoadTexture(full, key);
                int baseFrameH = Math.Max(1, baseFrameW / 2);
                if (tex.width == baseFrameW && tex.height == baseFrameH)
                {
                    var sprite = MakeSprite(tex, new Rect(0, 0, tex.width, tex.height), ppu, key);
                    Register(key, new[] { sprite });
                    log.Msg($"[ClonZones] Loaded {relPath} → {key}");
                    return;
                }

                if (gridCols <= 0 || gridRows <= 0 || tex.width % gridCols != 0 || tex.height % gridRows != 0)
                    throw new InvalidDataException($"{tex.width}x{tex.height} is not divisible by {gridCols}x{gridRows}.");

                int frameW = tex.width / gridCols;
                int frameH = tex.height / gridRows;
                float scaledPpu = ppu * Math.Max(1f, frameW / (float)baseFrameW);
                var frames = MakeFrameSheet(tex, key, scaledPpu, frameW, frameH, out int cols, out int rows);
                Register(key, frames);
                log.Msg($"[ClonZones] Loaded {relPath} → {key} ({frames.Length} frame(s), {cols}x{rows} grid of {frameW}x{frameH}, ppu={scaledPpu:F1})");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] Failed {relPath}: {ex.Message}");
            }
        }

        private static void TryLoadTapStarPowerSheet(
            string assetRoot, string relPath, string key, float ppu, MelonLogger.Instance log)
        {
            var full = AbsPath(assetRoot, relPath);
            if (!File.Exists(full)) return;

            try
            {
                var tex = LoadTexture(full, key);
                if (tex.width == 128 && tex.height == 64)
                {
                    var sprite = MakeSprite(tex, new Rect(0, 0, tex.width, tex.height), ppu, key);
                    Register(key, new[] { sprite });
                    log.Msg($"[ClonZones] Loaded {relPath} → {key}");
                    return;
                }

                int frameW;
                int frameH;
                if (tex.width >= 512 && tex.width % 4 == 0 && tex.height % 4 == 0)
                {
                    // FastGH3/GH2Theme star-tap sheets are authored as 4x4 grids,
                    // e.g. 1024x512 → 256x128 frames.
                    frameW = tex.width / 4;
                    frameH = tex.height / 4;
                }
                else
                {
                    // GH3+/GH3 battle tap SP sheets are 256x256, but their frames
                    // are still 128x64 in a 2x4 grid. Do not force these into 4x4.
                    frameW = 128;
                    frameH = 64;
                }

                if (tex.width % frameW != 0 || tex.height % frameH != 0)
                    throw new InvalidDataException($"{tex.width}x{tex.height} is not divisible by {frameW}x{frameH} frames.");

                float scaledPpu = ppu * Math.Max(1f, frameW / 128f);
                var frames = MakeFrameSheet(tex, key, scaledPpu, frameW, frameH, out int cols, out int rows);
                Register(key, frames);
                log.Msg($"[ClonZones] Loaded {relPath} → {key} ({frames.Length} frame(s), {cols}x{rows} grid of {frameW}x{frameH}, ppu={scaledPpu:F1})");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] Failed {relPath}: {ex.Message}");
            }
        }

        private static void TryLoadFrameSheet(
            string assetRoot, string relPath, string key, float ppu, MelonLogger.Instance log, int frameW = 128, int frameH = 64)
        {
            var full = AbsPath(assetRoot, relPath);
            if (!File.Exists(full)) return;

            try
            {
                var tex = LoadTexture(full, key);
                var frames = MakeFrameSheet(tex, key, ppu, frameW, frameH, out int cols, out int rows);
                Register(key, frames);
                log.Msg($"[ClonZones] Loaded {relPath} → {key} ({frames.Length} frame(s), {cols}x{rows} grid of {frameW}x{frameH})");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] Failed {relPath}: {ex.Message}");
            }
        }

        private static Sprite[] MakeFrameSheet(Texture2D tex, string key, float ppu, int frameW, int frameH, out int cols, out int rows)
        {
            // Frame grid, row-major from the TOP-LEFT (standard sheet order).
            // Unity sprite rects use a bottom-left origin, so flip the row.
            cols = Math.Max(1, tex.width / frameW);
            rows = Math.Max(1, tex.height / frameH);
            int count = cols * rows;
            var frames = new Sprite[count];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    int y = tex.height - (r + 1) * frameH;
                    frames[idx] = MakeSprite(tex, new Rect(c * frameW, y, frameW, frameH), ppu, $"{key}_{idx}");
                }
            }
            return frames;
        }

        private static void Register(string key, Sprite[] frames)
        {
            _sprites[key] = frames;
        }

        private static string AbsPath(string root, string rel) =>
            Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

        private static Texture2D LoadTexture(string path, string name)
        {
            var bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes))
                throw new InvalidDataException("Unity could not decode image data.");
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = $"gh3zones_{name}_tex";
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return tex;
        }

        private static Sprite MakeSprite(Texture2D tex, Rect rect, float ppu, string name)
        {
            var s = Sprite.Create(tex, rect, new Vector2(0.5f, 0.18f), ppu, 0, SpriteMeshType.FullRect);
            s.name = $"gh3zones_{name}";
            s.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return s;
        }
    }
}
