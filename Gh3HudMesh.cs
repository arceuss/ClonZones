using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClonZones
{
    /// <summary>
    /// The HUD's single mesh: one MeshRenderer parented to the rendering camera, fed
    /// authored 1280x720 quads through the outer presentation transform (uniform scale
    /// `min(W/1280, H/720)`, centred letterbox) into camera-local coordinates on a plane
    /// just past the near clip. Uniform on purpose: the highway bridge's independent X/Y
    /// scales would turn the Nixie tube into an oval.
    /// </summary>
    internal sealed class Gh3HudMesh : IGh3HudQuadSink, IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public Vector3 Position;
            public Color32 Color;
            public Vector2 UV;
        }

        public const float AuthoredWidth = 1280f, AuthoredHeight = 720f;

        private readonly GameObject _object;
        private readonly Mesh _mesh;
        private readonly Material _material;
        private readonly MeshRenderer _renderer;
        private readonly Camera _camera;
        private Vertex[] _vertices;
        private Il2CppStructArray<int> _indices;
        private Il2CppSystem.Array _indexArray;
        private int _count;
        private int _uploadedCount = -1;
        private int _quadCapacity;

        // Presentation transform for the current frame.
        private float _scale, _offsetX, _offsetY, _depth;
        public float Scale => _scale;
        public Vector2 Offset => new(_offsetX, _offsetY);
        public Rect Viewport { get; private set; }

        public Gh3HudMesh(string name, Texture texture, Shader shader, Camera camera, int layer, int sortingLayer, int order, int queue, int quads)
        {
            _camera = camera;
            _object = new GameObject(name) { layer = layer };
            _object.transform.SetParent(camera.transform, false);
            _object.transform.localPosition = Vector3.zero;
            _object.transform.localRotation = Quaternion.identity;
            _object.transform.localScale = Vector3.one;
            _mesh = new Mesh { name = name };
            _mesh.MarkDynamic();
            _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _object.AddComponent<MeshRenderer>();
            _material = new Material(shader) { mainTexture = texture, renderQueue = queue, name = name + "_mat" };
            _renderer.sharedMaterial = _material;
            _renderer.sortingLayerID = sortingLayer;
            _renderer.sortingOrder = order;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            Reserve(quads);
        }

        public void Reserve(int quads)
        {
            int capacity = checked(Math.Max(1, quads) * 6);
            if (_vertices != null && _vertices.Length >= capacity) return;
            _quadCapacity = capacity / 6;
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

        /// <summary>Recompute the outer transform from the camera's current pixel rect and projection. Call once per frame before Begin.</summary>
        public void RefreshViewport()
        {
            Rect rect = _camera.pixelRect;
            Viewport = rect;
            float w = rect.width, h = rect.height;
            _scale = Mathf.Min(w / AuthoredWidth, h / AuthoredHeight);
            _offsetX = (w - AuthoredWidth * _scale) * 0.5f;
            _offsetY = (h - AuthoredHeight * _scale) * 0.5f;
            _depth = _camera.nearClipPlane + 0.05f;
            // Sample the camera's real projection (CH may set a custom matrix; FOV math would not
            // match). At a fixed view depth the pixel->camera-space map is affine, so three
            // unprojected points define it.
            Matrix4x4 inv = _camera.projectionMatrix.inverse;
            bool ortho = _camera.orthographic;
            float near = _camera.nearClipPlane;
            Vector3 Unproject(float px, float py)
            {
                // NDC on the near plane (GL convention: z = -1), then to view space; slide the
                // perspective ray out to _depth, keep orthographic rays parallel.
                Vector4 v = inv * new Vector4(px * 2f / w - 1f, py * 2f / h - 1f, -1f, 1f);
                Vector3 view = new Vector3(v.x, v.y, v.z) / v.w;
                if (!ortho) view *= _depth / near;
                // Camera space looks down -z; the mesh is a child of the camera transform, which looks down +z.
                return new Vector3(view.x, view.y, -view.z);
            }
            _origin = Unproject(0f, 0f);
            _axisX = Unproject(1f, 0f) - _origin;
            _axisY = Unproject(0f, 1f) - _origin;
        }

        private Vector3 _origin, _axisX, _axisY;

        /// <summary>Authored (y-down) point to camera-local space via the sampled affine map.</summary>
        public Vector3 Local(Vector2 authored)
        {
            float sx = _offsetX + authored.x * _scale;
            float syFromTop = _offsetY + authored.y * _scale;
            float syBottomUp = Viewport.height - syFromTop;
            return _origin + _axisX * sx + _axisY * syBottomUp;
        }

        /// <summary>Authored point to screen pixels (origin top-left) for diagnostics and captures.</summary>
        public Vector2 ScreenTopLeft(Vector2 authored) => new(_offsetX + authored.x * _scale, _offsetY + authored.y * _scale);

        public void Begin() { _count = 0; }

        public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float u0, float v0, float u1, float v1, Color32 color)
        {
            if (_count + 6 > _vertices.Length) return; // bounded: drop rather than grow on the hot path
            Vector3 pa = Local(a), pb = Local(b), pc = Local(c), pd = Local(d);
            // a=top-left b=top-right c=bottom-right d=bottom-left in authored space; UVs (u0,v0) at a, (u1,v1) at c.
            _vertices[_count++] = new Vertex { Position = pa, Color = color, UV = new Vector2(u0, v0) };
            _vertices[_count++] = new Vertex { Position = pb, Color = color, UV = new Vector2(u1, v0) };
            _vertices[_count++] = new Vertex { Position = pc, Color = color, UV = new Vector2(u1, v1) };
            _vertices[_count++] = new Vertex { Position = pa, Color = color, UV = new Vector2(u0, v0) };
            _vertices[_count++] = new Vertex { Position = pc, Color = color, UV = new Vector2(u1, v1) };
            _vertices[_count++] = new Vertex { Position = pd, Color = color, UV = new Vector2(u0, v1) };
        }

        public int QuadCount => _count / 6;
        public int QuadCapacity => _quadCapacity;

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
            Vector3 min = _vertices[0].Position, max = min;
            for (int i = 1; i < _count; i++) { min = Vector3.Min(min, _vertices[i].Position); max = Vector3.Max(max, _vertices[i].Position); }
            var bounds = new Bounds(); bounds.SetMinMax(min, max); _mesh.bounds = bounds;
        }

        public void Disable() { if (_renderer != null) _renderer.enabled = false; }

        public void Dispose()
        {
            Disable();
            if (_object != null) UnityEngine.Object.Destroy(_object);
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            if (_material != null) UnityEngine.Object.Destroy(_material);
        }
    }
}
