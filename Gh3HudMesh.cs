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
        private struct MaterialRun
        {
            public int Start, Count;
            public Gh3HudBlend Blend;
        }


        public const float AuthoredWidth = 1280f, AuthoredHeight = 720f;

        private readonly GameObject _object;
        private readonly Mesh _mesh;
        private readonly Material _material;
        private readonly Material _additiveMaterial;
        private readonly MeshRenderer _renderer;
        private readonly Camera _camera;
        private Vertex[] _vertices;
        private Il2CppStructArray<int> _indices;
        private Il2CppSystem.Array _indexArray;
        private MaterialRun[] _runs, _uploadedRuns;
        private Il2CppReferenceArray<Material> _sharedMaterials;
        private int _count;
        private int _runCount;
        private int _uploadedCount = -1;
        private int _uploadedRunCount = -1;
        private int _meshSubmeshCount = -1;
        private int _quadCapacity;
        private bool _materialsBound;
        private bool _disposed;


        // Presentation transform for the current frame.
        private float _scale, _offsetX, _offsetY, _depth;
        public float Scale => _scale;
        public Vector2 Offset => new(_offsetX, _offsetY);
        public Rect Viewport { get; private set; }

        public Gh3HudMesh(string name, Texture texture, Shader shader, Camera camera, int layer, int sortingLayer, int order, int queue, int quads, Shader additiveShader = null)
        {
            try
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
                // this material is unbound during scene loading, so Unity must not unload it before the first draw.
                _additiveMaterial = additiveShader == null ? null :
                    new Material(additiveShader) { mainTexture = texture, renderQueue = queue, name = name + "_add_mat", hideFlags = HideFlags.DontUnloadUnusedAsset };
                if (_additiveMaterial != null)
                {
                    _additiveMaterial.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
                }
                _renderer.sharedMaterial = _material;
                _renderer.sortingLayerID = sortingLayer;
                _renderer.sortingOrder = order;
                _renderer.shadowCastingMode = ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
                _renderer.lightProbeUsage = LightProbeUsage.Off;
                _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                Reserve(quads);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Reserve(int quads)
        {
            int capacity = checked(Math.Max(1, quads) * 6);
            if (_vertices != null && _vertices.Length >= capacity) return;
            _vertices = new Vertex[capacity];
            _quadCapacity = capacity / 6;
            if (_additiveMaterial != null)
            {
                Array.Resize(ref _runs, _quadCapacity);
                Array.Resize(ref _uploadedRuns, _quadCapacity);
            }
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
            if (_additiveMaterial != null)
            {
                _uploadedRunCount = -1;
                _meshSubmeshCount = -1;
                _materialsBound = false;
            }
        }

        /// <summary>Recompute the outer transform from the camera's current pixel rect and projection. Call once per frame before Begin.</summary>
        public bool RefreshViewport()
        {
            Rect rect = UnityIcalls.PixelRect(_camera);
            Matrix4x4 projection = UnityIcalls.ProjectionMatrix(_camera);
            float nearClip = UnityIcalls.NearClipPlane(_camera);
            bool orthographic = _camera.orthographic;
            bool changed = !_projectionValid || !UnityIcalls.RectEquals(rect, Viewport) || nearClip != _lastNearClip ||
                           orthographic != _lastOrthographic || !SameMatrix(projection, _lastProjection);
            if (!changed) return false;
            _projectionValid = true; _lastProjection = projection;
            _lastNearClip = nearClip; _lastOrthographic = orthographic;
            Viewport = rect;
            float w = rect.width, h = rect.height;
            _viewportHeight = h;
            _scale = Mathf.Min(w / AuthoredWidth, h / AuthoredHeight);
            _offsetX = (w - AuthoredWidth * _scale) * 0.5f;
            _offsetY = (h - AuthoredHeight * _scale) * 0.5f;
            _depth = nearClip + 0.05f;
            // Sample the camera's real projection (CH may set a custom matrix; FOV math would not
            // match). At a fixed view depth the pixel->camera-space map is affine, so three
            // unprojected points define it.
            Matrix4x4 inv = projection.inverse;
            Vector3 Unproject(float px, float py)
            {
                // NDC on the near plane (GL convention: z = -1), then to view space; slide the
                // perspective ray out to _depth, keep orthographic rays parallel.
                Vector4 v = inv * new Vector4(px * 2f / w - 1f, py * 2f / h - 1f, -1f, 1f);
                Vector3 view = new Vector3(v.x, v.y, v.z) / v.w;
                if (!orthographic) view *= _depth / nearClip;
                // Camera space looks down -z; the mesh is a child of the camera transform, which looks down +z.
                return new Vector3(view.x, view.y, -view.z);
            }
            _origin = Unproject(0f, 0f);
            _axisX = Unproject(1f, 0f) - _origin;
            _axisY = Unproject(0f, 1f) - _origin;
            return true;
        }

        // Field compare: the interop Matrix4x4 indexer and == are calls into the player.
        private static bool SameMatrix(in Matrix4x4 a, in Matrix4x4 b) =>
            a.m00 == b.m00 && a.m01 == b.m01 && a.m02 == b.m02 && a.m03 == b.m03 &&
            a.m10 == b.m10 && a.m11 == b.m11 && a.m12 == b.m12 && a.m13 == b.m13 &&
            a.m20 == b.m20 && a.m21 == b.m21 && a.m22 == b.m22 && a.m23 == b.m23 &&
            a.m30 == b.m30 && a.m31 == b.m31 && a.m32 == b.m32 && a.m33 == b.m33;

        private bool _projectionValid;
        private Matrix4x4 _lastProjection;
        private float _lastNearClip;
        private float _viewportHeight;
        private bool _lastOrthographic;
        private Vector3 _origin, _axisX, _axisY;

        /// <summary>Authored (y-down) point to camera-local space via the sampled affine map.</summary>
        public Vector3 Local(Vector2 authored)
        {
            float sx = _offsetX + authored.x * _scale;
            float syFromTop = _offsetY + authored.y * _scale;
            float syBottomUp = _viewportHeight - syFromTop;
            Vector3 point = default;
            point.x = (_origin.x + _axisX.x * sx) + _axisY.x * syBottomUp;
            point.y = (_origin.y + _axisX.y * sx) + _axisY.y * syBottomUp;
            point.z = (_origin.z + _axisX.z * sx) + _axisY.z * syBottomUp;
            return point;
        }

        /// <summary>Authored point to screen pixels (origin top-left) for diagnostics and captures.</summary>
        public Vector2 ScreenTopLeft(Vector2 authored) => new(_offsetX + authored.x * _scale, _offsetY + authored.y * _scale);

        public void Begin()
        {
            _count = 0;
            if (_additiveMaterial is not null) _runCount = 0;
        }

        public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float u0, float v0, float u1, float v1, Color32 color, Gh3HudBlend blend = Gh3HudBlend.Alpha)
        {
            if (blend != Gh3HudBlend.Alpha && blend != Gh3HudBlend.Add)
                throw new ArgumentOutOfRangeException(nameof(blend), blend, "Unsupported GH3 HUD blend mode");
            if (blend == Gh3HudBlend.Add && _additiveMaterial is null)
                throw new InvalidOperationException("An additive HUD quad requires an additive shader material");
            if (_count + 6 > _vertices.Length)
            {
                if (_additiveMaterial is not null)
                    throw new InvalidOperationException($"GH3 HUD mesh capacity {_quadCapacity} is insufficient for an additive/WORMod frame");
                return;
            }

            int runIndex = -1;
            if (_additiveMaterial is not null)
            {
                runIndex = _runCount - 1;
                if (runIndex < 0 || _runs[runIndex].Blend != blend)
                {
                    if (_runCount == _runs.Length)
                        throw new InvalidOperationException($"GH3 HUD material-run capacity {_runs.Length} is insufficient");
                    runIndex = _runCount++;
                    _runs[runIndex] = new MaterialRun { Start = _count, Count = 0, Blend = blend };
                }
            }

            Vector3 pa = Local(a), pb = Local(b), pc = Local(c), pd = Local(d);
            // a=top-left b=top-right c=bottom-right d=bottom-left in authored space; UVs (u0,v0) at a, (u1,v1) at c.
            _vertices[_count++] = new Vertex { Position = pa, Color = color, UV = new Vector2 { x = u0, y = v0 } };
            _vertices[_count++] = new Vertex { Position = pb, Color = color, UV = new Vector2 { x = u1, y = v0 } };
            _vertices[_count++] = new Vertex { Position = pc, Color = color, UV = new Vector2 { x = u1, y = v1 } };
            _vertices[_count++] = new Vertex { Position = pa, Color = color, UV = new Vector2 { x = u0, y = v0 } };
            _vertices[_count++] = new Vertex { Position = pc, Color = color, UV = new Vector2 { x = u1, y = v1 } };
            _vertices[_count++] = new Vertex { Position = pd, Color = color, UV = new Vector2 { x = u0, y = v1 } };
            if (runIndex >= 0) _runs[runIndex].Count += 6;
        }

        public int QuadCount => _count / 6;
        public int QuadCapacity => _quadCapacity;

        public unsafe void Upload()
        {
            _renderer.enabled = _count != 0;
            if (_count == 0) return;

            fixed (Vertex* vertices = _vertices)
                _mesh.InternalSetVertexBufferData(0, (IntPtr)vertices, 0, 0, _count, sizeof(Vertex), MeshUpdateFlags.DontRecalculateBounds);

            if (_additiveMaterial is null)
            {
                // Preserve the original GH3 single-material upload path exactly.
                if (_uploadedCount != _count)
                {
                    _mesh.SetIndicesImpl(0, MeshTopology.Triangles, IndexFormat.UInt32, _indexArray, 0, _count, false, 0);
                    _uploadedCount = _count;
                }
            }
            else
            {
                bool runCountChanged = _uploadedRunCount != _runCount;
                if (_meshSubmeshCount != _runCount)
                {
                    _mesh.subMeshCount = _runCount;
                    _meshSubmeshCount = _runCount;
                    runCountChanged = true;
                }
                for (int i = 0; i < _runCount; i++)
                {
                    MaterialRun run = _runs[i];
                    if (runCountChanged || !SameRange(run, _uploadedRuns[i]))
                        _mesh.SetIndicesImpl(i, MeshTopology.Triangles, IndexFormat.UInt32, _indexArray, run.Start, run.Count, false, 0);
                }
                UpdateMaterials();
                Array.Copy(_runs, _uploadedRuns, _runCount);
                _uploadedRunCount = _runCount;
                _uploadedCount = _count;
            }

            Vector3 min = _vertices[0].Position, max = min;
            for (int i = 1; i < _count; i++)
            {
                Vector3 p = _vertices[i].Position;
                // preserve Unity's operand selection on ties and unordered comparisons.
                min.x = min.x < p.x ? min.x : p.x; max.x = max.x > p.x ? max.x : p.x;
                min.y = min.y < p.y ? min.y : p.y; max.y = max.y > p.y ? max.y : p.y;
                min.z = min.z < p.z ? min.z : p.z; max.z = max.z > p.z ? max.z : p.z;
            }
            Bounds bounds = default; bounds.SetMinMax(min, max); _mesh.bounds = bounds;
        }

        private static bool SameRange(MaterialRun a, MaterialRun b) => a.Start == b.Start && a.Count == b.Count;

        private void UpdateMaterials()
        {
            bool topologyChanged = !_materialsBound || _sharedMaterials == null || _sharedMaterials.Length != _runCount;
            bool changed = topologyChanged;
            if (_sharedMaterials == null || _sharedMaterials.Length != _runCount)
            {
                _sharedMaterials = new Il2CppReferenceArray<Material>(_runCount);
                ClonZonesBenchmark.Mark(BenchmarkEvent.MaterialArray);
            }
            for (int i = 0; i < _runCount; i++)
            {
                bool blendChanged = topologyChanged || _uploadedRuns[i].Blend != _runs[i].Blend;
                if (blendChanged)
                {
                    _sharedMaterials[i] = _runs[i].Blend == Gh3HudBlend.Add ? _additiveMaterial : _material;
                    changed = true;
                }
            }
            if (changed) _renderer.sharedMaterials = _sharedMaterials;
            _materialsBound = true;
        }

        public void Disable() { if (_renderer != null) _renderer.enabled = false; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disable();
            if (_object != null) UnityEngine.Object.Destroy(_object);
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_additiveMaterial != null) UnityEngine.Object.Destroy(_additiveMaterial);
        }
    }
}
