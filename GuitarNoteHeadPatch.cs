using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using MelonLoader.NativeUtils;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    internal enum RenderPatchMode { Inactive, Gameplay, ShuttingDown }

    /// <summary>
    /// GH3-style note-head replacement, per fret.
    ///
    /// Findings that drive this design (all confirmed at runtime / IDA in this CH build):
    ///   - Lane is known in SetupState, while ApplyNewState owns the live state CH consumes.
    ///   - Direct SpriteRenderer mutation can outlive scene/song teardown and can drive IL2CPP
    ///     into invalid class/object pointers during native cleanup, so replacement must happen
    ///     before vanilla ApplyNewState touches renderers.
    ///   - Lane is known in SetupState only. Runtime traces mostly alternate Setup/Apply,
    ///     but cumulative counts can lead Apply by several Setup calls, so use a small
    ///     same-frame FIFO bridge instead of a single slot.
    ///
    /// Mechanism:
    ///   1. SetupState postfix resolves the lane/open state from the note and enqueues
    ///      a long-lived bank-owned replacement sprite.
    ///   2. ApplyNewState prefix dequeues exactly one entry and writes only the visible
    ///      head sprite field into the byref live state before vanilla state application.
    ///
    /// Safety:
    ///   - Queue values are only long-lived, mod-owned bank sprites or null placeholders.
    ///   - The bridge is cleared on scene changes and frame changes so stale entries do
    ///     not drift across frames/scenes.
    ///   - No vanilla pointer cache and no renderer/container teardown mutation.
    /// </summary>
    internal static class GuitarNoteHeadPatch
    {
        private static RenderPatchMode _mode = RenderPatchMode.Inactive;

        private const int MaxPendingSprites = 4096;
        private static readonly PendingSprite[] _pendingSprites = new PendingSprite[MaxPendingSprites];
        private static int _pendingHead;
        private static int _pendingCount;
        private static int _pendingFrame = -1;
        private static int _disabledBridgeFrame = -1;
        private static int _currentUnityFrame = -1;

        private static readonly string[] LaneNames = { "green", "red", "yellow", "blue", "orange" };

        // Precomputed note-head frame arrays. SetupState is the hottest note path,
        // so resolve string dictionary keys once and only index arrays per note.
        // Variant order: 0 normal, 1 hopo, 2 tap, 3 star_normal, 4 star_hopo, 5 tap_starpower.
        private static readonly string[] LaneKeySuffixes = { "normal", "hopo", "tap", "star_normal", "star_hopo", "tap_starpower" };
        private static readonly string[][] LaneKeys = BuildLaneKeys();
        private static readonly string[] ActiveClosedKeys =
        {
            "active_star_normal",
            "active_star_hopo",
            "active_tap_starpower",
            "active_phrase_star_normal",
            "active_phrase_star_hopo",
            "active_phrase_tap_starpower"
        };
        private static readonly string[] OpenKeys =
        {
            "open/normal",
            "open/hopo",
            "open/star_normal",
            "open/star_hopo",
            "active_open_normal",
            "active_open_hopo",
            "active_phrase_open_normal",
            "active_phrase_open_hopo"
        };

        private static Sprite[][][] _closedLaneFrames;
        private static Sprite[][] _activeClosedFrames;
        private static Sprite[][] _openFrames;
        private static bool _frameCacheReady;
        private static int _cachedAnimationUnityFrame = -1;
        private static int _cachedAnimationFrame;

        private const int VariantNormal = 0;
        private const int VariantHopo = 1;
        private const int VariantTap = 2;
        private const int VariantStarNormal = 3;
        private const int VariantStarHopo = 4;
        private const int VariantTapStarPower = 5;

        private const int ActiveNormal = 0;
        private const int ActiveHopo = 1;
        private const int ActiveTap = 2;
        private const int ActivePhraseNormal = 3;
        private const int ActivePhraseHopo = 4;
        private const int ActivePhraseTap = 5;

        private const int OpenNormal = 0;
        private const int OpenHopo = 1;
        private const int OpenStarNormal = 2;
        private const int OpenStarHopo = 3;
        private const int OpenActiveNormal = 4;
        private const int OpenActiveHopo = 5;
        private const int OpenActivePhraseNormal = 6;
        private const int OpenActivePhraseHopo = 7;

        private static string[][] BuildLaneKeys()
        {
            var keys = new string[LaneNames.Length][];
            for (int lane = 0; lane < LaneNames.Length; lane++)
            {
                keys[lane] = new string[LaneKeySuffixes.Length];
                for (int v = 0; v < LaneKeySuffixes.Length; v++)
                    keys[lane][v] = LaneNames[lane] + "/" + LaneKeySuffixes[v];
            }
            return keys;
        }

        // GuitarNoteState flags bitmask. v11prototypes/latest both keep HEAD_HUE_SHIFT_MASK = 2 at flags offset 0x32.
        private const short HeadHueShiftMask = 0x0002;
        private const int NoteFlagHopo = 0x10;
        private const int NoteFlagTap = 0x40;
        private const int NoteFlagStarPower = 0x80;

        private struct PendingSprite
        {
            public Sprite Sprite;
        }

        private struct ApplyStateRestore
        {
            public bool ShouldRestore;
            public Sprite OriginalHead;
            public Color32 OriginalColor;
            public short OriginalFlags;
            public IntPtr AppliedSpritePointer;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ApplyNewStateNativeDelegate(IntPtr instance, IntPtr state, IntPtr materialPropertyBlock, IntPtr methodInfo);

        private sealed class ApplyNativeHook
        {
            public string Name;
            public NativeHook<ApplyNewStateNativeDelegate> Hook;
            public ApplyNewStateNativeDelegate Detour;
        }

        private static readonly List<ApplyNativeHook> _nativeApplyHooks = new();
        private const float PhraseAnimationFps = 30f;

        private static int _lastSetupNoteIndex = -1;
        // Keep the stable MelonLoaderMod1 FIFO bridge: SetupState only resolves
        // the desired sprite, then ApplyNewState mutates the byref state at the
        // exact vanilla render point.

        // ─── Public API ────────────────────────────────────────────────────────

        public static void SetMode(RenderPatchMode mode)
        {
            _mode = mode;
            ClearBridge();
            if (mode == RenderPatchMode.Gameplay)
                EnsureSpriteFrameCache();
            else
                _lastSetupNoteIndex = -1;
        }

        public static void BeginSongTransition(string reason)
        {
            SetMode(RenderPatchMode.Inactive);
        }

        public static void BeginFrame()
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.NoteBeginFrame);
            CacheAnimationFrameForCurrentUnityFrame();
            if (_mode != RenderPatchMode.Gameplay)
                ClearBridge();
            ClonZonesProfiler.EndScope(ProfileScope.NoteBeginFrame, profileStart);
        }

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            var setupTargets = ResolveSetupStateMethods().ToList();
            if (setupTargets.Count == 0)
            {
                log.Error("[ClonZones] FATAL: no SetupState target resolved.");
                return;
            }

            // SetupState fires per visible note per rendered frame (tens of thousands
            // of calls per second in dense charts). Each Harmony crossing marshals the
            // note into a fresh managed wrapper, which is pure GC churn; prefer a
            // native hook that reads the two needed note fields by offset and falls
            // back to the Harmony postfix per target if anything fails to resolve.
            bool noteOffsetsResolved = ResolveNoteFieldOffsets(log);
            int nativeSetupCount = 0;
            foreach (var t in setupTargets)
            {
                if (noteOffsetsResolved && InstallNativeSetupHook(t, log))
                {
                    nativeSetupCount++;
                    log.Msg($"[ClonZones] Native SetupState hook target: {t.DeclaringType?.Name}.{t.Name}");
                    continue;
                }

                harmony.Patch(t, postfix: new HarmonyMethod(typeof(GuitarNoteHeadPatch), nameof(SetupStatePostfix)));
                log.Msg($"[ClonZones] SetupState target (Harmony): {t.DeclaringType?.Name}.{t.Name}");
            }
            log.Msg($"[ClonZones] Installed SetupState on {setupTargets.Count} method(s) ({nativeSetupCount} native).");

            var applyTargets = ResolveApplyNewStateMethods().ToList();
            foreach (var m in applyTargets)
            {
                if (InstallNativeApplyHook(m, log))
                    log.Msg($"[ClonZones] Native ApplyNewState hook target: {m.DeclaringType?.Name}.{m.Name}");
            }
            log.Msg($"[ClonZones] Installed native ApplyNewState hook(s) on {_nativeApplyHooks.Count} method(s).");
            EnsureSpriteFrameCache();

        }

        private static bool InstallNativeApplyHook(MethodInfo method, MelonLogger.Instance log)
        {
            try
            {
                var methodInfoField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                var methodInfoPtr = (IntPtr)methodInfoField.GetValue(null);
                if (methodInfoPtr == IntPtr.Zero)
                {
                    log.Error($"[ClonZones] Native ApplyNewState hook failed for {method.Name}: MethodInfo pointer is zero.");
                    return false;
                }

                var targetPtr = Marshal.ReadIntPtr(methodInfoPtr);
                if (targetPtr == IntPtr.Zero)
                {
                    log.Error($"[ClonZones] Native ApplyNewState hook failed for {method.Name}: method pointer is zero.");
                    return false;
                }

                var hook = new ApplyNativeHook { Name = method.Name };
                hook.Detour = (instance, state, materialPropertyBlock, methodInfo) => ApplyNewStateNativeDetour(hook, instance, state, materialPropertyBlock, methodInfo);
                hook.Hook = new NativeHook<ApplyNewStateNativeDelegate>(targetPtr, Marshal.GetFunctionPointerForDelegate(hook.Detour));
                hook.Hook.Attach();
                _nativeApplyHooks.Add(hook);
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[ClonZones] Native ApplyNewState hook failed for {method.Name}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // ─── Native SetupState hook: per-note field reads without wrapper churn ──

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetupStateNativeDelegate(IntPtr instance, IntPtr noteSprites, int lanePos, int colorPos, IntPtr note, int noteIndex, byte isSPActive, byte unusedFlag, IntPtr methodInfo);

        private sealed class SetupNativeHook
        {
            public string Name;
            public NativeHook<SetupStateNativeDelegate> Hook;
            public SetupStateNativeDelegate Detour;
        }

        private static readonly List<SetupNativeHook> _nativeSetupHooks = new();
        private static int _noteMaskOffset = -1;
        private static int _noteFlagsOffset = -1;
        private static int _noteFlagsSize = 4;
        private static bool _noteReadsValidated;
        private static bool _noteNativeReadsBroken;

        private static bool ResolveNoteFieldOffsets(MelonLogger.Instance log)
        {
            try
            {
                var noteType = typeof(ObjectPublicObInObDoSiDoUIInBoInUnique);
                _noteMaskOffset = GetNativeFieldOffset(noteType, "NativeFieldInfoPtr_field_Public_UInt16_0");
                _noteFlagsOffset = GetNativeFieldOffset(noteType, "NativeFieldInfoPtr_field_Public_EnumNPublicSealedvaNoChDiExChHoStTaSoUnique_0");

                var flagsType = noteType.GetProperty("field_Public_EnumNPublicSealedvaNoChDiExChHoStTaSoUnique_0", BindingFlags.Instance | BindingFlags.Public)?.PropertyType
                                ?? noteType.GetField("field_Public_EnumNPublicSealedvaNoChDiExChHoStTaSoUnique_0", BindingFlags.Instance | BindingFlags.Public)?.FieldType;
                var underlying = flagsType != null && flagsType.IsEnum ? Enum.GetUnderlyingType(flagsType) : null;
                _noteFlagsSize = underlying == typeof(byte) || underlying == typeof(sbyte) ? 1
                    : underlying == typeof(short) || underlying == typeof(ushort) ? 2
                    : 4;

                return _noteMaskOffset > 0 && _noteFlagsOffset > 0;
            }
            catch (Exception ex)
            {
                log.Warning($"[ClonZones] Could not resolve note field offsets; SetupState stays on Harmony: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static int GetNativeFieldOffset(Type type, string nativeFieldInfoName)
        {
            var field = type.GetField(nativeFieldInfoName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
                return -1;

            var fieldInfoPtr = (IntPtr)field.GetValue(null);
            if (fieldInfoPtr == IntPtr.Zero)
                return -1;

            return (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(fieldInfoPtr);
        }

        private static int ReadNoteFlags(IntPtr note)
        {
            return _noteFlagsSize switch
            {
                1 => Marshal.ReadByte(note, _noteFlagsOffset),
                2 => Marshal.ReadInt16(note, _noteFlagsOffset),
                _ => Marshal.ReadInt32(note, _noteFlagsOffset)
            };
        }

        private static bool InstallNativeSetupHook(MethodInfo method, MelonLogger.Instance log)
        {
            try
            {
                var methodInfoField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                var methodInfoPtr = methodInfoField == null ? IntPtr.Zero : (IntPtr)methodInfoField.GetValue(null);
                if (methodInfoPtr == IntPtr.Zero)
                {
                    log.Warning($"[ClonZones] Native SetupState hook failed for {method.Name}: MethodInfo pointer is zero.");
                    return false;
                }

                var targetPtr = Marshal.ReadIntPtr(methodInfoPtr);
                if (targetPtr == IntPtr.Zero)
                {
                    log.Warning($"[ClonZones] Native SetupState hook failed for {method.Name}: method pointer is zero.");
                    return false;
                }

                var hook = new SetupNativeHook { Name = method.Name };
                hook.Detour = (instance, noteSprites, lanePos, colorPos, note, noteIndex, isSPActive, unusedFlag, methodInfo)
                    => SetupStateNativeDetour(hook, instance, noteSprites, lanePos, colorPos, note, noteIndex, isSPActive, unusedFlag, methodInfo);
                hook.Hook = new NativeHook<SetupStateNativeDelegate>(targetPtr, Marshal.GetFunctionPointerForDelegate(hook.Detour));
                hook.Hook.Attach();
                _nativeSetupHooks.Add(hook);
                return true;
            }
            catch (Exception ex)
            {
                log.Warning($"[ClonZones] Native SetupState hook failed for {method.Name}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void SetupStateNativeDetour(SetupNativeHook hook, IntPtr instance, IntPtr noteSprites, int lanePos, int colorPos, IntPtr note, int noteIndex, byte isSPActive, byte unusedFlag, IntPtr methodInfo)
        {
            hook.Hook.Trampoline(instance, noteSprites, lanePos, colorPos, note, noteIndex, isSPActive, unusedFlag, methodInfo);

            try
            {
                if (_mode != RenderPatchMode.Gameplay)
                    return;
                if (!_frameCacheReady && !EnsureSpriteFrameCache())
                    return;

                bool hasNote = note != IntPtr.Zero;
                ushort noteMask = 0;
                int noteFlags = 0;
                if (hasNote)
                {
                    if (_noteNativeReadsBroken)
                    {
                        var wrapper = new ObjectPublicObInObDoSiDoUIInBoInUnique(note);
                        noteMask = wrapper.field_Public_UInt16_0;
                        noteFlags = (int)wrapper.field_Public_EnumNPublicSealedvaNoChDiExChHoStTaSoUnique_0;
                    }
                    else
                    {
                        noteMask = unchecked((ushort)Marshal.ReadInt16(note, _noteMaskOffset));
                        noteFlags = ReadNoteFlags(note);

                        if (!_noteReadsValidated)
                        {
                            // One-time cross-check against the managed wrapper. If the
                            // native offsets ever disagree, permanently fall back to
                            // wrapper reads so sprite selection cannot silently drift.
                            var wrapper = new ObjectPublicObInObDoSiDoUIInBoInUnique(note);
                            ushort managedMask = wrapper.field_Public_UInt16_0;
                            int managedFlags = (int)wrapper.field_Public_EnumNPublicSealedvaNoChDiExChHoStTaSoUnique_0;
                            if (managedMask != noteMask || managedFlags != noteFlags)
                            {
                                _noteNativeReadsBroken = true;
                                noteMask = managedMask;
                                noteFlags = managedFlags;
                                MelonLogger.Warning("[ClonZones] Native note field reads disagree with managed wrapper; using wrapper reads.");
                            }
                            _noteReadsValidated = true;
                        }
                    }
                }

                ProcessSetupState(lanePos, colorPos, hasNote, noteMask, noteFlags, noteIndex, isSPActive != 0);
            }
            catch
            {
            }
        }

        private static void ApplyNewStateNativeDetour(ApplyNativeHook hook, IntPtr instance, IntPtr state, IntPtr materialPropertyBlock, IntPtr methodInfo)
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.NoteApply);
            try
            {
                if (_mode == RenderPatchMode.Gameplay && _frameCacheReady && TryDequeueBridge(out PendingSprite pending))
                {
                    var sprite = pending.Sprite;
                    if (!ReferenceEquals(sprite, null) && state != IntPtr.Zero)
                    {
                        IntPtr spritePtr = sprite.Pointer;
                        if (spritePtr != IntPtr.Zero)
                        {
                            Marshal.WriteIntPtr(state, 0x00, spritePtr);
                            Marshal.WriteInt32(state, 0x20, unchecked((int)0xFFFFFFFF));
                            // ClonZones note sprites are fully authored in the Head slot.
                            // Hide vanilla auxiliary slots for both closed and open notes;
                            // otherwise SP-active opens can inherit CH's colored tap/anim
                            // layers and visually turn into yellow tap gems.
                            Marshal.WriteIntPtr(state, 0x08, IntPtr.Zero); // Body
                            Marshal.WriteIntPtr(state, 0x10, IntPtr.Zero); // Anim
                            Marshal.WriteIntPtr(state, 0x18, IntPtr.Zero); // Open_HOPO
                            Marshal.WriteInt32(state, 0x24, 0);
                            Marshal.WriteInt32(state, 0x28, 0);
                            Marshal.WriteInt32(state, 0x2C, 0);
                            short flags = Marshal.ReadInt16(state, 0x32);
                            if ((flags & HeadHueShiftMask) != 0)
                                Marshal.WriteInt16(state, 0x32, (short)(flags & ~HeadHueShiftMask));
                        }

                    }
                }
            }
            catch
            {
            }
            finally
            {
                ClonZonesProfiler.EndScope(ProfileScope.NoteApply, profileStart);
            }

            hook.Hook.Trampoline(instance, state, materialPropertyBlock, methodInfo);
        }


        // ─── SetupState postfix: enqueue our fret sprite for the next apply ─────

        private static void SetupStatePostfix(
            int __1,                                    // lanePos
            int __2,                                    // colorPos
            ObjectPublicObInObDoSiDoUIInBoInUnique __3, // note
            int __4,                                    // noteIndex
            bool __5)                                   // isSPActive
        {

            if (_mode != RenderPatchMode.Gameplay) return;
            if (!_frameCacheReady && !EnsureSpriteFrameCache()) return;

            var note = __3;
            bool hasNote = !ReferenceEquals(note, null);
            ushort noteMask = hasNote ? note.field_Public_UInt16_0 : (ushort)0;
            int noteFlags = hasNote ? (int)note.field_Public_EnumNPublicSealedvaNoChDiExChHoStTaSoUnique_0 : 0;
            ProcessSetupState(__1, __2, hasNote, noteMask, noteFlags, __4, __5);
        }

        // Shared by the Harmony postfix fallback and the native SetupState detour.
        private static void ProcessSetupState(int lanePos, int colorPos, bool hasNote, ushort noteMask, int noteFlags, int noteIndex, bool isSPActive)
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.NoteSetup);

            if (_lastSetupNoteIndex >= 0 && noteIndex < _lastSetupNoteIndex)
            {
                ClearBridge();
            }
            _lastSetupNoteIndex = noteIndex;

            Sprite ourSprite = null;
            Sprite[] frames;

            if (!hasNote)
            {
                // Missing note data: enqueue a vanilla fallback placeholder so the
                // same-frame Setup/Apply bridge stays aligned.
            }
            else if (IsOpenMask(noteMask))
            {
                frames = ResolveOpenFrames(noteFlags, isSPActive);
                ourSprite = ResolveSprite(frames);
            }
            else if (!TryResolveClosedLane(noteMask, lanePos, colorPos, out int resolvedLane))
            {
                // Unknown closed-lane mapping: fallback placeholder.
            }
            else
            {
                frames = ResolveClosedFrames(resolvedLane, noteFlags, isSPActive);
                ourSprite = ResolveSprite(frames);
            }


            EnqueueBridge(ourSprite);
            ClonZonesProfiler.EndScope(ProfileScope.NoteSetup, profileStart);
        }

        // ─── ApplyNewState prefix: assign our fret sprite into the live state ──

        private static void ApplyNewStatePrefix(ref ValueTypePublicSealedBySpByCoSpInByCoSBSp0 __0, out ApplyStateRestore __state)
        {
            __state = default;

            try
            {
                if (_mode != RenderPatchMode.Gameplay || !_frameCacheReady)
                    return;

                if (!TryDequeueBridge(out PendingSprite pending))
                    return;

                Sprite ourSprite = pending.Sprite;
                if (ReferenceEquals(ourSprite, null) || ourSprite.Pointer == IntPtr.Zero)
                    return;


                // Open notes (N 7 0) are centered/rendered through CH's normal Head/Body/Anim
                // open-note state. Do not write Open_HOPO here; that slot is only the
                // optional open-HOPO glow, not the main visible open note.


                // Apply the replacement only for vanilla ApplyNewState, then the postfix
                // restores the byref state. This keeps CH's pooled/restarted note state
                // from retaining mod-owned Unity sprites across an in-song restart.
                __state = new ApplyStateRestore
                {
                    ShouldRestore = true,
                    OriginalHead = __0.field_Public_Sprite_0,
                    OriginalColor = __0.field_Public_Color32_0,
                    OriginalFlags = __0.field_Public_Int16_1,
                    AppliedSpritePointer = ourSprite.Pointer
                };

                __0.field_Public_Sprite_0 = ourSprite;
                __0.field_Public_Color32_0 = new Color32(255, 255, 255, 255);
                __0.field_Public_Int16_1 = (short)(__0.field_Public_Int16_1 & ~HeadHueShiftMask);
            }
            catch
            {
                __state = default;
            }
        }

        private static Exception ApplyNewStateFinalizer(ref ValueTypePublicSealedBySpByCoSpInByCoSBSp0 __0, ApplyStateRestore __state, Exception __exception)
        {
            // Native ApplyNewState hooks are the active v1.1 hotfix path; keep
            // this legacy Harmony finalizer inert if a future patch path re-enables it.
            return __exception;
        }

        private static void RestoreApplyState(ref ValueTypePublicSealedBySpByCoSpInByCoSBSp0 state, ApplyStateRestore restore)
        {
            if (!restore.ShouldRestore)
                return;

            // Do not touch live SpriteRenderers here. Only undo the temporary byref
            // state mutation if it still contains the sprite this prefix installed.
            if (!ReferenceEquals(state.field_Public_Sprite_0, null)
                && state.field_Public_Sprite_0.Pointer == restore.AppliedSpritePointer)
            {
                state.field_Public_Sprite_0 = restore.OriginalHead;
                state.field_Public_Color32_0 = restore.OriginalColor;
                state.field_Public_Int16_1 = restore.OriginalFlags;
            }
        }



        // ─── Helpers ───────────────────────────────────────────────────────────



        private static void ClearBridge()
        {
            _pendingHead = 0;
            _pendingCount = 0;
            _pendingFrame = -1;
        }

        private static void EnqueueBridge(Sprite sprite)
        {
            int frame = _currentUnityFrame;
            if (_pendingFrame != frame)
            {
                _pendingHead = 0;
                _pendingCount = 0;
                _pendingFrame = frame;
                _disabledBridgeFrame = -1;
            }

            if (_disabledBridgeFrame == frame)
                return;

            if (_pendingCount >= MaxPendingSprites)
            {
                // If a pathological chart outruns the same-frame FIFO capacity, fail
                // closed for this frame instead of clearing and re-aligning later notes
                // to the wrong queued sprites, which causes color/lane shuffling.
                _pendingHead = 0;
                _pendingCount = 0;
                _disabledBridgeFrame = frame;
                return;
            }

            int tail = _pendingHead + _pendingCount;
            if (tail >= MaxPendingSprites)
                tail -= MaxPendingSprites;

            _pendingSprites[tail].Sprite = sprite;
            _pendingCount++;
        }

        private static bool TryDequeueBridge(out PendingSprite pending)
        {
            pending = default;

            int frame = _currentUnityFrame;
            if (_pendingFrame != frame)
            {
                _pendingHead = 0;
                _pendingCount = 0;
                _pendingFrame = frame;
                return false;
            }

            if (_disabledBridgeFrame == frame)
            {
                _pendingHead = 0;
                _pendingCount = 0;
                return false;
            }

            if (_pendingCount == 0)
                return false;

            pending = _pendingSprites[_pendingHead];
            _pendingHead++;
            if (_pendingHead >= MaxPendingSprites)
                _pendingHead = 0;
            _pendingCount--;
            return true;
        }

        private static bool EnsureSpriteFrameCache()
        {
            if (_frameCacheReady)
                return true;
            if (!NoteHeadSpriteBank.IsReady)
                return false;

            var closedLaneFrames = new Sprite[LaneNames.Length][][];
            for (int lane = 0; lane < LaneNames.Length; lane++)
            {
                closedLaneFrames[lane] = new Sprite[LaneKeySuffixes.Length][];
                for (int variant = 0; variant < LaneKeySuffixes.Length; variant++)
                    closedLaneFrames[lane][variant] = GetFramesOrNull(LaneKeys[lane][variant]);
            }

            var activeClosedFrames = new Sprite[ActiveClosedKeys.Length][];
            for (int i = 0; i < ActiveClosedKeys.Length; i++)
                activeClosedFrames[i] = GetFramesOrNull(ActiveClosedKeys[i]);

            var openFrames = new Sprite[OpenKeys.Length][];
            for (int i = 0; i < OpenKeys.Length; i++)
                openFrames[i] = GetFramesOrNull(OpenKeys[i]);

            _closedLaneFrames = closedLaneFrames;
            _activeClosedFrames = activeClosedFrames;
            _openFrames = openFrames;
            _frameCacheReady = true;
            return true;
        }

        private static Sprite[] GetFramesOrNull(string key)
        {
            return NoteHeadSpriteBank.TryGetFrames(key, out var frames) ? frames : null;
        }

        private static Sprite[] ResolveOpenFrames(int noteFlags, bool isSpActive)
        {
            bool isTap = (noteFlags & NoteFlagTap) != 0;
            bool isHopo = (noteFlags & NoteFlagHopo) != 0;
            bool isPhrase = (noteFlags & NoteFlagStarPower) != 0;
            bool hopoLike = isTap || isHopo;

            if (isPhrase)
            {
                if (isSpActive)
                    return _openFrames[hopoLike ? OpenActivePhraseHopo : OpenActivePhraseNormal];
                return _openFrames[hopoLike ? OpenStarHopo : OpenStarNormal];
            }

            if (isSpActive)
                return _openFrames[hopoLike ? OpenActiveHopo : OpenActiveNormal];

            return _openFrames[hopoLike ? OpenHopo : OpenNormal];
        }

        private static Sprite[] ResolveClosedFrames(int lane, int noteFlags, bool isSpActive)
        {
            if ((uint)lane >= (uint)LaneNames.Length)
                return null;

            bool isTap = (noteFlags & NoteFlagTap) != 0;
            bool isHopo = (noteFlags & NoteFlagHopo) != 0;
            bool isPhrase = (noteFlags & NoteFlagStarPower) != 0;

            if (isSpActive)
            {
                if (isPhrase)
                {
                    if (isTap) return _activeClosedFrames[ActivePhraseTap];
                    if (isHopo) return _activeClosedFrames[ActivePhraseHopo];
                    return _activeClosedFrames[ActivePhraseNormal];
                }

                if (isTap) return _activeClosedFrames[ActiveTap];
                if (isHopo) return _activeClosedFrames[ActiveHopo];
                return _activeClosedFrames[ActiveNormal];
            }

            var laneFrames = _closedLaneFrames[lane];
            if (isPhrase)
            {
                if (isTap) return laneFrames[VariantTapStarPower];
                if (isHopo) return laneFrames[VariantStarHopo];
                return laneFrames[VariantStarNormal];
            }

            if (isTap) return laneFrames[VariantTap];
            if (isHopo) return laneFrames[VariantHopo];
            return laneFrames[VariantNormal];
        }

        private static Sprite ResolveSprite(Sprite[] frames)
        {
            int count = frames?.Length ?? 0;
            if (count == 0)
                return null;

            int frame = count > 1 ? _cachedAnimationFrame : 0;
            return frames[frame % count];
        }

        private static int GetCachedAnimationFrame()
        {
            return _cachedAnimationFrame;
        }

        private static void CacheAnimationFrameForCurrentUnityFrame()
        {
            _currentUnityFrame = Time.frameCount;
            _cachedAnimationUnityFrame = _currentUnityFrame;
            _cachedAnimationFrame = Mathf.FloorToInt(Time.time * PhraseAnimationFps);
        }

        private static bool IsOpenMask(ushort noteMask) => (noteMask & 0x0001) != 0;


        private static bool TryResolveClosedLane(ushort noteMask, int lanePos, int colorPos, out int lane)
        {
            lane = -1;


            // CH passes colorPos separately from lanePos. lanePos is physical/highway
            // placement; colorPos is the visual color after modifiers such as Color
            // Shuffle. v1.1 uses the same 1-based guitar lane convention here as
            // lanePos (1=green ... 5=orange). Prefer that visual color and fall back
            // to raw note data when CH does not provide one.
            if (colorPos >= 1 && colorPos <= 5)
            {
                lane = colorPos - 1;
                return true;
            }
            if (colorPos == 0)
            {
                lane = 0;
                return true;
            }
            if (lanePos >= 1 && lanePos <= 5)
            {
                ushort expected = (ushort)(1 << lanePos);
                if ((noteMask & expected) != 0) { lane = lanePos - 1; return true; }
            }
            ushort closed = (ushort)(noteMask & 0x003E);
            if (closed != 0 && (closed & (closed - 1)) == 0)
            {
                lane = LowestBitIndex(closed) - 1;
                return lane >= 0 && lane < 5;
            }
            return false;
        }

        private static int LowestBitIndex(ushort v)
        {
            for (int i = 0; i < 16; i++)
                if ((v & (1 << i)) != 0) return i;
            return -1;
        }




        // ─── Hook target resolution ────────────────────────────────────────────

        private static IEnumerable<MethodInfo> ResolveSetupStateMethods()
        {
            return typeof(ValueTypePublicSealedBySpByCoSpInByCoSBSp0)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(IsSetupStateSignature);
        }

        private static IEnumerable<MethodInfo> ResolveApplyNewStateMethods()
        {
            return typeof(GuitarNoteContainer)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(IsApplyNewStateSignature);
        }




        private static bool IsSetupStateSignature(MethodInfo m)
        {
            if (m.ReturnType != typeof(void)) return false;
            var p = m.GetParameters();
            if (p.Length != 7) return false;
            return p[0].ParameterType == typeof(GuitarNoteSprites)
                && p[1].ParameterType == typeof(int)
                && p[2].ParameterType == typeof(int)
                && p[3].ParameterType == typeof(ObjectPublicObInObDoSiDoUIInBoInUnique)
                && p[4].ParameterType == typeof(int)
                && p[5].ParameterType == typeof(bool)
                && p[6].ParameterType == typeof(bool);
        }



        private static bool IsApplyNewStateSignature(MethodInfo m)
        {
            if (m.ReturnType != typeof(void)) return false;
            var p = m.GetParameters();
            if (p.Length != 2) return false;
            return p[0].ParameterType.IsByRef
                && p[0].ParameterType.GetElementType() == typeof(ValueTypePublicSealedBySpByCoSpInByCoSBSp0)
                && p[1].ParameterType == typeof(MaterialPropertyBlock);
        }
    }
}
