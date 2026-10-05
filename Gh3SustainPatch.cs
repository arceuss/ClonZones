using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using MelonLoader.NativeUtils;
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
        private readonly InterfacePublicAbstractBoSiByBoBySiUnique _whammyEngine;
        private readonly NativePredicate _isWhammying;
        private readonly IntPtr _whammyMethod;
        private Matrix4x4 _trackMatrix;
        private bool _disposed;
        private bool _sampleLogged;
        private int _identitySamples;

        private sealed class Slot
        {
            public SpriteRenderer Body, Glow;
            public Transform Root;
            public GameObject Object;
            public MaterialPropertyBlock Block;
            public bool BodyWasOff, GlowWasOff, BodyHidden, GlowHidden, ReplaceBody, ReplaceGlow;
            public bool HasIdentity;
            public int Lane, Variant;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeDraw(IntPtr renderer, IntPtr method);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeSustainDraw(IntPtr renderer, IntPtr note, ushort mask, float position, byte starPower, IntPtr method);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte NativePredicate(IntPtr instance, IntPtr method);

        private static readonly NativeDraw HeldDetour = CaptureHeld;
        private static readonly NativeSustainDraw SustainDetour = CaptureSustain;
        private static NativeHook<NativeDraw> _heldHook;
        private static NativeHook<NativeSustainDraw> _sustainHook;
        private static int _noteFlagsOffset, _noteStateOffset, _noteSeedOffset;
        // held-list walk (CaptureHeld). offsets checked on CH 1.1.0.6142 against il2cpp_field_get_offset, and a
        // probe compared every raw read with the interop wrapper path (24576 frames, 18382 held notes, 0 differences).
        private static int _activeOffset, _heldListOffset, _thresholdOffset, _listItemsOffset, _listSizeOffset, _noteRemainingOffset, _noteMaskOffset;
        // Il2CppArray header is klass, monitor, bounds, max_length; Il2CppArrayBase uses the same 4 * IntPtr.Size.
        private static readonly int ArrayDataOffset = 4 * IntPtr.Size;

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            if (!GuitarNoteHeadPatch.ColorShuffleReady)
            {
                log.Error("[ClonZones] Sustain color resolver unavailable; native ribbons retained.");
                return;
            }
            _log = log;
            try
            {
                Type renderer = typeof(GuitarNoteRenderer).BaseType;
                Type note = typeof(ObjectPublicObInObDoSiDoUIInBoInUnique);
                _noteFlagsOffset = CheckedFieldOffset(note, "field_Public_EnumNPublicSealedvaNoChDiExChHoStTaSoUnique_0", 0x7C);
                _noteStateOffset = CheckedFieldOffset(note, "field_Public_EnumNPublicSealedvaNoHiMiDyIsWa8vWaUnique_0", 0x84);
                _noteSeedOffset = CheckedFieldOffset(note, "field_Public_Int32_0", 0x20);
                _noteRemainingOffset = CheckedFieldOffset(note, "field_Public_Int64_3", 0x60);
                _noteMaskOffset = CheckedFieldOffset(note, "field_Public_UInt16_0", 0x80);
                _activeOffset = CheckedFieldOffset(renderer, "field_Private_Boolean_0", 0x21C);
                _heldListOffset = CheckedFieldOffset(renderer, "HeldSustainNotes", 0x180);
                _thresholdOffset = CheckedFieldOffset(renderer, "sustainRenderTickThreshold", 0x200);
                Type heldList = typeof(Il2CppSystem.Collections.Generic.List<ObjectPublicObInObDoSiDoUIInBoInUnique>);
                _listItemsOffset = CheckedFieldOffset(heldList, "_items", 0x10);
                _listSizeOffset = CheckedFieldOffset(heldList, "_size", 0x18);
                _heldHook = new NativeHook<NativeDraw>(Marshal.ReadIntPtr(NativeMethod(AccessTools.Method(renderer, "Method_Private_Void_PDM_1"))),
                    Marshal.GetFunctionPointerForDelegate(HeldDetour));
                _sustainHook = new NativeHook<NativeSustainDraw>(Marshal.ReadIntPtr(NativeMethod(AccessTools.Method(renderer,
                    "Method_Private_Void_ObjectPublicObInObDoSiDoUIInBoInUnique_UInt16_Single_Boolean_PDM_0"))),
                    Marshal.GetFunctionPointerForDelegate(SustainDetour));
                _heldHook.Attach();
                _sustainHook.Attach();
            }
            catch (Exception error)
            {
                _sustainHook?.Detach();
                _heldHook?.Detach();
                log.Error($"[ClonZones] Sustain identity binding failed; native ribbons retained: {error}");
                return;
            }
            harmony.Patch(AccessTools.Method(typeof(GuitarNoteRenderer), nameof(GuitarNoteRenderer.Start)),
                postfix: new HarmonyMethod(typeof(Gh3SustainPatch), nameof(Started)));
        }

        private static IntPtr NativeMethod(MethodInfo method)
        {
            if (method == null) throw new MissingMethodException("Required CH sustain method is missing.");
            FieldInfo field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            IntPtr info = field == null ? IntPtr.Zero : (IntPtr)field.GetValue(null);
            if (info == IntPtr.Zero || Marshal.ReadIntPtr(info) == IntPtr.Zero)
                throw new MissingMethodException($"CH sustain method has no native binding: {method.Name}");
            return info;
        }

        private static int CheckedFieldOffset(Type type, string name, int expected)
        {
            FieldInfo field = type.GetField("NativeFieldInfoPtr_" + name, BindingFlags.Static | BindingFlags.NonPublic);
            IntPtr info = field == null ? IntPtr.Zero : (IntPtr)field.GetValue(null);
            int offset = info == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(info);
            if (offset != expected)
                throw new NotSupportedException($"CH sustain field {type.Name}.{name}: offset {offset:X}, expected {expected:X}.");
            return offset;
        }

        private static Gh3SustainPatch Capturing(IntPtr renderer)
        {
            return _active && Players.TryGetValue(renderer, out Gh3SustainPatch player) && !player._disposed ? player : null;
        }

        private static Gh3SustainPatch CaptureStart(IntPtr renderer, out int closed, out int open)
        {
            closed = open = 0;
            Gh3SustainPatch player = Capturing(renderer);
            if (player == null) return null;
            try
            {
                closed = player._notes.field_Private_Int32_3;
                open = player._notes.field_Private_Int32_4;
                return player;
            }
            catch (Exception error) { player.Stop(error); return null; }
        }

        private static void CaptureSustain(IntPtr renderer, IntPtr note, ushort mask, float position, byte starPower, IntPtr method)
        {
            Gh3SustainPatch player = CaptureStart(renderer, out int closed, out int open);
            _sustainHook.Trampoline(renderer, note, mask, position, starPower, method);
            if (player == null) return;
            try
            {
                bool dead = (Marshal.ReadInt16(note, _noteStateOffset) & 2) != 0;
                player.RecordMask(note, mask, dead, starPower != 0, ref closed, ref open);
                player.CheckCounts(closed, open);
            }
            catch (Exception error) { player.Stop(error); }
        }

        private static void CaptureHeld(IntPtr renderer, IntPtr method)
        {
            Gh3SustainPatch player = CaptureStart(renderer, out int closed, out int open);
            _heldHook.Trampoline(renderer, method);
            if (player == null) return;
            try
            {
                // CH 181155460 appends held notes in list/ascending-mask order.
                // +60 is remaining sustain ticks, not the chart duration at +48.
                // raw reads, not HeldSustainNotes/_items[i]: the interop _items getter wraps the array through
                // reflection CreateInstance on every read, which made a finalizable wrapper plus binder garbage per
                // held note per frame (over half of all managed allocation on 3999ascent).
                IntPtr held = Marshal.ReadIntPtr(renderer, _heldListOffset);
                bool active = Marshal.ReadByte(renderer, _activeOffset) != 0;
                bool whammy = !active && player._isWhammying(player._whammyEngine.Pointer, player._whammyMethod) != 0;
                if (held != IntPtr.Zero)
                {
                    int size = Marshal.ReadInt32(held, _listSizeOffset);
                    IntPtr items = Marshal.ReadIntPtr(held, _listItemsOffset);
                    // the wrapper path threw here too (null/out-of-range element), which stops the replacement.
                    if (size > 0 && (items == IntPtr.Zero || (uint)size > IL2CPP.il2cpp_array_length(items)))
                        throw new InvalidOperationException($"CH held sustain list is inconsistent: size {size}.");
                    long threshold = Marshal.ReadInt64(renderer, _thresholdOffset);
                    for (int i = 0; i < size; i++)
                    {
                        IntPtr note = Marshal.ReadIntPtr(items, ArrayDataOffset + i * IntPtr.Size);
                        if (note == IntPtr.Zero)
                            throw new InvalidOperationException("CH held sustain list contains a null note.");
                        if (Marshal.ReadInt64(note, _noteRemainingOffset) < threshold) continue;
                        bool phrase = (Marshal.ReadInt32(note, _noteFlagsOffset) & 0x80) != 0;
                        player.RecordMask(note, (ushort)Marshal.ReadInt16(note, _noteMaskOffset), false, active || (phrase && whammy), ref closed, ref open);
                    }
                }
                player.CheckCounts(closed, open);
            }
            catch (Exception error) { player.Stop(error); }
        }

        private void RecordMask(IntPtr note, ushort mask, bool dead, bool starPower, ref int closed, ref int open)
        {
            bool shuffle = _notes.field_Protected_Boolean_1;
            int seed = 0;
            bool seedRead = false;
            for (int lane = 0; lane <= 5; lane++)
            {
                if ((mask & (1 << lane)) == 0) continue;
                bool isOpen = lane == 0;
                Slot[] slots = isOpen ? _open : _closed;
                int index = isOpen ? open : closed;
                if (index >= slots.Length) continue;
                int color;
                if (isOpen)
                    color = 0;
                else
                {
                    if (shuffle && !seedRead)
                    {
                        if (note == IntPtr.Zero)
                            throw new InvalidOperationException("CH sustain color resolver received a null note.");
                        seed = Marshal.ReadInt32(note, _noteSeedOffset);
                        seedRead = true;
                    }
                    color = GuitarNoteColor.Resolve(lane, seed, shuffle);
                }
                Slot slot = slots[index];
                // keep this while CH skips renderer updates; native pool contents survive too.
                slot.HasIdentity = true;
                slot.Lane = lane - 1;
                slot.Variant = dead ? (isOpen ? Gh3SustainBank.OpenDead : Gh3SustainBank.Dead)
                    : starPower ? (isOpen ? Gh3SustainBank.OpenSp : Gh3SustainBank.Sp)
                    : isOpen ? Gh3SustainBank.Open : color - 1;
                if (isOpen) open++; else closed++;
                if (_identitySamples < 6)
                {
                    _identitySamples++;
                    _log.Msg($"[ClonZones] Sustain identity: lane={lane}, color={color}, shuffle={shuffle}, dead={dead}, sp={starPower}, variant={slot.Variant}.");
                }
            }
        }

        private void CheckCounts(int closed, int open)
        {
            if (closed != _notes.field_Private_Int32_3 || open != _notes.field_Private_Int32_4)
                throw new InvalidOperationException($"CH sustain identity count mismatch: mapped {closed}/{open}, native {_notes.field_Private_Int32_3}/{_notes.field_Private_Int32_4}.");
        }

        private void Stop(Exception error)
        {
            Dispose();
            _log.Error($"[ClonZones] Sustain replacement stopped; native ribbons restored: {error}");
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
            try { Players.Add(notes.Pointer, new Gh3SustainPatch(notes, solid, additive)); }
            catch (Exception error) { _log.Error($"[ClonZones] Sustain attachment failed; native ribbons retained: {error}"); }
        }

        private Gh3SustainPatch(GuitarNoteRenderer notes, Shader solid, Shader additive)
        {
            _notes = notes;
            BasePlayer player = notes.GetComponent<BasePlayer>();
            _whammyEngine = player.engine.TryCast<InterfacePublicAbstractBoSiByBoBySiUnique>()
                ?? throw new NotSupportedException("CH guitar engine has no whammy interface.");
            IntPtr whammyInterfaceMethod = NativeMethod(typeof(InterfacePublicAbstractBoSiByBoBySiUnique)
                .GetProperty(nameof(InterfacePublicAbstractBoSiByBoBySiUnique.prop_Boolean_0)).GetMethod);
            _whammyMethod = IL2CPP.il2cpp_object_get_virtual_method(_whammyEngine.Pointer, whammyInterfaceMethod);
            if (_whammyMethod == IntPtr.Zero || Marshal.ReadIntPtr(_whammyMethod) == IntPtr.Zero)
                throw new MissingMethodException("CH whammy predicate has no native implementation.");
            _isWhammying = Marshal.GetDelegateForFunctionPointer<NativePredicate>(Marshal.ReadIntPtr(_whammyMethod));
            _track = player.transform;
            _bridge = new Gh3HighwayBridge(player.mainCamera, _track, notes);
            _bridge.Refresh();
            var frets = player.neckController.FretAnimators;
            for (int i = 0; i < _lanes.Length; i++) _lanes[i] = _track.InverseTransformPoint(frets[i].transform.position).x;
            _halfWidth = MathF.Abs(_lanes[4] - _lanes[0]) * 5f / 8f / 16f;
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

        // Size every ribbon mesh for the whole visible span up front: growing inside Draw
        // reallocates vertex/index storage and rebuilds the GPU buffers mid-song. Closed
        // variants can occupy all five lanes at once (SP, dead, colour shuffle); the +16 covers
        // the per-ribbon ceiling when several short sustains share a lane. Overflow still grows.
        private int SpanUnits(int variant)
        {
            float span = (float)(_notes.noteZPosFarLimit - _notes.noteZPosCloseLimit);
            if (!(span > 0f) || !float.IsFinite(span)) return 32;
            int perLane = (int)MathF.Ceiling(span / (Gh3HighwayBridge.DepthSpan / 128f)) + 16;
            int lanes = variant >= Gh3SustainBank.Open ? 1 : 5;
            return Math.Max(32, (perLane * lanes * 6 + 23) / 24);
        }

        private void ReserveSpan()
        {
            for (int i = 0; i < _bodyMeshes.Length; i++)
            {
                int units = SpanUnits(i);
                _bodyMeshes[i]?.Reserve(units);
                _glowMeshes[i]?.Reserve(units);
            }
        }

        private Gh3HighwayMesh CreateMesh(string kind, int variant, Texture texture, Shader shader, SpriteRenderer native)
        {
            return new Gh3HighwayMesh($"clonzones_gh3_sustain_{kind}_{variant}", texture, shader, null,
                native.gameObject.layer, native.sortingLayerID, native.sortingOrder, native.sharedMaterial.renderQueue, SpanUnits(variant), false);
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
            if (_disposed || !UnityIcalls.Alive(_notes)) return;
            if (_bridge.Refresh()) ReserveSpan();
            _trackMatrix = UnityIcalls.LocalToWorld(_track);
            _whammy.Refresh();
            for (int i = 0; i < _bodyMeshes.Length; i++) { _bodyMeshes[i]?.Begin(); _glowMeshes[i]?.Begin(); }
            Append(_closed, _notes.field_Private_Int32_3, false);
            Append(_open, _notes.field_Private_Int32_4, true);
            // Hide only after every mesh upload succeeds. No native sprite, material,
            // sliced geometry, shader uniform or pool-enabled state is rewritten.
            for (int i = 0; i < _bodyMeshes.Length; i++) { _bodyMeshes[i]?.Upload(); _glowMeshes[i]?.Upload(); }
            Suppress(_closed); Suppress(_open);
        }

        private void Append(Slot[] slots, int count, bool open)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                Slot slot = slots[i]; slot.ReplaceBody = false; slot.ReplaceGlow = false;
                if (i >= count || !slot.HasIdentity || !UnityIcalls.ActiveSelf(slot.Object) || !UnityIcalls.Enabled(slot.Body)) continue;
                if (!UnityIcalls.AlivePtr(UnityIcalls.SpritePtr(slot.Body))) continue;
                bool held = UnityIcalls.Enabled(slot.Glow);
                int variant = slot.Variant;
                bool dead = variant == Gh3SustainBank.Dead || variant == Gh3SustainBank.OpenDead;
                Vector3 root = UnityIcalls.LocalPosition(slot.Root);
                int lane = slot.Lane;
                Gh3HighwayMesh body = _bodyMeshes[variant];
                if (body == null) continue;
                slot.Body.GetPropertyBlock(slot.Block);
                float length = slot.Block.GetFloat(LengthId);
                if (!(length > 0f) || !float.IsFinite(length)) continue;
                float far = MathF.Min(root.z + length, (float)_notes.noteZPosFarLimit);
                // keep the held root at the same depth when the visible highway gets longer.
                float near = MathF.Max(held ? _notes.strikeLine + .378f : root.z, (float)_notes.noteZPosCloseLimit);
                if (far <= near) continue;
                Vector4 whammy = default; // Vector4.zero is a boxing invoke in the interop
                if (held) { slot.Glow.GetPropertyBlock(slot.Block); whammy = slot.Block.GetVector(WhammyId); }
                float x = open ? 0f : _lanes[lane];
                float width = _halfWidth * (open ? 11.8f : 1f);
                float rowStep = Gh3HighwayBridge.DepthSpan / 128f;
                float tipRamp = Gh3HighwayBridge.DepthSpan / 32f;
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
                float w0 = width, w1 = width;
                if (held)
                {
                    // only the held whammy width needs the transformed center. Vector3's
                    // constructor and MultiplyPoint3x4 are IL2CPP invokes here; field writes and
                    // the managed copy of the native sum give the same value without the box.
                    Vector3 local = default; local.x = x; local.z = g0;
                    w0 = width * _whammy.Width(UnityIcalls.MultiplyPoint3x4(_trackMatrix, local).z, whammy);
                    local.z = g1;
                    w1 = width * _whammy.Width(UnityIcalls.MultiplyPoint3x4(_trackMatrix, local).z, whammy);
                }
                // same entry curve as the heads' per-pixel fade (GH3 WhammyBar VS: smoothstep).
                float a0 = _bridge.EntryAlphaAtY(_bridge.DepthY(g0));
                float a1 = _bridge.EntryAlphaAtY(_bridge.DepthY(g1));
                if (glow) { a0 *= a0; a1 *= a1; }
                Color32 c0 = default, c1 = default;
                c0.r = c0.g = c0.b = 255; c0.a = (byte)(255f*a0);
                c1.r = c1.g = c1.b = 255; c1.a = (byte)(255f*a1);
                mesh.WorldQuad(_bridge.FieldPoint(x-w0, g0),
                    _bridge.FieldPoint(x+w0, g0),
                    _bridge.FieldPoint(x+w1, g1),
                    _bridge.FieldPoint(x-w1, g1), v0, v1, c0, c1);
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
