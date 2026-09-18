using Il2Cpp;
using UnityEngine;

namespace ClonZones
{
    internal sealed class Gh3HighwayBridge
    {
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
            Matrix4x4 projection = _camera.projectionMatrix;
            Matrix4x4 view = _camera.worldToCameraMatrix;
            Matrix4x4 field = _field.localToWorldMatrix;
            Rect rect = _camera.pixelRect;
            double far = _notes.noteZPosFarLimit;
            float strike = _notes.strikeLine;
            if (_ready && projection == _projection && view == _view && field == _fieldMatrix
                && rect == _rect && far == _far && strike == _strike)
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
            Vector3 top = Project(field.MultiplyPoint3x4(new Vector3(0f, 0f, (float)far)));
            _screenX = center.x;
            _screenY = center.y;
            _scaleX = (right.x - left.x) / Gh3HighwayLayout.BottomWidth;
            _scaleY = (top.y - center.y) / Gh3HighwayLayout.Height;
            _depth = center.z;
            PixelWorldSize = new Vector2((World(641f,655f)-World(640f,655f)).magnitude,
                (World(640f,654f)-World(640f,655f)).magnitude);
            Rotation = _camera.transform.rotation;
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
            // CH determines the visible seconds; the GH3 table supplies projection.
            float row = (float)(1024.0 * (1.0 - (z - _strike) / (_far - _strike)));
            return Gh3HighwayLayout.YAtRow(row);
        }

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

        public Vector3 World(float x, float y)
        {
            float sx = _screenX + (x - Gh3HighwayLayout.CenterX) * _scaleX;
            float sy = _screenY + (Gh3HighwayLayout.Playline - y) * _scaleY;
            Vector4 world = _screenToWorld * new Vector4(
                (sx - _rect.x) * 2f / _rect.width - 1f,
                (sy - _rect.y) * 2f / _rect.height - 1f, _depth, 1f);
            return new Vector3(world.x / world.w, world.y / world.w, world.z / world.w);
        }
    }
}
