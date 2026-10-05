// Numeric storage only. These stubs do not test native Unity calls or raster output.
namespace UnityEngine
{
    public struct Vector2 { public float x,y; }
    public struct Vector3 { public float x,y,z; }
    public struct Matrix4x4
    {
        public float m00,m01,m02,m03,m10,m11,m12,m13,m20,m21,m22,m23,m30,m31,m32,m33;
    }
    public struct Rect
    {
        public float m_XMin,m_YMin,m_Width,m_Height;
        public float x { get => m_XMin; set => m_XMin = value; }
        public float y { get => m_YMin; set => m_YMin = value; }
        public float width { get => m_Width; set => m_Width = value; }
        public float height { get => m_Height; set => m_Height = value; }
    }
}
namespace ClonZones
{
    internal static class Gh3HighwayLayout
    {
        public const float CenterX=640,Playline=655,BottomWidth=512;
    }
}
