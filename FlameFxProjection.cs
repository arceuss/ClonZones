using System;
using UnityEngine;

namespace ClonZones
{
    // Reuse the camera inverse, not the highway's depth-dependent vertical fit.
    // NoteFX is a 2D element; its screen scale comes from the rendered fret span.
    internal readonly struct FlameFxProjection
    {
        private readonly Matrix4x4 _m;
        private readonly float _x, _y, _sx, _sy, _depth, _rx, _ry, _rw, _rh;
        public FlameFxProjection(Matrix4x4 m, float x, float y, float sx, float sy, float depth, Rect rect)
        {
            _m=m; _x=x; _y=y; _sx=sx; _sy=sy; _depth=depth;
            // the fields, not x/width/...: those getters are boxing invokes in the interop.
            _rx=rect.m_XMin; _ry=rect.m_YMin; _rw=rect.m_Width; _rh=rect.m_Height;
            if (_rw <= 0 || _rh <= 0) throw new InvalidOperationException("Flame viewport is empty.");
        }
        public static FlameFxProjection AtFrets(Matrix4x4 matrix, Vector3 first, Vector3 last, float depth, Rect rect)
        {
            // highway_2d.q: five centers span 4/5 of the 512-unit playline.
            float scale = Math.Abs(last.x - first.x) / (Gh3HighwayLayout.BottomWidth * 4f / 5f);
            if (!float.IsFinite(scale) || scale <= 0)
                throw new InvalidOperationException("Rendered fret span is empty.");
            return new FlameFxProjection(matrix, (first.x + last.x) * .5f, (first.y + last.y) * .5f,
                scale, scale, depth, rect);
        }
        public Vector2 VirtualPoint(float screenX, float screenY)
        {
            Vector2 point = default;
            point.x = Gh3HighwayLayout.CenterX + (screenX - _x) / _sx;
            point.y = Gh3HighwayLayout.Playline - (screenY - _y) / _sy;
            return point;
        }
        public Vector3 World(float x, float y)
        {
            float sx = _x + (x - Gh3HighwayLayout.CenterX) * _sx;
            float sy = _y + (Gh3HighwayLayout.Playline - y) * _sy;
            float nx = (sx - _rx) * 2f / _rw - 1f, ny = (sy - _ry) * 2f / _rh - 1f;
            float w = ((_m.m30*nx + _m.m31*ny) + _m.m32*_depth) + _m.m33;
            Vector3 p = default;
            p.x = (((_m.m00*nx + _m.m01*ny) + _m.m02*_depth) + _m.m03) / w;
            p.y = (((_m.m10*nx + _m.m11*ny) + _m.m12*_depth) + _m.m13) / w;
            p.z = (((_m.m20*nx + _m.m21*ny) + _m.m22*_depth) + _m.m23) / w;
            if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z))
                throw new InvalidOperationException("Invalid flame projection; not submitting nonfinite bounds.");
            return p;
        }
    }

}
