using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    internal sealed class Gh3SustainPatch : IDisposable
    {
        private static readonly Dictionary<IntPtr, Gh3SustainPatch> Players = new();
        private static MelonLogger.Instance _log;
        private static bool _active;
        private static readonly int LengthId = Shader.PropertyToID("_SustainLength");
        private static readonly int WhammyId = Shader.PropertyToID("_uSustainProperties");
        private readonly GuitarNoteRenderer _notes;
        private readonly Transform _track;
        private readonly Gh3HighwayBridge _bridge;
        private readonly Gh3WhammyHistory _whammy = new Gh3WhammyHistory();
        private readonly Slot[] _closed, _open;
        private readonly Gh3HighwayMesh[] _bodyMeshes = new Gh3HighwayMesh[Gh3SustainBank.Count];
        private readonly Gh3HighwayMesh[] _glowMeshes = new Gh3HighwayMesh[Gh3SustainBank.Count];
        private readonly float[] _lanes = new float[5];
        private readonly float _halfWidth;
        private readonly IntPtr _deadSprite;
        private Matrix4x4 _trackMatrix;
        private bool _disposed;
        private bool _sampleLogged;
        private int _spriteSamples;
        private IntPtr _sampledSprite;

        private sealed class Slot
        {
            public SpriteRenderer Body, Glow;
            public Transform Root;
            public GameObject Object;
            public MaterialPropertyBlock Block;
            public bool BodyWasOff, GlowWasOff, BodyHidden, GlowHidden, ReplaceBody, ReplaceGlow;
        }

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            _log = log;
            harmony.Patch(AccessTools.Method(typeof(GuitarNoteRenderer), nameof(GuitarNoteRenderer.Start)),
                postfix: new HarmonyMethod(typeof(Gh3SustainPatch), nameof(Started)));
        }

        private static void Started(GuitarNoteRenderer __instance)
        {
            GuitarNoteRenderer notes = __instance;
            if (Players.ContainsKey(notes.Pointer)) return;
            Shader solid = Shader.Find("Sprites/Default");
            Shader additive = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive");
            if (solid == null || additive == null)
            {
                _log.Warning("[ClonZones] Required sustain mesh shaders unavailable; native ribbons retained.");
                return;
            }
            Players.Add(notes.Pointer, new Gh3SustainPatch(notes, solid, additive));
        }

        private Gh3SustainPatch(GuitarNoteRenderer notes, Shader solid, Shader additive)
        {
            _notes = notes;
            BasePlayer player = notes.GetComponent<BasePlayer>();
            _track = player.transform;
            _bridge = new Gh3HighwayBridge(player.mainCamera, _track, notes);
            _bridge.Refresh();
            var frets = player.neckController.FretAnimators;
            for (int i = 0; i < _lanes.Length; i++) _lanes[i] = _track.InverseTransformPoint(frets[i].transform.position).x;
            _halfWidth = MathF.Abs(_lanes[4] - _lanes[0]) * 5f / 8f / 16f;
            _deadSprite = notes.Sustains[2].Pointer;
            _closed = Cache(notes.field_Private_Il2CppReferenceArray_1_SpriteRenderer_0,
                notes.field_Private_Il2CppReferenceArray_1_SpriteRenderer_1, notes.field_Private_Il2CppReferenceArray_1_Transform_0);
            _open = Cache(notes.field_Private_Il2CppReferenceArray_1_SpriteRenderer_2,
                notes.field_Private_Il2CppReferenceArray_1_SpriteRenderer_3, notes.field_Private_Il2CppReferenceArray_1_Transform_1);
            for (int i = 0; i < _bodyMeshes.Length; i++)
            {
                SpriteRenderer body = i >= Gh3SustainBank.Open ? _open[0].Body : _closed[0].Body;
                SpriteRenderer glow = i >= Gh3SustainBank.Open ? _open[0].Glow : _closed[0].Glow;
                if (Gh3SustainBank.Body[i] != null)
                    _bodyMeshes[i] = CreateMesh("body", i, Gh3SustainBank.Body[i], solid, body);
                if (Gh3SustainBank.Glow[i] != null && i != Gh3SustainBank.Dead && i != Gh3SustainBank.OpenDead)
                {
                    _glowMeshes[i] = CreateMesh("glow", i, Gh3SustainBank.Glow[i], additive, glow);
                    _glowMeshes[i].SetAdditiveTint();
                }
            }
            _log.Msg($"[ClonZones] GH3 ribbon meshes attached: pools={_closed.Length}/{_open.Length}, glow={additive.name}, native whammy history enabled.");
        }

        private Gh3HighwayMesh CreateMesh(string kind, int variant, Texture texture, Shader shader, SpriteRenderer native)
        {
            return new Gh3HighwayMesh($"clonzones_gh3_sustain_{kind}_{variant}", texture, shader, null,
                native.gameObject.layer, native.sortingLayerID, native.sortingOrder, native.sharedMaterial.renderQueue, 32, false);
        }

        private static Slot[] Cache(Il2CppReferenceArray<SpriteRenderer> bodies, Il2CppReferenceArray<SpriteRenderer> glows,
            Il2CppReferenceArray<Transform> roots)
        {
            Slot[] slots = new Slot[bodies.Length];
            for (int i = 0; i < slots.Length; i++)
                slots[i] = new Slot { Body = bodies[i], Glow = glows[i], Root = roots[i], Object = roots[i].gameObject,
                    Block = new MaterialPropertyBlock(), BodyWasOff = bodies[i].forceRenderingOff, GlowWasOff = glows[i].forceRenderingOff };
            return slots;
        }

        public static void SetActive(bool active) { _active = active; }
        public static void Tick()
        {
            if (!_active) return;
            foreach (Gh3SustainPatch player in Players.Values)
            {
                try { player.Update(); }
                catch (Exception error)
                {
                    player.Dispose();
                    _log.Error($"[ClonZones] Sustain replacement stopped; native ribbons restored: {error}");
                }
            }
        }

        private void Update()
        {
            if (_disposed || _notes == null) return;
            _bridge.Refresh();
            _trackMatrix = _track.localToWorldMatrix;
            _whammy.Refresh();
            for (int i = 0; i < _bodyMeshes.Length; i++) { _bodyMeshes[i]?.Begin(); _glowMeshes[i]?.Begin(); }
            Color32 sp = _notes.field_Protected_Color32_2;
            Append(_closed, _notes.field_Private_Int32_3, false, sp);
            Append(_open, _notes.field_Private_Int32_4, true, sp);
            // Hide only after every mesh upload succeeds. No native sprite, material,
            // sliced geometry, shader uniform or pool-enabled state is rewritten.
            for (int i = 0; i < _bodyMeshes.Length; i++) { _bodyMeshes[i]?.Upload(); _glowMeshes[i]?.Upload(); }
            Suppress(_closed); Suppress(_open);
        }

        private void Append(Slot[] slots, int count, bool open, Color32 sp)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                Slot slot = slots[i]; slot.ReplaceBody = false; slot.ReplaceGlow = false;
                if (i >= count || !slot.Object.activeSelf || !slot.Body.enabled) continue;
                Sprite sprite = slot.Body.sprite;
                if (sprite == null) continue;
                bool held = slot.Glow.enabled;
                Color tint = slot.Body.color;
                Color32 tint32 = tint;
                bool dead = !held && (sprite.Pointer == _deadSprite || (open && tint.r == .5f && tint.g == .5f && tint.b == .5f));
                bool starPower = tint32.r == sp.r && tint32.g == sp.g && tint32.b == sp.b;
                Vector3 root = slot.Root.localPosition;
                int lane = 0; float closest = float.MaxValue;
                for (int l = 0; l < 5; l++) { float d = MathF.Abs(root.x - _lanes[l]); if (d < closest) { closest = d; lane = l; } }
                int variant = open ? (dead ? Gh3SustainBank.OpenDead : starPower ? Gh3SustainBank.OpenSp : Gh3SustainBank.Open)
                    : dead ? Gh3SustainBank.Dead : starPower ? Gh3SustainBank.Sp : lane;
                Gh3HighwayMesh body = _bodyMeshes[variant];
                if (body == null) continue;
                slot.Body.GetPropertyBlock(slot.Block);
                float length = slot.Block.GetFloat(LengthId);
                if (!(length > 0f) || !float.IsFinite(length)) continue;
                float far = MathF.Min(root.z + length, (float)_notes.noteZPosFarLimit);
                // Same held-root bridge as the working port: GH3's row 977.8.
                float near = MathF.Max(held ? _notes.strikeLine + .378f : root.z, (float)_notes.noteZPosCloseLimit);
                if (far <= near) continue;
                Vector4 whammy = Vector4.zero;
                if (held) { slot.Glow.GetPropertyBlock(slot.Block); whammy = slot.Block.GetVector(WhammyId); }
                float x = open ? 0f : _lanes[lane];
                float width = _halfWidth * (open ? 11.8f : 1f);
                float rowStep = (float)_notes.noteZPosFarLimit / 128f;
                float tipRamp = (float)_notes.noteZPosFarLimit / 32f;
                int segments = Math.Max(1, (int)MathF.Ceiling((far - near) / rowStep));
                Draw(body, x, width, near, far, segments, tipRamp, false, held, whammy);
                slot.ReplaceBody = true;
                Gh3HighwayMesh glow = _glowMeshes[variant];
                if (held && !dead && glow != null)
                {
                    if (segments >= 6) Draw(glow, x, width * (open ? 1f : 3.5f), near, far, segments, tipRamp, true, true, whammy);
                    slot.ReplaceGlow = true;
                }
                if (!_sampleLogged && held)
                {
                    _sampleLogged = true;
                    _log.Msg($"[ClonZones] Held GH3 ribbon: lane={lane}, root={root.z}, span={length}, near={near}, far={far}, whammy={whammy}.");
                }
                if (_spriteSamples < 6 && sprite.Pointer != _sampledSprite)
                {
                    _spriteSamples++; _sampledSprite = sprite.Pointer;
                    _log.Msg($"[ClonZones] Sustain sprite sample: '{sprite.name}' tint=({tint32.r},{tint32.g},{tint32.b}) sp=({sp.r},{sp.g},{sp.b}) open={open} held={held} dead={dead} classifiedSp={starPower}.");
                }
            }
        }

        private void Draw(Gh3HighwayMesh mesh, float x, float width, float near, float far, int segments,
            float tipRamp, bool glow, bool held, Vector4 whammy)
        {
            float pivot = near + (far - near) * (segments - 6) / segments;
            float z0 = near;
            for (int row = 0; row < segments; row++)
            {
                float z1 = near + (far - near) * (row + 1) / segments;
                float v0 = 1f - Math.Clamp((far - z0) / tipRamp, 0f, 1f);
                float v1 = 1f - Math.Clamp((far - z1) / tipRamp, 0f, 1f);
                float g0 = glow && z0 > pivot ? pivot + (z0 - pivot) * 1.4f : z0;
                float g1 = glow && z1 > pivot ? pivot + (z1 - pivot) * 1.4f : z1;
                Vector3 center0 = _trackMatrix.MultiplyPoint3x4(new Vector3(x, 0f, g0));
                Vector3 center1 = _trackMatrix.MultiplyPoint3x4(new Vector3(x, 0f, g1));
                float w0 = width * (held ? _whammy.Width(center0.z, whammy) : 1f);
                float w1 = width * (held ? _whammy.Width(center1.z, whammy) : 1f);
                float a0 = Gh3HighwayLayout.AlphaAtY(_bridge.DepthY(g0));
                float a1 = Gh3HighwayLayout.AlphaAtY(_bridge.DepthY(g1));
                if (glow) { a0 *= a0; a1 *= a1; }
                mesh.WorldQuad(_bridge.FieldPoint(x-w0, g0),
                    _bridge.FieldPoint(x+w0, g0),
                    _bridge.FieldPoint(x+w1, g1),
                    _bridge.FieldPoint(x-w1, g1), v0, v1,
                    new Color32(255,255,255,(byte)(255f*a0)), new Color32(255,255,255,(byte)(255f*a1)));
                z0 = z1;
            }
        }

        private static void Suppress(Slot[] slots)
        {
            foreach (Slot slot in slots)
            {
                if (slot.BodyHidden != slot.ReplaceBody) { slot.Body.forceRenderingOff = slot.ReplaceBody || slot.BodyWasOff; slot.BodyHidden = slot.ReplaceBody; }
                if (slot.GlowHidden != slot.ReplaceGlow) { slot.Glow.forceRenderingOff = slot.ReplaceGlow || slot.GlowWasOff; slot.GlowHidden = slot.ReplaceGlow; }
            }
        }

        public static void Clear()
        {
            _active = false;
            foreach (Gh3SustainPatch player in Players.Values) player.Dispose();
            Players.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Restore(_closed); Restore(_open);
            for (int i = 0; i < _bodyMeshes.Length; i++) { _bodyMeshes[i]?.Dispose(); _glowMeshes[i]?.Dispose(); }
        }

        private static void Restore(Slot[] slots)
        {
            foreach (Slot slot in slots)
            {
                if (slot.Body != null) slot.Body.forceRenderingOff = slot.BodyWasOff;
                if (slot.Glow != null) slot.Glow.forceRenderingOff = slot.GlowWasOff;
            }
        }
    }
}
