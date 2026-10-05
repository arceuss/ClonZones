using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    internal sealed class Gh3NoteProjection : IDisposable
    {
        private static readonly int FadeParamsId = Shader.PropertyToID("_FadeParams");
        // Vector3.one is a boxing invoke in the interop.
        private static readonly Vector3 One = new Vector3 { x = 1f, y = 1f, z = 1f };
        private static Gh3NoteProjection _current;
        private readonly Dictionary<IntPtr, Slot> _byContainer = new();
        private readonly List<FadeMaterial> _fadeMaterials = new();
        private readonly GameManager _manager;
        private readonly GuitarNoteRenderer _notes;
        private readonly Gh3HighwayBridge _bridge;
        private readonly Slot[] _slots;
        private readonly MelonLogger.Instance _log;
        private bool _logged;

        private sealed class Slot
        {
            public Transform Root, Head, Parent;
            public SpriteRenderer Renderer;
            public GameObject Object;
            public Vector3 OriginalPosition, OriginalScale;
            public Quaternion OriginalRotation;
            public bool Changed;
            public double HitTime;
            public float OriginalRootDepthScale;
            public bool Captured;
            public FadeMaterial Fade;
        }

        // CH's head material ("CloneHero/Sprite Track FadeIn", TRACKFADE_ON) already fades every
        // pixel with smoothstep against _FadeParams, but CH fills those from its own track depth,
        // which is not where the projected highway ends. Projected heads draw with one owned copy
        // per native material whose far band is the projected one; CH never rewrites head
        // materials after its first fade registration (IDA: set_sharedMaterial callers).
        private sealed class FadeMaterial
        {
            public Material Native, Owned;
            public Vector4 Applied;
        }

        public Gh3NoteProjection(GuitarNoteRenderer notes, Gh3HighwayBridge bridge, MelonLogger.Instance log)
        {
            _notes = notes; _bridge = bridge; _log = log;
            _manager = notes.basePlayer.gameManager;
            var pool = notes.field_Protected_Il2CppArrayBase_1_TNoteContainer_0;
            _slots = new Slot[pool.Length];
            for (int i = 0; i < _slots.Length; i++)
            {
                GuitarNoteContainer container = pool[i];
                Transform head = container.Head.transform;
                _slots[i] = new Slot { Root = container.transform, Head = head, Parent = head.parent, Renderer = container.Head, Object = container.gameObject,
                    OriginalPosition = head.localPosition, OriginalRotation = head.localRotation, OriginalScale = head.localScale };
                _byContainer.Add(container.Pointer, _slots[i]);
            }
            _current = this;
        }

        public static void Capture(IntPtr container, double hitTime)
        {
            if (_current != null && _current._byContainer.TryGetValue(container, out Slot slot))
            {
                slot.HitTime = hitTime;
                slot.Captured = true;
            }
        }

        public void Update()
        {
            int count = _slots.Length;
            Vector2 pixel = _bridge.PixelWorldSize;
            double now = _manager.songTimeVideoTime;
            float farEnd = _bridge.ScreenUvY(_bridge.TopY);
            float farStart = _bridge.ScreenUvY(_bridge.TopY + Gh3HighwayLayout.Fade);
            for (int i = 0; i < _fadeMaterials.Count; i++) Sync(_fadeMaterials[i], farEnd, farStart);
            for (int i = 0; i < count; i++)
            {
                Slot slot = _slots[i];
                if (!slot.Captured || !UnityIcalls.ActiveSelf(slot.Object) || !UnityIcalls.Enabled(slot.Renderer)) continue;
                IntPtr sprite = UnityIcalls.SpritePtr(slot.Renderer);
                if (!UnityIcalls.AlivePtr(sprite) || !NoteHeadSpriteBank.TryGetMetrics(sprite, out var metrics))
                {
                    Restore(slot);
                    continue;
                }
                Vector3 original = UnityIcalls.LocalPosition(slot.Root);
                Vector3 rootScale = UnityIcalls.LocalScale(slot.Root);
                if (!slot.Changed) slot.OriginalRootDepthScale = rootScale.z;
                // CH flattens the container to z=0. A child cannot be moved to
                // the GH3 camera plane through that singular parent transform.
                rootScale.z = 1f;
                // keep this before the parent's lossyScale read below, the parent can sit under the root.
                UnityIcalls.SetLocalScale(slot.Root, rootScale);
                float y = _bridge.TimeY(slot.HitTime - now);
                float scale = Gh3HighwayLayout.WidthAtY(y) / (5f * 128f);
                Vector3 parent = UnityIcalls.Alive(slot.Parent) ? UnityIcalls.LossyScale(slot.Parent) : One;
                Vector3 target = _bridge.World(_bridge.FieldX(original.x, y), y);
                UnityIcalls.SetPosition(slot.Head, target);
                UnityIcalls.SetRotation(slot.Head, _bridge.Rotation);
                UnityIcalls.SetLocalScale(slot.Head, new Vector3 { x = metrics.ScaleX * scale * pixel.x / parent.x,
                    y = metrics.ScaleY * scale * pixel.y / parent.y, z = 1f });
                slot.Changed = true;
                // The fade is spatial and lives in the material; renderer color stays CH's.
                if (slot.Fade == null) Bind(slot, farEnd, farStart);
                if (!_logged)
                {
                    _logged = true;
                    _log.Msg($"[ClonZones] GH3 note projection: pool={_slots.Length}, rowY={y}, actualY={_bridge.VirtualY(slot.Head.position)}, rootDepthScale={rootScale.z}, pivot={UnityIcalls.SpritePivot(sprite).y / UnityIcalls.SpriteRect(sprite).m_Height}.");
                }
            }
        }

        private void Bind(Slot slot, float farEnd, float farStart)
        {
            Material native = slot.Renderer.sharedMaterial;
            if (native == null) return;
            IntPtr pointer = native.Pointer;
            FadeMaterial fade = null;
            for (int i = 0; i < _fadeMaterials.Count; i++)
                if (_fadeMaterials[i].Native.Pointer == pointer || _fadeMaterials[i].Owned.Pointer == pointer) { fade = _fadeMaterials[i]; break; }
            if (fade == null)
            {
                // Any zero component makes the shader fall back to its built-in world-depth
                // band, so wait until CH has written its computed params (it does every frame
                // once its fade registration ran) instead of copying an unset vector.
                Vector4 nativeParams = native.GetVector(FadeParamsId);
                if (nativeParams.x == 0f || nativeParams.y == 0f) return;
                ClonZonesBenchmark.Mark(BenchmarkEvent.FadeMaterial);
                fade = new FadeMaterial { Native = native, Owned = new Material(native) };
                fade.Owned.name = native.name + " (ClonZones GH3 fade)";
                fade.Owned.renderQueue = native.renderQueue;
                fade.Owned.hideFlags = HideFlags.DontUnloadUnusedAsset;
                fade.Applied.x = float.NaN;
                _fadeMaterials.Add(fade);
                Sync(fade, farEnd, farStart);
                _log.Msg($"[ClonZones] GH3 note fade: {native.name} ({native.shader.name}, queue {native.renderQueue}) far band {farEnd:R}..{farStart:R}, native {nativeParams}.");
            }
            if (pointer != fade.Owned.Pointer) slot.Renderer.sharedMaterial = fade.Owned;
            slot.Fade = fade;
        }

        private static void Sync(FadeMaterial fade, float farEnd, float farStart)
        {
            // x/y are CH's close band (notes past the strikeline); only the far band moves.
            Vector4 native = UnityIcalls.GetVector(fade.Native, FadeParamsId);
            Vector4 applied = fade.Applied;
            if (native.x == applied.x && native.y == applied.y && farEnd == applied.z && farStart == applied.w) return;
            applied.x = native.x; applied.y = native.y; applied.z = farEnd; applied.w = farStart;
            fade.Owned.SetVector(FadeParamsId, applied);
            fade.Applied = applied;
        }

        private static void Restore(Slot slot)
        {
            if (!slot.Changed || slot.Head == null) return;
            Vector3 rootScale = slot.Root.localScale;
            rootScale.z = slot.OriginalRootDepthScale;
            slot.Root.localScale = rootScale;
            slot.Head.localPosition = slot.OriginalPosition;
            slot.Head.localRotation = slot.OriginalRotation;
            slot.Head.localScale = slot.OriginalScale;
            if (slot.Fade != null)
            {
                Material current = slot.Renderer.sharedMaterial;
                if (current != null && current.Pointer == slot.Fade.Owned.Pointer)
                    slot.Renderer.sharedMaterial = slot.Fade.Native;
                slot.Fade = null;
            }
            slot.Changed = false;
        }

        public void Dispose()
        {
            if (ReferenceEquals(_current, this)) _current = null;
            foreach (Slot slot in _slots) Restore(slot);
            foreach (FadeMaterial fade in _fadeMaterials) UnityEngine.Object.Destroy(fade.Owned);
            _fadeMaterials.Clear();
        }
    }
}
