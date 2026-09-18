using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    internal sealed class Gh3NoteProjection : IDisposable
    {
        private static Gh3NoteProjection _current;
        private readonly Dictionary<IntPtr, Slot> _byContainer = new();
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
            for (int i = 0; i < count; i++)
            {
                Slot slot = _slots[i];
                if (!slot.Captured || !slot.Object.activeSelf || !slot.Renderer.enabled) continue;
                Sprite sprite = slot.Renderer.sprite;
                if (sprite == null || !NoteHeadSpriteBank.TryGetMetrics(sprite.Pointer, out var metrics))
                {
                    Restore(slot);
                    continue;
                }
                Vector3 original = slot.Root.localPosition;
                Vector3 rootScale = slot.Root.localScale;
                if (!slot.Changed) slot.OriginalRootDepthScale = rootScale.z;
                // CH flattens the container to z=0. A child cannot be moved to
                // the GH3 camera plane through that singular parent transform.
                rootScale.z = 1f;
                slot.Root.localScale = rootScale;
                float y = _bridge.TimeY(slot.HitTime - now);
                float scale = Gh3HighwayLayout.WidthAtY(y) / (5f * 128f);
                Vector3 parent = slot.Parent != null ? slot.Parent.lossyScale : Vector3.one;
                Vector3 target = _bridge.World(_bridge.FieldX(original.x, y), y);
                slot.Head.position = target;
                slot.Head.rotation = _bridge.Rotation;
                slot.Head.localScale = new Vector3(metrics.ScaleX * scale * pixel.x / parent.x,
                    metrics.ScaleY * scale * pixel.y / parent.y, 1f);
                slot.Changed = true;
                if (!_logged)
                {
                    _logged = true;
                    _log.Msg($"[ClonZones] GH3 note projection: pool={_slots.Length}, rowY={y}, actualY={_bridge.VirtualY(slot.Head.position)}, rootDepthScale={rootScale.z}, pivot={sprite.pivot.y / sprite.rect.height}.");
                }
            }
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
            slot.Changed = false;
        }

        public void Dispose()
        {
            if (ReferenceEquals(_current, this)) _current = null;
            foreach (Slot slot in _slots) Restore(slot);
        }
    }
}
