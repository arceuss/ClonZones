using System;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    // GH3 sustain bodies plus a runtime-built glow sheet per body. Ten files under
    // the selected asset root, index 0..4 green/red/yellow/blue/orange sustain.png,
    // 5 generic sustain_star_power, 6 generic sustain_dead, 7 open sustain,
    // 8 generic sustain_open_star_power, 9 open sustain_dead.
    // Lane/SP/dead bodies are 32x32 from sys_Whammy2D_*, open bodies are 128x32
    // GH3+ art (gh3.SOURCE.txt). Body alpha uploads untouched, no black key.
    // Glow bakes the NotClon whammy glow pass (renderer.cpp GH3_WHAMMY_FS with
    // uEdge 0.85/-0.3): flat lane color sampled at GH3 (0.9, 0.9), per pixel
    // prof = 1-abs(2u-1), g = clamp(prof/0.7), edge = g*g*(3-2g),
    // alpha = edge*v*bodyAlpha*0.85 with v = 1 at the PNG bottom.
    // Vertex alpha, whammy stretch and fade stay with CH at runtime.
    internal static class Gh3SustainBank
    {
        public const int Count = 10;
        public const int Sp = 5;
        public const int Dead = 6;
        public const int Open = 7;
        public const int OpenSp = 8;
        public const int OpenDead = 9;

        private static readonly string[] RelPaths =
        {
            "Notes/green/sustain.png",
            "Notes/red/sustain.png",
            "Notes/yellow/sustain.png",
            "Notes/blue/sustain.png",
            "Notes/orange/sustain.png",
            "Notes/generic/sustain_star_power.png",
            "Notes/generic/sustain_dead.png",
            "Notes/open/sustain.png",
            "Notes/generic/sustain_open_star_power.png",
            "Notes/open/sustain_dead.png",
        };

        private static readonly Texture2D[] _body = new Texture2D[Count];
        private static readonly Texture2D[] _glow = new Texture2D[Count];

        public static Texture2D[] Body => _body;
        public static Texture2D[] Glow => _glow;
        public static bool IsLoaded { get; private set; }

        public static void LoadAll(string assetRoot, MelonLogger.Instance log)
        {
            if (IsLoaded)
                return;

            if (string.IsNullOrEmpty(assetRoot))
            {
                log.Warning("[ClonZones] Gh3SustainBank: no asset root; GH3 sustains unavailable.");
                return;
            }

            try
            {
                for (int i = 0; i < Count; i++)
                {
                    var tex = TryLoad(assetRoot, RelPaths[i], i, log);
                    if (tex == null)
                        continue;

                    _body[i] = tex;
                    try
                    {
                        _glow[i] = BuildGlow(tex, i);
                    }
                    catch (Exception ex)
                    {
                        log.Warning($"[ClonZones] Gh3SustainBank: {RelPaths[i]} glow unusable ({ex.Message}).");
                        _glow[i] = null;
                    }
                }

                IsLoaded = true;
                log.Msg($"[ClonZones] Gh3SustainBank ready: body={Describe(_body)}, glow={Describe(_glow)}.");
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] Gh3SustainBank.LoadAll fatal: {ex}");
            }
        }

        private static Texture2D TryLoad(string assetRoot, string rel, int index, MelonLogger.Instance log)
        {
            var full = Path.Combine(assetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                log.Warning($"[ClonZones] Gh3SustainBank: {rel} missing; that sustain falls back to vanilla.");
                return null;
            }

            Texture2D tex = null;
            try
            {
                var bytes = File.ReadAllBytes(full);
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes))
                    throw new InvalidDataException($"Unity could not decode {rel}.");

                if (tex.width <= 0 || tex.height <= 0)
                    throw new InvalidDataException($"{rel} decoded to {tex.width}x{tex.height}.");

                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = $"clonzones_sustain_{index}_tex";
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

                log.Msg($"[ClonZones] Loaded {rel} -> sustain body {index} ({tex.width}x{tex.height}).");
                return tex;
            }
            catch (Exception ex)
            {
                log.Warning($"[ClonZones] Gh3SustainBank: {rel} unusable ({ex.Message}); that sustain falls back to vanilla.");
                if (tex != null)
                {
                    try { UnityEngine.Object.Destroy(tex); }
                    catch (Exception destroyEx)
                    {
                        log.Warning($"[ClonZones] Gh3SustainBank: could not release a half-decoded texture ({destroyEx.Message}).");
                    }
                }
                return null;
            }
        }

        private static Texture2D BuildGlow(Texture2D body, int index)
        {
            int w = body.width;
            int h = body.height;

            Color32[] src = body.GetPixels32();
            // Match GH3's GPU sample at (0.9, 0.9), with top-down V.
            // GetPixelBilinear's CPU coordinates omit the half-texel offset;
            // on Rainbow this blends too far into the transparent right border.
            float sx = Mathf.Clamp(.9f * w - .5f, 0f, w - 1f);
            float sy = Mathf.Clamp(.1f * h - .5f, 0f, h - 1f);
            int x0 = Mathf.FloorToInt(sx), y0 = Mathf.FloorToInt(sy);
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            Color lane = Color.Lerp(
                Color.Lerp(src[y0 * w + x0], src[y0 * w + x1], sx - x0),
                Color.Lerp(src[y1 * w + x0], src[y1 * w + x1], sx - x0), sy - y0);
            var dst = new Color32[src.Length];

            byte r = (byte)Mathf.RoundToInt(Mathf.Clamp01(lane.r) * 255f);
            byte g = (byte)Mathf.RoundToInt(Mathf.Clamp01(lane.g) * 255f);
            byte b = (byte)Mathf.RoundToInt(Mathf.Clamp01(lane.b) * 255f);

            for (int y = 0; y < h; y++)
            {
                float v = 1f - (y + 0.5f) / h;
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w;
                    float prof = 1f - Mathf.Abs(2f * u - 1f);
                    float gg = Mathf.Clamp01(prof / 0.7f);
                    float edge = gg * gg * (3f - 2f * gg);
                    float a = edge * v * (src[y * w + x].a / 255f) * 0.85f;
                    dst[y * w + x] = new Color32(r, g, b, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
                }
            }

            var glow = new Texture2D(w, h, TextureFormat.RGBA32, false);
            glow.SetPixels32(dst);
            glow.Apply(false, false);
            glow.filterMode = FilterMode.Bilinear;
            glow.wrapMode = TextureWrapMode.Clamp;
            glow.name = $"clonzones_sustain_glow_{index}_tex";
            glow.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return glow;
        }

        private static string Describe(Texture2D[] texs)
        {
            int n = 0;
            foreach (var t in texs)
                if (t != null)
                    n++;
            return $"{n}/{texs.Length}";
        }
    }
}
