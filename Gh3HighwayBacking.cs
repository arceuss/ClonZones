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
                Sprite sprite = slot.Source.sprite;
                if (sprite == null || !slot.Source.enabled)
                {
                    slot.Mesh?.Disable();
                    if (slot.Hidden) { slot.Source.forceRenderingOff = slot.WasOff; slot.Hidden = false; }
                    continue;
                }
                bool changed = projectionChanged || slot.Sprite != sprite;
                if (slot.Mesh == null)
                {
                    Material material = slot.Source.sharedMaterial;
                    slot.Mesh = new Gh3HighwayMesh("clonzones_gh3_backing", sprite.texture, material.shader, null,
                        slot.Source.gameObject.layer, slot.Source.sortingLayerID, slot.Source.sortingOrder,
                        material.renderQueue, 144, false, material);
                    _log.Msg($"[ClonZones] Highway backing projection: owner={slot.Source.name}, drawMode={slot.Source.drawMode}, size={slot.Source.size}, sprite={sprite.rect}, ppu={sprite.pixelsPerUnit}, shader={material.shader.name}.");
                    changed = true;
                }
                if (slot.Sprite != sprite)
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
                Matrix4x4 matrix = slot.Transform.worldToLocalMatrix;
                Vector2 size = slot.Source.size;
                Color color = slot.Source.color;
                if (matrix != slot.Matrix || size != slot.Size || color != slot.Color) changed = true;
                slot.Matrix = matrix; slot.Size = size; slot.Color = color;
                if (changed) Draw(slot);
                // CH still owns selected art, video frames, tint and UV scrolling.
                slot.Source.GetPropertyBlock(slot.Block);
                if (slot.Block.GetTexture(MainTexture) == null) slot.Block.SetTexture(MainTexture, sprite.texture);
                slot.Mesh.SetPropertyBlock(slot.Block);
                if (!slot.Hidden) { slot.Source.forceRenderingOff = true; slot.Hidden = true; }
            }
        }

        private void Draw(Slot slot)
        {
            slot.Mesh.Begin();
            float near = (float)_notes.noteZPosCloseLimit, far = (float)_notes.noteZPosFarLimit;
            Matrix4x4 track = _player.transform.localToWorldMatrix;
            float height = slot.Source.drawMode == SpriteDrawMode.Sliced ? slot.Size.y : slot.Height;
            float halfWidth = _bridge.HalfWidth;
            for (int row = 0; row < 144; row++)
            {
                float z0 = near + (far-near)*row/144f, z1 = near + (far-near)*(row+1)/144f;
                float local0 = slot.Matrix.MultiplyPoint3x4(track.MultiplyPoint3x4(new Vector3(0f,0f,z0))).y;
                float local1 = slot.Matrix.MultiplyPoint3x4(track.MultiplyPoint3x4(new Vector3(0f,0f,z1))).y;
                float v0 = slot.V0 + (slot.PivotY + local0/height)*(slot.V1-slot.V0);
                float v1 = slot.V0 + (slot.PivotY + local1/height)*(slot.V1-slot.V0);
                Color c0 = slot.Color, c1 = c0;
                c0.a *= Gh3HighwayLayout.AlphaAtY(_bridge.DepthY(z0));
                c1.a *= Gh3HighwayLayout.AlphaAtY(_bridge.DepthY(z1));
                slot.Mesh.WorldQuad(_bridge.FieldPoint(-halfWidth,z0),_bridge.FieldPoint(halfWidth,z0),
                    _bridge.FieldPoint(halfWidth,z1),_bridge.FieldPoint(-halfWidth,z1),v0,v1,c0,c1,slot.U0,slot.U1);
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
