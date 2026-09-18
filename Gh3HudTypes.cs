using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// One rectangle inside the runtime HUD atlas. <see cref="Width"/>/<see cref="Height"/>
    /// are the source texel dimensions (the GH3 sprite's default dims when the
    /// script gives none); the UVs already exclude the gutter.
    /// </summary>
    internal readonly struct Gh3HudRegion
    {
        public readonly int Width, Height;
        public readonly float U0, V0, U1, V1;
        public Gh3HudRegion(int width, int height, float u0, float v0, float u1, float v1)
        { Width = width; Height = height; U0 = u0; V0 = v0; U1 = u1; V1 = v1; }
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
