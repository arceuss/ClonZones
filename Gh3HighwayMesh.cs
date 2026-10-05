using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClonZones
{
    internal sealed class Gh3HighwayMesh : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public Vector3 Position;
            public Color32 Color;
            public Vector2 UV;
        }

        private struct Point
        {
            public float X, Y, U, V;
            public Point(float x, float y, float u, float v) { X = x; Y = y; U = u; V = v; }
        }

        private readonly GameObject _object;
        private readonly Mesh _mesh;
        private readonly Material _material;
        private readonly MeshRenderer _renderer;
        private readonly bool _fade;
        private readonly bool _ownsMaterial;
        private readonly Gh3HighwayBridge _bridge;
        private readonly Point[] _quad = new Point[4];
        private readonly Point[] _clipA = new Point[8];
        private readonly Point[] _clipB = new Point[8];
        private Vertex[] _vertices;
        private Il2CppStructArray<int> _indices;
        private Il2CppSystem.Array _indexArray;
        private int _count;
        private int _uploadedCount = -1;

        public Gh3HighwayMesh(string name, Texture texture, Shader shader, Gh3HighwayBridge bridge,
            int layer, int sortingLayer, int order, int queue, int quads, bool fade = true, Material material = null)
        {
            _bridge = bridge;
            _fade = fade;
            _object = new GameObject(name);
            _object.layer = layer;
            _mesh = new Mesh { name = name };
            _mesh.MarkDynamic();
            _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _object.AddComponent<MeshRenderer>();
            _ownsMaterial = material == null;
            _material = material ?? new Material(shader) { mainTexture = texture, renderQueue = queue };
            _renderer.sharedMaterial = _material;
            _renderer.sortingLayerID = sortingLayer;
            _renderer.sortingOrder = order;
            Reserve(quads);
        }

        public void Reserve(int quads)
        {
            int capacity = checked(Math.Max(1, quads) * 24);
            if (_vertices != null && _vertices.Length >= capacity) return;
            if (_vertices != null) ClonZonesBenchmark.Mark(BenchmarkEvent.MeshGrowth);
            Array.Resize(ref _vertices, capacity);
            _indices = new Il2CppStructArray<int>(capacity);
            for (int i = 0; i < capacity; i++) _indices[i] = i;
            _indexArray = _indices.Cast<Il2CppSystem.Array>();
            _mesh.indexFormat = IndexFormat.UInt32;
            _mesh.SetVertexBufferParams(capacity, new VertexAttributeDescriptor[] {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 0),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 0)
            });
            _uploadedCount = -1;
        }

        public void Begin() { _count = 0; }

        public void SetAdditiveTint()
        {
            _material.SetColor("_TintColor", new Color(.5f, .5f, .5f, .5f));
        }

        public void SetPropertyBlock(MaterialPropertyBlock block) { _renderer.SetPropertyBlock(block); }

        public void WorldQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
            float v0, float v1, Color32 c0, Color32 c1, float u0 = 0f, float u1 = 1f)
        {
            if (_count + 6 > _vertices.Length) Reserve((_vertices.Length / 24) * 2);
            Vertex a = new Vertex { Position = p0, Color = c0, UV = new Vector2 { x = u0, y = v0 } };
            Vertex b = new Vertex { Position = p1, Color = c0, UV = new Vector2 { x = u1, y = v0 } };
            Vertex c = new Vertex { Position = p2, Color = c1, UV = new Vector2 { x = u1, y = v1 } };
            Vertex d = new Vertex { Position = p3, Color = c1, UV = new Vector2 { x = u0, y = v1 } };
            _vertices[_count++] = a; _vertices[_count++] = b; _vertices[_count++] = c;
            _vertices[_count++] = a; _vertices[_count++] = c; _vertices[_count++] = d;
        }

        public void Bar(float y, float width, float height, Color32 color)
        {
            float x = Gh3HighwayLayout.CenterX;
            SetQuad(new Point(x-width/2f,y-height/2f,0f,1f), new Point(x+width/2f,y-height/2f,1f,1f),
                new Point(x+width/2f,y+height/2f,1f,0f), new Point(x-width/2f,y+height/2f,0f,0f), color);
        }

        public void Strip(float ax, float ay, float dx, float dy, float length, float width, bool mirror, Color32 color,
            float coverageX = 1f, float coverageY = 1f, float lengthScale = 1f)
        {
            float inverseLength = 1f / MathF.Sqrt(dx*dx + dy*dy);
            float nx = dx * inverseLength, ny = dy * inverseLength;
            float px = -ny * width * 0.5f, py = nx * width * 0.5f;
            float tx = ax + nx * (length * lengthScale), ty = ay + ny * (length * lengthScale);
            float u0 = mirror ? 1f : 0f, u1 = mirror ? 0f : 1f;
            // GH3's D3DX_FILTER_NONE upload pads NPOT art at the right/bottom.
            // Keep the original sprite pivot, but emit only the source-covered
            // rectangle instead of allocating the transparent padded texture.
            float left = mirror ? 1f - 2f * coverageX : -1f;
            float right = mirror ? 1f : 2f * coverageX - 1f;
            // extend the tip, not the NPOT padding trim, or the visible near edge moves.
            float bx = ax + nx * length * (1f - coverageY);
            float by = ay + ny * length * (1f - coverageY);
            SetQuad(new Point(bx+px*left,by+py*left,u0,0f), new Point(bx+px*right,by+py*right,u1,0f),
                new Point(tx+px*right,ty+py*right,u1,1f), new Point(tx+px*left,ty+py*left,u0,1f), color);
        }

        private void SetQuad(Point a, Point b, Point c, Point d, Color32 color)
        {
            _quad[0]=a; _quad[1]=b; _quad[2]=c; _quad[3]=d;
            if (!_fade)
            {
                Emit(_quad, 4, color);
                return;
            }
            // Split at both fade planes. Linear vertex alpha is then exact within
            // each band, even for the rotated string/sidebar quads.
            float top = _bridge.TopY;
            int n = Clip(_quad, 4, _clipA, top, true);
            n = Clip(_clipA, n, _clipB, top + Gh3HighwayLayout.Fade, false);
            Emit(_clipB, n, color);
            n = Clip(_quad, 4, _clipA, top + Gh3HighwayLayout.Fade, true);
            Emit(_clipA, n, color);
        }

        private static int Clip(Point[] input, int count, Point[] output, float y, bool greater)
        {
            if (count == 0) return 0;
            int written = 0;
            Point a = input[count-1];
            bool aInside = greater ? a.Y >= y : a.Y <= y;
            for (int i=0; i<count; i++)
            {
                Point b = input[i];
                bool bInside = greater ? b.Y >= y : b.Y <= y;
                if (aInside != bInside)
                {
                    float t = (y-a.Y)/(b.Y-a.Y);
                    output[written++] = new Point(a.X+(b.X-a.X)*t, y, a.U+(b.U-a.U)*t, a.V+(b.V-a.V)*t);
                }
                if (bInside) output[written++] = b;
                a=b; aInside=bInside;
            }
            return written;
        }

        private void Emit(Point[] polygon, int count, Color32 color)
        {
            for (int i=1; i<count-1; i++)
            {
                Add(polygon[0],color); Add(polygon[i],color); Add(polygon[i+1],color);
            }
        }

        private void Add(Point point, Color32 color)
        {
            if (_fade) color.a = (byte)(color.a * _bridge.AlphaAtY(point.Y));
            _vertices[_count++] = new Vertex { Position = _bridge.World(point.X,point.Y), Color=color, UV=new Vector2 { x=point.U, y=point.V } };
        }

        public unsafe void Upload()
        {
            _renderer.enabled = _count != 0;
            if (_count == 0) return;
            fixed (Vertex* vertices = _vertices)
                _mesh.InternalSetVertexBufferData(0, (IntPtr)vertices, 0, 0, _count, sizeof(Vertex), MeshUpdateFlags.DontRecalculateBounds);
            if (_uploadedCount != _count)
            {
                _mesh.SetIndicesImpl(0, MeshTopology.Triangles, IndexFormat.UInt32, _indexArray, 0, _count, false, 0);
                _uploadedCount = _count;
            }
            Vector3 min=_vertices[0].Position, max=min;
            for (int i = 1; i < _count; i++)
            {
                Vector3 p = _vertices[i].Position;
                min.x = min.x < p.x ? min.x : p.x; max.x = max.x > p.x ? max.x : p.x;
                min.y = min.y < p.y ? min.y : p.y; max.y = max.y > p.y ? max.y : p.y;
                min.z = min.z < p.z ? min.z : p.z; max.z = max.z > p.z ? max.z : p.z;
            }
            Bounds bounds = default; bounds.SetMinMax(min,max); _mesh.bounds=bounds;
        }

        public void Disable() { if (_renderer != null) _renderer.enabled = false; }
        public void Dispose()
        {
            Disable();
            UnityEngine.Object.Destroy(_object);
            UnityEngine.Object.Destroy(_mesh);
            if (_ownsMaterial) UnityEngine.Object.Destroy(_material);
        }
    }
}
