using System;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Loads GH3-style hit-flame sprite sheets once and keeps them alive for the
    /// process lifetime.
    ///
    /// Source art is NOT resized: note_hit.png / note_hit_blue.png are 512x1024 =
    /// a 4x4 grid of 16 frames at 128x256, animating ignite -> peak -> fade
    /// (row-major from the top-left, same as the note sheets). On-screen size is
    /// matched to vanilla via <see cref="FlamePpu"/> only.
    ///
    /// The sheets have a black background. It is removed at render time with an
    /// additive material (black contributes nothing under additive blend). If no
    /// additive shader exists in the build, the textures are luminance-alpha-keyed
    /// at load instead (alpha = max(r,g,b)) so black is transparent under normal
    /// alpha blending.
    /// </summary>
    internal static class FlameSpriteBank
    {
        private const int FrameW = 128;
        private const int FrameH = 256;

        // ── Tunable: source frames are 128x256; vanilla v1.1 hit flames are 96x96.
        // Match the highway size here, NOT by resizing the art. Tune from screenshots.
        private const float FlamePpu = 600f;
        // Pivot at the base center so the flame rises from the fret/strike line.
        private static readonly Vector2 Pivot = new Vector2(0.5f, 0.04f);

        private static Sprite[] _normal;
        private static Sprite[] _starpower;
        private static Material _material; // shared additive material, or null = default

        public static bool IsReady { get; private set; }
        public static Material FlameMaterial => _material;
        public static int FrameCount => _normal != null ? _normal.Length : 0;

        public static void LoadAll(string assetRoot, MelonLogger.Instance log)
        {
            try
            {
                // Use the same proven render path as notes/frets: default sprite
                // material + black removed by luminance-alpha key at load. (Additive
                // is the nicer look but a custom material on a SpriteRenderer has
                // texture-binding/sorting pitfalls; revisit once flames are visible.)
                bool additive = false;
                _normal = LoadSheet(assetRoot, "FX/note_hit.png", "flame_normal", !additive, log);
                _starpower = LoadSheet(assetRoot, "FX/note_hit_blue.png", "flame_starpower", !additive, log);

                IsReady = _normal != null && _normal.Length > 0
                          && _starpower != null && _starpower.Length > 0;
                if (IsReady)
                    log.Msg($"[ClonZones] FlameSpriteBank ready: {_normal.Length} frame(s) each, blend={(additive ? "additive" : "alpha-key")}.");
                else
                    log.Warning("[ClonZones] FlameSpriteBank: flame sheets missing/empty; flames disabled.");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] FlameSpriteBank.LoadAll fatal: {ex}");
            }
        }

        public static Sprite Get(bool starpower, int frame)
        {
            var frames = starpower ? _starpower : _normal;
            if (frames == null || frames.Length == 0)
                return null;
            return frames[Math.Abs(frame) % frames.Length];
        }

        public static Sprite[] GetFrames(bool starpower)
        {
            return starpower ? _starpower : _normal;
        }

        private static bool TryCreateAdditiveMaterial()
        {
            var shader = Shader.Find("Legacy Shaders/Particles/Additive")
                         ?? Shader.Find("Particles/Additive")
                         ?? Shader.Find("Mobile/Particles/Additive");
            if (shader == null)
                return false;

            _material = new Material(shader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            return true;
        }

        private static Sprite[] LoadSheet(string assetRoot, string relPath, string name, bool keyAlpha, MelonLogger.Instance log)
        {
            var full = Path.Combine(assetRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                log.Warning($"[ClonZones] Flame sheet missing: {relPath}");
                return null;
            }

            var bytes = File.ReadAllBytes(full);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes))
                throw new InvalidDataException($"Unity could not decode {relPath}.");

            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = $"clonzones_{name}_tex";
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

            if (keyAlpha)
                KeyBlackToAlpha(tex);

            int cols = Math.Max(1, tex.width / FrameW);
            int rows = Math.Max(1, tex.height / FrameH);
            var frames = new Sprite[cols * rows];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    // Sheet is row-major from the top-left; Unity rects are bottom-left.
                    int y = tex.height - (r + 1) * FrameH;
                    var s = Sprite.Create(tex, new Rect(c * FrameW, y, FrameW, FrameH), Pivot, FlamePpu, 0, SpriteMeshType.FullRect);
                    s.name = $"clonzones_{name}_{idx}";
                    s.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    frames[idx] = s;
                }
            }

            log.Msg($"[ClonZones] Loaded {relPath} → {name} ({frames.Length} frame(s), {cols}x{rows} of {FrameW}x{FrameH})");
            return frames;
        }

        // Fallback only (no additive shader available): make black transparent so the
        // flame composites under standard alpha blending. alpha = max(r,g,b).
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
