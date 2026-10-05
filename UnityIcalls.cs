using System;
using System.Reflection;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Box-free versions of the Unity calls ClonZones makes every frame.
    ///
    /// Il2CppInterop sends every value-returning Unity member through il2cpp_runtime_invoke, which
    /// boxes the result on Unity's IL2CPP heap (a Vector3 is a 32-byte object, a bool 24 bytes).
    /// That also covers Object ==/!= and the Vector/Mathf operators. At ~1200 updates/s ClonZones
    /// was making 36-47 MB/s of that garbage against CH's own ~0.7 MB/s, so Unity's incremental GC
    /// finished a cycle 4-7 times a second and those frames ran ~1 ms long (most of the p99.9 tail
    /// on drunken2/3999ascent in the session 2 benchmarks).
    ///
    /// These call the same icalls the compiled wrappers call (CH 1.1.0.6142, Unity 2022.3.62f2;
    /// signature strings and argument lists read from GameAssembly), so the values are the same
    /// native results, written into a stack struct instead of a box. Unity raises a destroyed-object
    /// error from inside an icall with a native throw, which must never unwind through a CoreCLR
    /// frame, so every call checks m_CachedPtr first, the same test Unity's own `obj != null` makes
    /// (Object.op_Inequality 0x18287FED0 reads +0x10). A dead object gets a managed
    /// NullReferenceException instead, which callers already handle like the Il2CppException the
    /// interop path threw.
    ///
    /// Main thread only, like the icalls themselves. If anything fails to resolve, every helper
    /// falls back to the original interop member (and its garbage).
    /// </summary>
    internal static unsafe class UnityIcalls
    {
        private static bool _ready;
        private static int _cachedPtr;
        // Matrix/Vector4 equality epsilon: Vector4.op_Equality 0x18288F590 compares the squared
        // difference with this float (kEpsilon * kEpsilon rounded to float).
        private static readonly float VectorEpsilonSquared = BitConverter.Int32BitsToSingle(0x2EDBE6FE);

        private static delegate* unmanaged[Cdecl]<IntPtr, Vector3*, void> _getLocalPosition, _getLocalScale, _getLossyScale, _setPosition, _setLocalScale;
        private static delegate* unmanaged[Cdecl]<IntPtr, Quaternion*, void> _setRotation;
        private static delegate* unmanaged[Cdecl]<IntPtr, Matrix4x4*, void> _getLocalToWorld, _getWorldToLocal, _getProjection, _getWorldToCamera;
        private static delegate* unmanaged[Cdecl]<IntPtr, Vector3*, Vector3*, void> _transformPoint;
        private static delegate* unmanaged[Cdecl]<IntPtr, Vector3*, int, Vector3*, void> _worldToScreenPoint;
        private static delegate* unmanaged[Cdecl]<IntPtr, Rect*, void> _getPixelRect, _getSpriteRect;
        private static delegate* unmanaged[Cdecl]<IntPtr, Vector2*, void> _getSpritePivot, _getSpriteRendererSize;
        private static delegate* unmanaged[Cdecl]<IntPtr, Color*, void> _getSpriteRendererColor;
        private static delegate* unmanaged[Cdecl]<IntPtr, float> _getPixelsPerUnit, _getNearClipPlane;
        private static delegate* unmanaged[Cdecl]<IntPtr, byte> _getActiveSelf, _getRendererEnabled;
        private static delegate* unmanaged[Cdecl]<IntPtr, int> _getSortingOrder;
        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _getSprite;
        private static delegate* unmanaged[Cdecl]<IntPtr, int, Vector4*, void> _getMaterialVector;
        private static delegate* unmanaged[Cdecl]<Color*, IntPtr, Color32> _colorToColor32;
        private static IntPtr _colorToColor32Method;
        private static delegate* unmanaged[Cdecl]<float> _getTime, _getUnscaledDeltaTime, _getUnscaledTime;
        private static delegate* unmanaged[Cdecl]<int> _getFrameCount;
        private static delegate* unmanaged[Cdecl]<int, byte> _mouseButtonDown, _mouseButtonUp;

        // Camera.WorldToScreenPoint(Vector3) passes MonoOrStereoscopicEye.Mono (0x18285B1EA: mov r8d, 2).
        private const int EyeMono = 2;

        public static bool Ready => _ready;

        public static void Initialize(MelonLogger.Instance log)
        {
            if (_ready) return;
            try
            {
                int offset = CachedPtrOffset;
                // 0x10 is what the compiled op_Equality/op_Inequality read; anything else is a different player.
                if (offset != 0x10)
                    throw new NotSupportedException($"UnityEngine.Object.m_CachedPtr at 0x{offset:X}, expected 0x10.");
                if (sizeof(Vector3) != 12 || sizeof(Vector2) != 8 || sizeof(Vector4) != 16 || sizeof(Quaternion) != 16 || sizeof(Rect) != 16 || sizeof(Matrix4x4) != 64
                    || sizeof(Color) != 16)
                    throw new NotSupportedException("Unity interop struct sizes differ from the native layouts.");

                _getLocalPosition = (delegate* unmanaged[Cdecl]<IntPtr, Vector3*, void>)Icall("UnityEngine.Transform::get_localPosition_Injected(UnityEngine.Vector3&)");
                _getLocalScale = (delegate* unmanaged[Cdecl]<IntPtr, Vector3*, void>)Icall("UnityEngine.Transform::get_localScale_Injected(UnityEngine.Vector3&)");
                _getLossyScale = (delegate* unmanaged[Cdecl]<IntPtr, Vector3*, void>)Icall("UnityEngine.Transform::get_lossyScale_Injected(UnityEngine.Vector3&)");
                _setPosition = (delegate* unmanaged[Cdecl]<IntPtr, Vector3*, void>)Icall("UnityEngine.Transform::set_position_Injected(UnityEngine.Vector3&)");
                _setLocalScale = (delegate* unmanaged[Cdecl]<IntPtr, Vector3*, void>)Icall("UnityEngine.Transform::set_localScale_Injected(UnityEngine.Vector3&)");
                _setRotation = (delegate* unmanaged[Cdecl]<IntPtr, Quaternion*, void>)Icall("UnityEngine.Transform::set_rotation_Injected(UnityEngine.Quaternion&)");
                _getLocalToWorld = (delegate* unmanaged[Cdecl]<IntPtr, Matrix4x4*, void>)Icall("UnityEngine.Transform::get_localToWorldMatrix_Injected(UnityEngine.Matrix4x4&)");
                _getWorldToLocal = (delegate* unmanaged[Cdecl]<IntPtr, Matrix4x4*, void>)Icall("UnityEngine.Transform::get_worldToLocalMatrix_Injected(UnityEngine.Matrix4x4&)");
                _transformPoint = (delegate* unmanaged[Cdecl]<IntPtr, Vector3*, Vector3*, void>)Icall("UnityEngine.Transform::TransformPoint_Injected(UnityEngine.Vector3&,UnityEngine.Vector3&)");
                _getProjection = (delegate* unmanaged[Cdecl]<IntPtr, Matrix4x4*, void>)Icall("UnityEngine.Camera::get_projectionMatrix_Injected(UnityEngine.Matrix4x4&)");
                _getWorldToCamera = (delegate* unmanaged[Cdecl]<IntPtr, Matrix4x4*, void>)Icall("UnityEngine.Camera::get_worldToCameraMatrix_Injected(UnityEngine.Matrix4x4&)");
                _getPixelRect = (delegate* unmanaged[Cdecl]<IntPtr, Rect*, void>)Icall("UnityEngine.Camera::get_pixelRect_Injected(UnityEngine.Rect&)");
                _getNearClipPlane = (delegate* unmanaged[Cdecl]<IntPtr, float>)Icall("UnityEngine.Camera::get_nearClipPlane()");
                _worldToScreenPoint = (delegate* unmanaged[Cdecl]<IntPtr, Vector3*, int, Vector3*, void>)Icall(
                    "UnityEngine.Camera::WorldToScreenPoint_Injected(UnityEngine.Vector3&,UnityEngine.Camera/MonoOrStereoscopicEye,UnityEngine.Vector3&)");
                _getSpriteRect = (delegate* unmanaged[Cdecl]<IntPtr, Rect*, void>)Icall("UnityEngine.Sprite::get_rect_Injected(UnityEngine.Rect&)");
                _getSpritePivot = (delegate* unmanaged[Cdecl]<IntPtr, Vector2*, void>)Icall("UnityEngine.Sprite::get_pivot_Injected(UnityEngine.Vector2&)");
                _getPixelsPerUnit = (delegate* unmanaged[Cdecl]<IntPtr, float>)Icall("UnityEngine.Sprite::get_pixelsPerUnit()");
                _getActiveSelf = (delegate* unmanaged[Cdecl]<IntPtr, byte>)Icall("UnityEngine.GameObject::get_activeSelf()");
                _getRendererEnabled = (delegate* unmanaged[Cdecl]<IntPtr, byte>)Icall("UnityEngine.Renderer::get_enabled()");
                _getSortingOrder = (delegate* unmanaged[Cdecl]<IntPtr, int>)Icall("UnityEngine.Renderer::get_sortingOrder()");
                _getSprite = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Icall("UnityEngine.SpriteRenderer::get_sprite()");
                _getSpriteRendererSize = (delegate* unmanaged[Cdecl]<IntPtr, Vector2*, void>)Icall("UnityEngine.SpriteRenderer::get_size_Injected(UnityEngine.Vector2&)");
                _getSpriteRendererColor = (delegate* unmanaged[Cdecl]<IntPtr, Color*, void>)Icall("UnityEngine.SpriteRenderer::get_color_Injected(UnityEngine.Color&)");
                // Material.GetVector(int) 0x182866330 is this icall into a Vector4 (Color and Vector4 share the layout).
                _getMaterialVector = (delegate* unmanaged[Cdecl]<IntPtr, int, Vector4*, void>)Icall("UnityEngine.Material::GetColorImpl_Injected(System.Int32,UnityEngine.Color&)");
                _getTime = (delegate* unmanaged[Cdecl]<float>)Icall("UnityEngine.Time::get_time()");
                _getUnscaledDeltaTime = (delegate* unmanaged[Cdecl]<float>)Icall("UnityEngine.Time::get_unscaledDeltaTime()");
                _getUnscaledTime = (delegate* unmanaged[Cdecl]<float>)Icall("UnityEngine.Time::get_unscaledTime()");
                _getFrameCount = (delegate* unmanaged[Cdecl]<int>)Icall("UnityEngine.Time::get_frameCount()");
                _mouseButtonDown = (delegate* unmanaged[Cdecl]<int, byte>)Icall("UnityEngine.Input::GetMouseButtonDown(System.Int32)");
                _mouseButtonUp = (delegate* unmanaged[Cdecl]<int, byte>)Icall("UnityEngine.Input::GetMouseButtonUp(System.Int32)");
                // Color -> Color32 is compiled C#, not an icall: clamp, *255, a modf-based round-half-even,
                // cvttss2si (0x1801F2960). Call that exact code rather than re-deriving its rounding. It is a
                // leaf (no class init, no allocation, no throw), takes the Color by pointer, returns in eax.
                MethodInfo implicitColor32 = typeof(Color32).GetMethod("op_Implicit", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(Color) }, null);
                FieldInfo implicitField = implicitColor32 == null ? null : Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(implicitColor32);
                _colorToColor32Method = implicitField == null ? IntPtr.Zero : (IntPtr)implicitField.GetValue(null);
                IntPtr implicitPointer = _colorToColor32Method == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_colorToColor32Method;
                if (implicitPointer == IntPtr.Zero || sizeof(Color32) != 4)
                    throw new NotSupportedException("Color32.op_Implicit(Color) has no native binding.");
                _colorToColor32 = (delegate* unmanaged[Cdecl]<Color*, IntPtr, Color32>)implicitPointer;
                _ready = true;
                log.Msg("[ClonZones] Unity icalls bound directly (no per-frame IL2CPP boxing).");
            }
            catch (Exception error) when (error is NotSupportedException || error is EntryPointNotFoundException)
            {
                log.Warning($"[ClonZones] Direct Unity icalls unavailable, using the interop members: {error.Message}");
            }
        }

        private static IntPtr Icall(string signature)
        {
            IntPtr pointer = IL2CPP.il2cpp_resolve_icall(signature);
            if (pointer == IntPtr.Zero) throw new NotSupportedException("Unity icall not found: " + signature);
            return pointer;
        }

        // The runtime's own field offset (what the generated m_CachedPtr accessor reads), resolved
        // once. Independent of the icall bindings so the alive checks never need the boxing ==.
        private static int CachedPtrOffset
        {
            get
            {
                if (_cachedPtr != 0) return _cachedPtr;
                FieldInfo cachedPtrField = typeof(UnityEngine.Object).GetField("NativeFieldInfoPtr_m_CachedPtr", BindingFlags.Static | BindingFlags.NonPublic);
                IntPtr info = cachedPtrField == null ? IntPtr.Zero : (IntPtr)cachedPtrField.GetValue(null);
                if (info == IntPtr.Zero) throw new NotSupportedException("UnityEngine.Object.m_CachedPtr field info is missing.");
                return _cachedPtr = (int)IL2CPP.il2cpp_field_get_offset(info);
            }
        }

        /// <summary>Unity's (o != null): a live managed wrapper whose native object still exists.</summary>
        public static bool Alive(UnityEngine.Object o) => o is not null && AlivePtr(o.Pointer);

        /// <summary>Same test on a raw IL2CPP object pointer (e.g. from SpritePtr).</summary>
        public static bool AlivePtr(IntPtr o) => o != IntPtr.Zero && *(IntPtr*)((byte*)o + CachedPtrOffset) != IntPtr.Zero;

        /// <summary>
        /// Unity's a == b (Object.op_Equality 0x18287FD30): both null are equal, a null side equals a
        /// destroyed object, otherwise object identity.
        /// </summary>
        public static bool Same(UnityEngine.Object a, UnityEngine.Object b)
        {
            if (a is null) return b is null || !Alive(b);
            if (b is null) return !Alive(a);
            return a.Pointer == b.Pointer;
        }

        // The pointer an icall may be given: the interop getters throw on a managed null too.
        private static IntPtr Self(UnityEngine.Object o)
        {
            IntPtr pointer = o.Pointer;
            if (*(IntPtr*)((byte*)pointer + _cachedPtr) == IntPtr.Zero)
                throw new NullReferenceException($"{o.GetType().Name} has been destroyed.");
            return pointer;
        }

        private static IntPtr SelfPtr(IntPtr pointer)
        {
            if (!AlivePtr(pointer)) throw new NullReferenceException("Unity object has been destroyed.");
            return pointer;
        }

        public static Vector3 LocalPosition(Transform t)
        {
            if (!_ready) return t.localPosition;
            Vector3 r; _getLocalPosition(Self(t), &r); return r;
        }

        public static Vector3 LocalScale(Transform t)
        {
            if (!_ready) return t.localScale;
            Vector3 r; _getLocalScale(Self(t), &r); return r;
        }

        public static Vector3 LossyScale(Transform t)
        {
            if (!_ready) return t.lossyScale;
            Vector3 r; _getLossyScale(Self(t), &r); return r;
        }

        public static Matrix4x4 LocalToWorld(Transform t)
        {
            if (!_ready) return t.localToWorldMatrix;
            Matrix4x4 r; _getLocalToWorld(Self(t), &r); return r;
        }

        public static Matrix4x4 WorldToLocal(Transform t)
        {
            if (!_ready) return t.worldToLocalMatrix;
            Matrix4x4 r; _getWorldToLocal(Self(t), &r); return r;
        }

        public static Vector3 TransformPoint(Transform t, Vector3 position)
        {
            if (!_ready) return t.TransformPoint(position);
            Vector3 r; _transformPoint(Self(t), &position, &r); return r;
        }

        public static void SetPosition(Transform t, Vector3 value)
        {
            if (!_ready) { t.position = value; return; }
            _setPosition(Self(t), &value);
        }

        public static void SetLocalScale(Transform t, Vector3 value)
        {
            if (!_ready) { t.localScale = value; return; }
            _setLocalScale(Self(t), &value);
        }

        public static void SetRotation(Transform t, Quaternion value)
        {
            if (!_ready) { t.rotation = value; return; }
            _setRotation(Self(t), &value);
        }

        public static bool ActiveSelf(GameObject o) => _ready ? _getActiveSelf(Self(o)) != 0 : o.activeSelf;

        public static bool Enabled(Renderer r) => _ready ? _getRendererEnabled(Self(r)) != 0 : r.enabled;

        public static int SortingOrder(Renderer r) => _ready ? _getSortingOrder(Self(r)) : r.sortingOrder;

        /// <summary>SpriteRenderer.sprite as a raw IL2CPP pointer (zero when unset): no wrapper, no pool lookup.</summary>
        public static IntPtr SpritePtr(SpriteRenderer r)
        {
            if (!_ready) { Sprite s = r.sprite; return s is null ? IntPtr.Zero : s.Pointer; }
            return _getSprite(Self(r));
        }

        public static Vector2 SpritePivot(IntPtr sprite)
        {
            if (!_ready) return Il2CppObjectPool.Get<Sprite>(SelfPtr(sprite)).pivot;
            Vector2 r; _getSpritePivot(SelfPtr(sprite), &r); return r;
        }

        public static Rect SpriteRect(IntPtr sprite)
        {
            if (!_ready) return Il2CppObjectPool.Get<Sprite>(SelfPtr(sprite)).rect;
            Rect r; _getSpriteRect(SelfPtr(sprite), &r); return r;
        }

        public static float SpritePixelsPerUnit(IntPtr sprite) =>
            _ready ? _getPixelsPerUnit(SelfPtr(sprite)) : Il2CppObjectPool.Get<Sprite>(SelfPtr(sprite)).pixelsPerUnit;

        public static Vector2 SpriteRendererSize(SpriteRenderer s)
        {
            if (!_ready) return s.size;
            Vector2 r; _getSpriteRendererSize(Self(s), &r); return r;
        }

        public static Color SpriteRendererColor(SpriteRenderer s)
        {
            if (!_ready) return s.color;
            Color r; _getSpriteRendererColor(Self(s), &r); return r;
        }

        public static Matrix4x4 ProjectionMatrix(Camera c)
        {
            if (!_ready) return c.projectionMatrix;
            Matrix4x4 r; _getProjection(Self(c), &r); return r;
        }

        public static Matrix4x4 WorldToCameraMatrix(Camera c)
        {
            if (!_ready) return c.worldToCameraMatrix;
            Matrix4x4 r; _getWorldToCamera(Self(c), &r); return r;
        }

        public static Rect PixelRect(Camera c)
        {
            if (!_ready) return c.pixelRect;
            Rect r; _getPixelRect(Self(c), &r); return r;
        }

        public static float NearClipPlane(Camera c) => _ready ? _getNearClipPlane(Self(c)) : c.nearClipPlane;

        public static Vector3 WorldToScreenPoint(Camera c, Vector3 position)
        {
            if (!_ready) return c.WorldToScreenPoint(position);
            Vector3 r; _worldToScreenPoint(Self(c), &position, EyeMono, &r); return r;
        }

        public static Vector4 GetVector(Material m, int nameId)
        {
            if (!_ready) return m.GetVector(nameId);
            Vector4 r; _getMaterialVector(Self(m), nameId, &r); return r;
        }

        public static float Time => _ready ? _getTime() : UnityEngine.Time.time;
        public static float UnscaledDeltaTime => _ready ? _getUnscaledDeltaTime() : UnityEngine.Time.unscaledDeltaTime;
        public static float UnscaledTime => _ready ? _getUnscaledTime() : UnityEngine.Time.unscaledTime;
        public static int FrameCount => _ready ? _getFrameCount() : UnityEngine.Time.frameCount;

        public static bool MouseButtonDown(int button) => _ready ? _mouseButtonDown(button) != 0 : Input.GetMouseButtonDown(button);
        public static bool MouseButtonUp(int button) => _ready ? _mouseButtonUp(button) != 0 : Input.GetMouseButtonUp(button);

        /// <summary>(Color32)color through the compiled conversion, without runtime_invoke boxing the result.</summary>
        public static Color32 ToColor32(Color color) => _ready ? _colorToColor32(&color, _colorToColor32Method) : color;

        /// <summary>
        /// Matrix4x4 == as the interop's restored operator computes it: four GetColumn pairs, each
        /// through Vector4 == (0x18288F590): ((dy*dy + dx*dx) + dz*dz) + dw*dw &lt; epsilon, NaN false.
        /// </summary>
        public static bool MatrixEquals(in Matrix4x4 a, in Matrix4x4 b) =>
            ColumnEquals(a.m00 - b.m00, a.m10 - b.m10, a.m20 - b.m20, a.m30 - b.m30) &&
            ColumnEquals(a.m01 - b.m01, a.m11 - b.m11, a.m21 - b.m21, a.m31 - b.m31) &&
            ColumnEquals(a.m02 - b.m02, a.m12 - b.m12, a.m22 - b.m22, a.m32 - b.m32) &&
            ColumnEquals(a.m03 - b.m03, a.m13 - b.m13, a.m23 - b.m23, a.m33 - b.m33);

        private static bool ColumnEquals(float dx, float dy, float dz, float dw) => ((dy * dy + dx * dx) + dz * dz) + dw * dw < VectorEpsilonSquared;

        /// <summary>Rect == (0x18286DC20): exact x, y, width, height equality, NaN unequal.</summary>
        public static bool RectEquals(in Rect a, in Rect b) =>
            a.m_XMin == b.m_XMin && a.m_YMin == b.m_YMin && a.m_Width == b.m_Width && a.m_Height == b.m_Height;

        /// <summary>Vector2 == (0x18288DC40): (dy*dy + dx*dx) &lt; epsilon, NaN false.</summary>
        public static bool Vector2Equals(in Vector2 a, in Vector2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return dy * dy + dx * dx < VectorEpsilonSquared;
        }

        /// <summary>Color == (0x18285CD20): the Vector4 test on r, g, b, a.</summary>
        public static bool ColorEquals(in Color a, in Color b) => ColumnEquals(a.r - b.r, a.g - b.g, a.b - b.b, a.a - b.a);

        /// <summary>
        /// Matrix4x4.MultiplyPoint3x4 (0x182867760), same operations in the same order:
        /// ((m*1 * v.y + m*0 * v.x) + m*2 * v.z) + m*3 per row. The interop sends this through
        /// runtime_invoke and boxes the Vector3.
        /// </summary>
        public static Vector3 MultiplyPoint3x4(in Matrix4x4 m, in Vector3 v)
        {
            Vector3 r = default;
            r.x = ((m.m01 * v.y + m.m00 * v.x) + m.m02 * v.z) + m.m03;
            r.y = ((m.m11 * v.y + m.m10 * v.x) + m.m12 * v.z) + m.m13;
            r.z = ((m.m21 * v.y + m.m20 * v.x) + m.m22 * v.z) + m.m23;
            return r;
        }
    }
}
