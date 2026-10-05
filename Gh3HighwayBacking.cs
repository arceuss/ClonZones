using System;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    internal sealed class Gh3HighwayBacking : IDisposable
    {
        private static readonly int MainTexture = Shader.PropertyToID("_MainTex");
        private readonly BasePlayer _player;
        private readonly GuitarNoteRenderer _notes;
        private readonly Gh3HighwayBridge _bridge;
        private readonly Slot[] _slots;
        private readonly MelonLogger.Instance _log;

        private sealed class Slot
        {
            public SpriteRenderer Source;
            public Transform Transform;
            public MaterialPropertyBlock Block;
            public Gh3HighwayMesh Mesh;
            public Sprite Sprite;
            // rooted so the per-frame GetTexture lookup reuses one pooled wrapper.
            public Texture BlockTexture;
            public bool WasOff, Hidden;
            public Matrix4x4 Matrix;
            public Vector2 Size;
            public Color Color;
            public float U0, U1, V0, V1, PivotY, Height;
        }

        public Gh3HighwayBacking(BasePlayer player, GuitarNoteRenderer notes, Gh3HighwayBridge bridge, MelonLogger.Instance log)
        {
            _player = player; _notes = notes; _bridge = bridge; _log = log;
            var owners = player.GetComponentsInChildren<HighwayScroll>(true);
            _slots = new Slot[owners.Length];
            for (int i = 0; i < owners.Length; i++)
            {
                SpriteRenderer source = owners[i].GetComponent<SpriteRenderer>();
                _slots[i] = new Slot { Source = source, Transform = source.transform,
                    Block = new MaterialPropertyBlock(), WasOff = source.forceRenderingOff };
            }
        }

        public void Update(bool projectionChanged)
        {
            foreach (Slot slot in _slots)
            {
                IntPtr spritePointer = UnityIcalls.SpritePtr(slot.Source);
                if (!UnityIcalls.AlivePtr(spritePointer) || !UnityIcalls.Enabled(slot.Source))
                {
                    slot.Mesh?.Disable();
                    if (slot.Hidden) { slot.Source.forceRenderingOff = slot.WasOff; slot.Hidden = false; }
                    continue;
                }
                // slot.Sprite != sprite with sprite alive: a different (or no) previous sprite.
                bool spriteChanged = slot.Sprite is null || slot.Sprite.Pointer != spritePointer;
                Sprite sprite = spriteChanged ? slot.Source.sprite : slot.Sprite;
                bool changed = projectionChanged || spriteChanged;
                if (slot.Mesh == null)
                {
                    Material material = slot.Source.sharedMaterial;
                    slot.Mesh = new Gh3HighwayMesh("clonzones_gh3_backing", sprite.texture, material.shader, null,
                        slot.Source.gameObject.layer, slot.Source.sortingLayerID, slot.Source.sortingOrder,
                        material.renderQueue, 144, false, material);
                    _log.Msg($"[ClonZones] Highway backing projection: owner={slot.Source.name}, drawMode={slot.Source.drawMode}, size={slot.Source.size}, sprite={sprite.rect}, ppu={sprite.pixelsPerUnit}, shader={material.shader.name}.");
                    changed = true;
                }
                if (spriteChanged)
                {
                    slot.Sprite = sprite;
                    var uv = sprite.uv;
                    slot.U0 = slot.U1 = uv[0].x; slot.V0 = slot.V1 = uv[0].y;
                    for (int i = 1; i < uv.Length; i++)
                    {
                        slot.U0 = MathF.Min(slot.U0, uv[i].x); slot.U1 = MathF.Max(slot.U1, uv[i].x);
                        slot.V0 = MathF.Min(slot.V0, uv[i].y); slot.V1 = MathF.Max(slot.V1, uv[i].y);
                    }
                    slot.PivotY = sprite.pivot.y / sprite.rect.height;
                    slot.Height = sprite.rect.height / sprite.pixelsPerUnit;
                }
                Matrix4x4 matrix = UnityIcalls.WorldToLocal(slot.Transform);
                Vector2 size = UnityIcalls.SpriteRendererSize(slot.Source);
                Color color = UnityIcalls.SpriteRendererColor(slot.Source);
                if (!UnityIcalls.MatrixEquals(matrix, slot.Matrix) || !UnityIcalls.Vector2Equals(size, slot.Size)
                    || !UnityIcalls.ColorEquals(color, slot.Color)) changed = true;
                slot.Matrix = matrix; slot.Size = size; slot.Color = color;
                if (changed) Draw(slot);
                // CH still owns selected art, video frames, tint and UV scrolling.
                slot.Source.GetPropertyBlock(slot.Block);
                slot.BlockTexture = slot.Block.GetTexture(MainTexture);
                if (!UnityIcalls.Alive(slot.BlockTexture)) slot.Block.SetTexture(MainTexture, sprite.texture);
                slot.Mesh.SetPropertyBlock(slot.Block);
                if (!slot.Hidden) { slot.Source.forceRenderingOff = true; slot.Hidden = true; }
            }
        }

        private void Draw(Slot slot)
        {
            slot.Mesh.Begin();
            float near = (float)_notes.noteZPosCloseLimit, far = (float)_notes.noteZPosFarLimit;
            Matrix4x4 track = UnityIcalls.LocalToWorld(_player.transform);
            float height = slot.Source.drawMode == SpriteDrawMode.Sliced ? slot.Size.y : slot.Height;
            float halfWidth = _bridge.HalfWidth;
            for (int row = 0; row < 144; row++)
            {
                float z0 = near + (far-near)*row/144f, z1 = near + (far-near)*(row+1)/144f;
                Vector3 point0 = default, point1 = default;
                point0.z = z0; point1.z = z1;
                float local0 = UnityIcalls.MultiplyPoint3x4(slot.Matrix, UnityIcalls.MultiplyPoint3x4(track, point0)).y;
                float local1 = UnityIcalls.MultiplyPoint3x4(slot.Matrix, UnityIcalls.MultiplyPoint3x4(track, point1)).y;
                float v0 = slot.V0 + (slot.PivotY + local0/height)*(slot.V1-slot.V0);
                float v1 = slot.V0 + (slot.PivotY + local1/height)*(slot.V1-slot.V0);
                Color c0 = slot.Color, c1 = c0;
                c0.a *= _bridge.AlphaAtY(_bridge.DepthY(z0));
                c1.a *= _bridge.AlphaAtY(_bridge.DepthY(z1));
                slot.Mesh.WorldQuad(_bridge.FieldPoint(-halfWidth,z0),_bridge.FieldPoint(halfWidth,z0),
                    _bridge.FieldPoint(halfWidth,z1),_bridge.FieldPoint(-halfWidth,z1),v0,v1,UnityIcalls.ToColor32(c0),UnityIcalls.ToColor32(c1),slot.U0,slot.U1);
            }
            slot.Mesh.Upload();
        }

        public void Dispose()
        {
            foreach (Slot slot in _slots)
            {
                if (slot.Source != null) slot.Source.forceRenderingOff = slot.WasOff;
                slot.Mesh?.Dispose();
            }
        }
    }
}
