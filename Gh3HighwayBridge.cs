using Il2Cpp;
using UnityEngine;

namespace ClonZones
{
    internal sealed class Gh3HighwayBridge
    {
        // CH 1.1.0.6142's far distance at 100%. length changes visibility, not this depth map.
        public const float DepthSpan = 7.87f;
        private readonly Camera _camera;
        private readonly Transform _field;
        private readonly GuitarNoteRenderer _notes;
        private Matrix4x4 _projection;
        private Matrix4x4 _view;
        private Matrix4x4 _fieldMatrix;
        private Matrix4x4 _screenToWorld;
        private Rect _rect;
        private float _screenX;
        private float _screenY;
        private float _scaleX;
        private float _scaleY;
        private float _depth;
        private bool _ready;
        private double _far;
        private float _strike;
        private readonly float _halfWidth;
        public Vector2 PixelWorldSize { get; private set; }
        public Quaternion Rotation { get; private set; }
        public float TopY { get; private set; }

        // GH3 and WoRMod share the authored aspect. anchor it to CH's actual fret span.
        public Gh3HighwayBridge(Camera camera, Transform field, GuitarNoteRenderer notes)
        {
            _camera = camera;
            _field = field;
            _notes = notes;
            var frets = notes.GetComponent<BasePlayer>().neckController.FretAnimators;
            float first = field.InverseTransformPoint(frets[0].transform.position).x;
            float last = field.InverseTransformPoint(frets[4].transform.position).x;
            _halfWidth = Mathf.Abs(last - first) * 5f / 8f;
        }

        public bool Refresh()
        {
            // three bridges run this every frame; the interop getters and Matrix4x4 == boxed
            // ~40 IL2CPP objects per call (UnityIcalls has the numbers).
            Matrix4x4 projection = UnityIcalls.ProjectionMatrix(_camera);
            Matrix4x4 view = UnityIcalls.WorldToCameraMatrix(_camera);
            Matrix4x4 field = UnityIcalls.LocalToWorld(_field);
            Rect rect = UnityIcalls.PixelRect(_camera);
            double far = _notes.noteZPosFarLimit;
            float strike = _notes.strikeLine;
            if (_ready && UnityIcalls.MatrixEquals(projection, _projection) && UnityIcalls.MatrixEquals(view, _view)
                && UnityIcalls.MatrixEquals(field, _fieldMatrix) && UnityIcalls.RectEquals(rect, _rect) && far == _far && strike == _strike)
                return false;
            _projection = projection;
            _view = view;
            _fieldMatrix = field;
            _rect = rect;
            _far = far;
            _strike = strike;
            _screenToWorld = (projection * view).inverse;
            Vector3 center = Project(field.MultiplyPoint3x4(new Vector3(0f, 0f, strike)));
            Vector3 left = Project(field.MultiplyPoint3x4(new Vector3(-_halfWidth, 0f, strike)));
            Vector3 right = Project(field.MultiplyPoint3x4(new Vector3(_halfWidth, 0f, strike)));
            _screenX = center.x;
            _screenY = center.y;
            _scaleX = (right.x - left.x) / Gh3HighwayLayout.BottomWidth;
            _scaleY = _scaleX;
            _depth = center.z;
            PixelWorldSize = new Vector2((World(641f,655f)-World(640f,655f)).magnitude,
                (World(640f,654f)-World(640f,655f)).magnitude);
            Rotation = _camera.transform.rotation;
            TopY = DepthY((float)far);
            _ready = true;
            return true;
        }

        private Vector3 Project(Vector3 world)
        {
            Vector4 clip = _projection * (_view * new Vector4(world.x, world.y, world.z, 1f));
            return new Vector3(_rect.x + (clip.x / clip.w + 1f) * _rect.width * 0.5f,
                _rect.y + (clip.y / clip.w + 1f) * _rect.height * 0.5f, clip.z / clip.w);
        }

        public float VirtualY(Vector3 world)
        {
            return Gh3HighwayLayout.Playline - (Project(world).y - _screenY) / _scaleY;
        }


        public float TimeY(double timeUntilNote)
        {
            return DepthY((float)(timeUntilNote * _notes.noteSpeed + _strike));
        }

        public float DepthY(float z)
        {
            float row = (float)(1024.0 * (1.0 - (z - _strike) / (double)DepthSpan));
            return Gh3HighwayLayout.ExtendedYAtRow(row);
        }

        public float AlphaAtY(float y)
        {
            if (y <= TopY) return 0f;
            if (y >= TopY + Gh3HighwayLayout.Fade) return 1f;
            return (y - TopY) / Gh3HighwayLayout.Fade;
        }

        // note heads and sustain ribbons enter with GH3's smoothstep curve; bars, strings and
        // the backing keep their existing linear AlphaAtY.
        public float EntryAlphaAtY(float y) => Gh3HighwayLayout.EntryFade(y - TopY);

        public float ScreenUvY(float y) => Gh3HighwayLayout.ScreenUvY(y, _screenY, _scaleY, _rect.m_YMin, _rect.m_Height);

        public float FieldX(float x, float y)
        {
            return Gh3HighwayLayout.CenterX + x * Gh3HighwayLayout.WidthAtY(y) / (2f * _halfWidth);
        }

        public Vector3 FieldPoint(float x, float z)
        {
            float y = DepthY(z);
            return World(FieldX(x, y), y);
        }

        public float HalfWidth => _halfWidth;

        internal FlameFxProjection CaptureFlameProjection(Vector3 firstFretScreen, Vector3 lastFretScreen)
        {
            if (!_ready) throw new System.InvalidOperationException("Highway projection is not initialized.");
            return FlameFxProjection.AtFrets(_screenToWorld, firstFretScreen, lastFretScreen, _depth, _rect);
        }


        public Vector3 World(float x, float y)
        {
            float sx = _screenX + (x - Gh3HighwayLayout.CenterX) * _scaleX;
            float sy = _screenY + (Gh3HighwayLayout.Playline - y) * _scaleY;
            float nx = (sx - _rect.m_XMin) * 2f / _rect.m_Width - 1f;
            float ny = (sy - _rect.m_YMin) * 2f / _rect.m_Height - 1f;
            float wx = ((_screenToWorld.m00 * nx + _screenToWorld.m01 * ny) + _screenToWorld.m02 * _depth) + _screenToWorld.m03;
            float wy = ((_screenToWorld.m10 * nx + _screenToWorld.m11 * ny) + _screenToWorld.m12 * _depth) + _screenToWorld.m13;
            float wz = ((_screenToWorld.m20 * nx + _screenToWorld.m21 * ny) + _screenToWorld.m22 * _depth) + _screenToWorld.m23;
            float ww = ((_screenToWorld.m30 * nx + _screenToWorld.m31 * ny) + _screenToWorld.m32 * _depth) + _screenToWorld.m33;
            Vector3 world = default;
            world.x = wx / ww; world.y = wy / ww; world.z = wz / ww;
            return world;
        }
    }
}
