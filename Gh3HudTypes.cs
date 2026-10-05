using UnityEngine;

namespace ClonZones
{
    /// <summary>Blend mode selected by a source HUD declaration.</summary>
    internal enum Gh3HudBlend
    {
        Alpha = 0,
        Add = 1,
    }

    /// <summary>
    /// One rectangle inside the runtime HUD atlas. <see cref="Width"/>/<see cref="Height"/>
    /// are the source texel dimensions (the GH3 sprite's default dims when the
    /// script gives none); the UVs already exclude the gutter.
    /// GH3 PC uploads every image through D3DXCreateTextureFromFileInMemoryEx with
    /// Width/Height = 0 and FILTER_NONE (5F8A57, the only texture-creation path), so the
    /// GPU texture rounds up to a power of two with transparent black padding while the
    /// sprite keeps the TEX size (cullWidth/cullHeight in SpriteElement::MutateTextures
    /// 4FAC60). Nothing queries the padded size, so the art covers only this fraction of
    /// the quad. Same emulation as the highway sidebar.
    /// </summary>
    internal readonly struct Gh3HudRegion
    {
        public readonly int Width, Height;
        public readonly float U0, V0, U1, V1;
        public readonly float CoverageX, CoverageY;
        public Gh3HudRegion(int width, int height, float u0, float v0, float u1, float v1)
        {
            Width = width; Height = height; U0 = u0; V0 = v0; U1 = u1; V1 = v1;
            CoverageX = width > 0 ? (float)width / Mathf.NextPowerOfTwo(width) : 1f;
            CoverageY = height > 0 ? (float)height / Mathf.NextPowerOfTwo(height) : 1f;
        }
        public bool IsValid => Width > 0;
    }

    /// <summary>
    /// Straight-alpha RGBA bitmap in top-down row order, the convention every decoder
    /// here produces before packing.
    /// </summary>
    internal sealed class Gh3HudBitmap
    {
        public readonly int Width, Height;
        public readonly Color32[] Pixels;
        public Gh3HudBitmap(int width, int height, Color32[] pixels) { Width = width; Height = height; Pixels = pixels; }
    }
}
