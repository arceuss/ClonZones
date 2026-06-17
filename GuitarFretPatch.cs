using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Common;
using MelonLoader;
using MelonLoader.NativeUtils;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Minimal fret sprite replacement. Frets are persistent lane objects, so this
    /// patches fret animator lifecycle/press methods and does not touch note state.
    /// </summary>
    internal static class GuitarFretPatch
    {
        private static readonly string[] LaneNames = { "green", "red", "yellow", "blue", "orange" };

        // Fret animators are persistent objects. Resolve lane/renderers/sprites once per
        // animator pointer, then keep the existing per-frame hooks lightweight.
        private static readonly Dictionary<IntPtr, FretVisualCache> VisualCaches = new();
        private static readonly Dictionary<IntPtr, object[]> NeckAnimatorCaches = new();
        private static readonly Dictionary<IntPtr, ushort> LastButtonsPressedByNeck = new();
        private static readonly Dictionary<MemberCacheKey, MemberInfo> MemberLookupCache = new();
        private static readonly HashSet<MemberCacheKey> MissingMemberLookupCache = new();

        private static readonly Dictionary<IntPtr, float> LitFretsUntil = new();
        private const float FretLitSeconds = 0.14f;

        private static readonly Color White = Color.white;
        private static readonly object[] EmptyAnimators = Array.Empty<object>();

        private static Type _fretType;
        private static int _playFretDepth;
        private static bool _createOverlayRenderers = true;
        private static bool _useOverlayRenderers = true;
        private static bool _useHeadOverlayRenderers = true;
        private static bool _useLiftOverlayRenderers = true;
        private static bool _hideVanillaWithForceRenderingOff = true;
        private static bool _skipBaseMaskDisableWhenOverlay;
        private const float FretAnimationFps = 30f;
        private static int _lastFretAnimationFrame = -1;

        private readonly struct MemberCacheKey : IEquatable<MemberCacheKey>
        {
            private readonly Type _type;
            private readonly string _name;

            public MemberCacheKey(Type type, string name)
            {
                _type = type;
                _name = name;
            }

            public bool Equals(MemberCacheKey other) => _type == other._type && string.Equals(_name, other._name, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is MemberCacheKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((_type != null ? _type.GetHashCode() : 0) * 397)
                           ^ (_name != null ? StringComparer.Ordinal.GetHashCode(_name) : 0);
                }
            }
        }

        private sealed class FretVisualCache
        {
            public IntPtr Ptr;
            public int LaneIndex;
            public BaseFretAnimator FretAnimator;

            public Sprite MidSprite;
            public Sprite LipSprite;
            public Sprite HeadSprite;
            public Sprite HeadLightSprite;
            public Sprite DownSprite;
            public Sprite[] MidFrames;
            public Sprite[] LipFrames;
            public Sprite[] HeadFrames;
            public Sprite[] HeadLightFrames;
            public Sprite[] DownFrames;
            public Sprite[] CurrentHeadFrames;
            public Sprite CurrentHeadFallback;

            public SpriteRenderer HookRenderer;
            public SpriteRenderer HeadRenderer;
            public Transform HeadTransform;
            public SpriteRenderer LiftRenderer;
            public SpriteRenderer BaseRenderer;
            public SpriteRenderer CoverRenderer;
            public SpriteRenderer HalfCoverRenderer;
            public SpriteRenderer HeadLightRenderer;
            public SpriteRenderer HeadCoverRenderer;
            public SpriteMask BaseMask;

            public Vector3 IdleHeadLocalPosition;
            public bool HasIdleHeadLocalPosition;
            public float LitUntil;

            // Settle-gate bookkeeping: true once the resting composition has been
            // applied for the current at-rest period. Lets the Update prefix skip
            // vanilla Update + our re-skin while the fret stays at rest, but only
            // after one final apply has locked in the resting visual.
            public bool SettledApplied;

            // Held state the last ApplyPressState composed with. The settle-gate
            // also covers steadily-held frets (down.png, head pinned at idle), so a
            // held-state flip the hooks somehow missed must reopen the gate and let
            // the Update postfix re-skin with the live isHeld value.
            public bool AppliedHeld;

            // Sustain settle-gate motion detector. Vanilla freezes the piston while
            // a sustain holds it up, so the sustain composition is static; gate only
            // once the head Y is bit-identical across two consecutive pre-Update
            // (prefix-side) samples, proving vanilla is no longer moving it.
            public float LastPrefixHeadY;
            public bool HasPrefixHeadY;

            public float ManualOpenPopUntil;
            public float ManualOpenPopStart;
            public float ManualOpenPopDelta;
            public bool HasOpenPopDelta;
            public float OpenPopDelta;
            public FretOverlayCache Overlay;
            public bool VanillaOverlayForceOffApplied;
            public bool BaseLipOverlayApplied;
            public bool HeadOverlayApplied;
            public bool LiftOverlayApplied;
        }

        private sealed class FretOverlayCache
        {
            public SpriteRenderer BaseRenderer;
            public SpriteRenderer HeadRenderer;
            public SpriteRenderer LiftRenderer;
            public SpriteRenderer HalfCoverRenderer;
            public Sprite LastHeadSprite;
            public bool LastLiftVisible;
            public Sprite LastBaseSprite;
            public Sprite LastHalfCoverSprite;
        }

        private readonly struct FretRuntimeState
        {
            public readonly bool IsHeld;
            public readonly bool IsSustaining;
            public readonly bool OpenNoteSustaining;

            public FretRuntimeState(bool isHeld, bool isSustaining, bool openNoteSustaining)
            {
                IsHeld = isHeld;
                IsSustaining = isSustaining;
                OpenNoteSustaining = openNoteSustaining;
            }
        }

        public static void Configure(string assetRoot, MelonLogger.Instance log)
        {
            _createOverlayRenderers = true;
            _useOverlayRenderers = true;
            _useHeadOverlayRenderers = true;
            _useLiftOverlayRenderers = true;
            _hideVanillaWithForceRenderingOff = true;
            _skipBaseMaskDisableWhenOverlay = false;
            _lastFretAnimationFrame = -1;
            ReadFretSettings(assetRoot);

            if (!_useOverlayRenderers)
            {
                _useHeadOverlayRenderers = false;
                _useLiftOverlayRenderers = false;
            }

            log?.Msg($"[ClonZones] Fret overlays: create={_createOverlayRenderers}, use={_useOverlayRenderers}, head={_useHeadOverlayRenderers}, lift={_useLiftOverlayRenderers}, animatedFrames={FretSpriteBank.HasAnimatedFrames}, fps={FretAnimationFps:0.###}, forceOff={_hideVanillaWithForceRenderingOff}, skipMask={_skipBaseMaskDisableWhenOverlay}");
        }


        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            _fretType = FindIl2CppType("GuitarFretAnimator");
            if (_fretType == null)
            {
                log.Warning("[ClonZones] Could not resolve GuitarFretAnimator; fret replacement disabled.");
                return;
            }

            var start = ResolveStartMethod(_fretType);
            if (start != null)
            {
                harmony.Patch(start, postfix: new HarmonyMethod(typeof(GuitarFretPatch), nameof(StartPostfix)));
                log.Msg($"[ClonZones] Installed GuitarFretAnimator.Start postfix: {start.Name}");
            }
            else
            {
                log.Warning("[ClonZones] Could not resolve GuitarFretAnimator.Start; idle fret replacement may wait for press/release.");
            }

            // Do not patch every no-arg void method: several fret update helpers call
            // each other and broad postfixing caused recursive IL2CPP stack overflow.
            // Patch only the semantic Pressed/Released methods, or their exact
            // BaseFretAnimator no-arg overrides when this build exposes obfuscated names.
            bool haveFretStateHooks = InstallFretStateHooks(harmony, log);

            var neckType = FindIl2CppType("GuitarNeckController");
            if (!haveFretStateHooks)
            {
                var updateFrets = ResolveUpdateFretsMethod(neckType);
                if (updateFrets != null)
                {
                    harmony.Patch(updateFrets, postfix: new HarmonyMethod(typeof(GuitarFretPatch), nameof(UpdateFretsPostfix)));
                    log.Msg($"[ClonZones] Installed GuitarNeckController.UpdateFrets reskin fallback postfix: {updateFrets.Name}");
                }
                else
                {
                    log.Warning("[ClonZones] Could not resolve GuitarNeckController.UpdateFrets fallback; down state may be overwritten by vanilla updates.");
                }
            }
            else
            {
                log.Msg("[ClonZones] Using BaseFretAnimator Pressed/Released overrides for fret held state; UpdateFrets fallback not installed.");
            }

            // EndFretHeldState(mask) is the sustain/note cleanup path, not physical
            // button state. Idle fret visuals should follow UpdateFrets/isHeld only;
            // patching EndFretHeldState here can desync held/down from actual input.

            var playFret = ResolvePlayFretMethod(neckType);
            if (playFret != null)
            {
                harmony.Patch(playFret, prefix: new HarmonyMethod(typeof(GuitarFretPatch), nameof(PlayFretPrefix)), postfix: new HarmonyMethod(typeof(GuitarFretPatch), nameof(PlayFretPostfix)));
                log.Msg($"[ClonZones] Installed GuitarNeckController.PlayFret postfix: {playFret.Name}");
            }

            var play = ResolvePlayMethod(_fretType);
            if (play != null)
            {
                harmony.Patch(play, postfix: new HarmonyMethod(typeof(GuitarFretPatch), nameof(PlayPostfix)));
                log.Msg($"[ClonZones] Installed GuitarFretAnimator.Play postfix: {play.Name}");
            }

            // Native Update() already calls UpdateSpriteColors before our Update
            // postfix. A separate UpdateSpriteColors postfix would normalize in the
            // middle of vanilla's frame update, then UpdatePostfix would do the same
            // work again. Keep one final post-vanilla normalization point instead.

            var update = ResolveNamedNoArgVoidMethod(_fretType, "Update");
            if (update != null)
            {
                // Prefer a native hook: Update fires per fret per rendered frame, so
                // even gated frames pay the Harmony interop crossing (arg marshaling
                // and managed wrapper churn) twice. The native detour reads only the
                // IntPtr-keyed cache on the hot path and allocates nothing.
                if (InstallNativeUpdateHook(update, log))
                {
                    log.Msg($"[ClonZones] Installed GuitarFretAnimator.Update settle-gate (native hook): {update.Name}");
                }
                else
                {
                    harmony.Patch(update,
                        prefix: new HarmonyMethod(typeof(GuitarFretPatch), nameof(UpdatePrefix)),
                        postfix: new HarmonyMethod(typeof(GuitarFretPatch), nameof(UpdatePostfix)));
                    log.Msg($"[ClonZones] Installed GuitarFretAnimator.Update settle-gate (prefix+postfix): {update.Name}");
                }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FretUpdateNativeDelegate(IntPtr instance, IntPtr methodInfo);

        private static NativeHook<FretUpdateNativeDelegate> _updateHook;
        private static FretUpdateNativeDelegate _updateDetour; // GC root for the marshaled delegate

        private static bool InstallNativeUpdateHook(MethodInfo update, MelonLogger.Instance log)
        {
            try
            {
                var methodInfoField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(update);
                var methodInfoPtr = methodInfoField == null ? IntPtr.Zero : (IntPtr)methodInfoField.GetValue(null);
                if (methodInfoPtr == IntPtr.Zero)
                    return false;

                var targetPtr = Marshal.ReadIntPtr(methodInfoPtr);
                if (targetPtr == IntPtr.Zero)
                    return false;

                _updateDetour = FretUpdateNativeDetour;
                _updateHook = new NativeHook<FretUpdateNativeDelegate>(targetPtr, Marshal.GetFunctionPointerForDelegate(_updateDetour));
                _updateHook.Attach();
                return true;
            }
            catch (Exception ex)
            {
                log.Warning($"[ClonZones] Native GuitarFretAnimator.Update hook failed; falling back to Harmony: {ex.GetType().Name}: {ex.Message}");
                _updateHook = null;
                _updateDetour = null;
                return false;
            }
        }

        // Native equivalent of UpdatePrefix + UpdatePostfix. No exception may cross
        // the native boundary, so all managed work is wrapped.
        private static void FretUpdateNativeDetour(IntPtr instance, IntPtr methodInfo)
        {
            FretVisualCache visual = null;
            try
            {
                if (instance != IntPtr.Zero
                    && FretSpriteBank.IsReady
                    && VisualCaches.TryGetValue(instance, out visual)
                    && IsVisualCacheAlive(visual)
                    && visual.FretAnimator != null)
                {
                    if (visual.SettledApplied && IsFretSettled(visual.FretAnimator, visual))
                    {
                        if (ClonZonesProfiler.Enabled)
                            ClonZonesProfiler.RecordFretRest(true);
                        return; // fully at rest: skip vanilla Update + re-skin entirely
                    }
                }
                else
                {
                    visual = null;
                }
            }
            catch
            {
                visual = null;
            }

            _updateHook.Trampoline(instance, methodInfo);

            try
            {
                if (instance == IntPtr.Zero || !FretSpriteBank.IsReady)
                    return;

                if (ClonZonesProfiler.Enabled)
                    ClonZonesProfiler.RecordFretRest(false);

                long profile = ClonZonesProfiler.BeginScope(ProfileScope.FretUpdate);
                // Reuse the cached managed wrapper; only a fret without a cache entry
                // yet pays a one-time wrapper construction to build it.
                object target = visual?.FretAnimator;
                if (target == null)
                    target = new GuitarFretAnimator(instance);
                bool fullOverlay = _useOverlayRenderers && _useHeadOverlayRenderers && _useLiftOverlayRenderers;
                ApplyPressState(target, forceRendererWrites: !fullOverlay);
                ClonZonesProfiler.EndScope(ProfileScope.FretUpdate, profile);
            }
            catch
            {
            }
        }

        public static void ClearRuntimeState()
        {
            VisualCaches.Clear();
            NeckAnimatorCaches.Clear();
            LastButtonsPressedByNeck.Clear();
            LitFretsUntil.Clear();
            _lastFretAnimationFrame = -1;
        }

        public static void TickAnimations()
        {
            if (!FretSpriteBank.IsReady || !FretSpriteBank.HasAnimatedFrames || VisualCaches.Count == 0)
                return;

            int frame = GetFretAnimationFrame();
            if (frame == _lastFretAnimationFrame)
                return;
            _lastFretAnimationFrame = frame;

            foreach (var visual in VisualCaches.Values)
            {
                if (!IsVisualCacheAlive(visual))
                    continue;

                ApplyAnimatedFrame(visual, frame);
            }
        }

        private static void StartPostfix(object __instance)
        {
            ApplyPressState(__instance);
        }

        private static void FretStatePostfix(object __instance)
        {
            ApplyPressState(__instance);
        }

        private static void PlayPostfix(object __instance, bool __0, bool __1)
        {
            float now = Time.time;
            // Every GuitarFretAnimator.Play() represents a vanilla fret pop, including
            // the song-start BeginningAnimation which calls Play(false, false) directly
            // one fret at a time instead of routing through GuitarNeckController.PlayFret.
            // Keep the settle-gate open for that time window so it cannot mark the fret
            // as idle before vanilla has advanced the pop off its rest position.
            MarkFretLit(__instance, now);

            if (_playFretDepth > 0 && __1)
            {
                // __1 is isOpenNote. CH lights all five GRYBO frets for open hits;
                // add the missing per-fret head lift without changing closed-note motion.
                MarkManualOpenPop(__instance, now);
            }

            ApplyPressState(__instance);

            // Play() queues a head pop that vanilla may only visibly advance on a later
            // Update. The settle-gate must therefore run upcoming Updates instead of
            // skipping them while the transform is still near idle.
            if (TryGetVisualCache(__instance, out var visual))
                visual.SettledApplied = false;
        }

        private static void PlayFretPrefix()
        {
            _playFretDepth++;
        }

        private static void PlayFretPostfix()
        {
            if (_playFretDepth > 0)
                _playFretDepth--;
        }

        // Settle-gate. When a fret is provably at rest, GuitarFretAnimator.Update is
        // idempotent (it re-asserts the same head position / sprites / sorting every
        // frame) and our re-skin reproduces the same frame. In that case we skip both
        // vanilla Update and our postfix: the renderers retain ClonZones' last-applied
        // resting state at zero interop cost. "At rest" includes steadily-held frets:
        // ClonZones pins the held head at idle showing down.png, so the composition is
        // just as static as an untouched fret, and the Pressed/Released/Play hooks
        // re-apply on every state flip. Any transient (held state changed since the
        // last apply, sustaining, open sustain, active pop/lit window, head not yet
        // settled at idle) runs the original path unchanged, so animated frames are
        // byte-identical to before.
        private static bool UpdatePrefix(object __instance, out bool __state)
        {
            __state = false;

            if (!TryGetVisualCache(__instance, out var visual))
                return true; // can't classify -> run vanilla; postfix early-outs

            if (!IsFretSettled(__instance, visual))
                return true; // transient -> run vanilla Update + re-skin

            if (!visual.SettledApplied)
                return true; // run once more so the resting composition is locked in

            // Fully at rest and already applied -> skip vanilla Update entirely.
            __state = true;
            if (ClonZonesProfiler.Enabled)
                ClonZonesProfiler.RecordFretRest(true);
            return false;
        }

        private static void UpdatePostfix(object __instance, bool __state)
        {
            if (__state)
                return; // gated: vanilla Update skipped, nothing to re-skin

            if (ClonZonesProfiler.Enabled)
                ClonZonesProfiler.RecordFretRest(false);

            long profile = ClonZonesProfiler.BeginScope(ProfileScope.FretUpdate);
            bool fullOverlay = _useOverlayRenderers && _useHeadOverlayRenderers && _useLiftOverlayRenderers;
            ApplyPressState(__instance, forceRendererWrites: !fullOverlay);
            ClonZonesProfiler.EndScope(ProfileScope.FretUpdate, profile);
        }

        // A fret is "settled" when vanilla Update has nothing left to animate. Cheap
        // direct-memory state bits are checked first and short-circuit before any
        // Transform interop call; the head-position read only happens once the fret
        // is otherwise idle.
        private static bool IsFretSettled(object instance, FretVisualCache visual)
        {
            var state = ReadFretRuntimeState(instance, visual);
            return IsFretSettled(visual, state, Time.time, sampleMotion: true);
        }

        private static bool IsFretSettled(FretVisualCache visual, FretRuntimeState state, float now, bool sampleMotion)
        {
            bool manualPop = visual.ManualOpenPopUntil > now;
            bool lit = visual.LitUntil > now;
            // Held alone does not block: the held-rest composition (down.png, head
            // pinned at idle) is static. Only a held flip our hooks have not yet
            // re-applied forces the Update path to run and re-skin.
            bool heldChanged = state.IsHeld != visual.AppliedHeld;
            if (heldChanged || manualPop || lit)
            {
                ClonZonesProfiler.RecordFretSettleBlocker(heldChanged, false, false, manualPop, lit, false);
                return false;
            }

            if (state.IsSustaining || state.OpenNoteSustaining)
            {
                // Vanilla holds the piston frozen while a sustain keeps it up, so the
                // sustain composition is static once motion stops. Motion is proven by
                // two bit-identical head Y samples taken pre-Update on consecutive
                // frames; the per-frame state re-read reopens the gate the instant the
                // sustain ends or the held state flips. Calls from ApplyPressState
                // (sampleMotion=false) only confirm the composition is locked in; the
                // prefix-side samples alone decide when vanilla Update may be skipped.
                if (!sampleMotion)
                    return true;

                var headTransform = visual.HeadTransform;
                if (headTransform == null)
                {
                    ClonZonesProfiler.RecordFretSettleBlocker(false, state.IsSustaining, state.OpenNoteSustaining, false, false, false);
                    return false;
                }

                float headY = headTransform.localPosition.y;
                bool steady = visual.HasPrefixHeadY && visual.LastPrefixHeadY == headY;
                visual.LastPrefixHeadY = headY;
                visual.HasPrefixHeadY = true;
                if (!steady)
                    ClonZonesProfiler.RecordFretSettleBlocker(false, state.IsSustaining, state.OpenNoteSustaining, false, false, false);
                return steady;
            }

            // Each new sustain must prove steadiness with fresh consecutive samples.
            visual.HasPrefixHeadY = false;

            // Timers clear; confirm the head has physically reached its idle rest band
            // (covers both the closed-note pop settling down and the post-release snap).
            bool headSettled = IsHeadSettledAtIdle(visual);
            if (!headSettled)
                ClonZonesProfiler.RecordFretSettleBlocker(false, false, false, false, false, true);
            return headSettled;
        }

        private static bool IsHeadSettledAtIdle(FretVisualCache visual)
        {
            if (!visual.HasIdleHeadLocalPosition)
                return false;

            var headTransform = visual.HeadTransform;
            if (headTransform == null)
                return false;

            float dy = headTransform.localPosition.y - visual.IdleHeadLocalPosition.y;
            return dy <= 0.001f && dy >= -0.0015f;
        }

        private static void UpdateFretsPostfix(object __instance, ushort __0)
        {
            // Fallback only: when exact Pressed/Released hooks are unavailable, mirror
            // vanilla's changed-mask early out and re-skin only the affected frets.
            // Held/down state still comes from BaseFretAnimator.isHeld, not this mask.
            var neckPtr = ObjectPointer(__instance);
            ushort lastButtons = 0;
            bool haveLastButtons = neckPtr != IntPtr.Zero
                                   && LastButtonsPressedByNeck.TryGetValue(neckPtr, out lastButtons);
            if (haveLastButtons && lastButtons == __0)
                return;

            ushort changedButtons = haveLastButtons ? (ushort)(lastButtons ^ __0) : ushort.MaxValue;
            if (neckPtr != IntPtr.Zero)
                LastButtonsPressedByNeck[neckPtr] = __0;

            var animators = GetNeckAnimatorCache(__instance);
            for (int i = 0; i < animators.Length; i++)
            {
                var animator = animators[i];
                if (!IsFretMaskAffected(animator, changedButtons))
                    continue;
                ApplyPressState(animator);
            }
        }

        private static void ApplyPressState(object instance, bool forceRendererWrites = false)
        {
            long profile = ClonZonesProfiler.BeginScope(ProfileScope.FretApply);

            if (!TryGetVisualCache(instance, out var visual))
            {
                ClonZonesProfiler.EndScope(ProfileScope.FretApply, profile);
                return;
            }

            float now = Time.time;
            ExpireManualOpenPopIfNeeded(visual, now);
            bool timerPopActive = visual.ManualOpenPopUntil > now || visual.LitUntil > now;
            bool headAboveIdle = !timerPopActive && IsHeadAboveIdle(visual);
            var fretState = ReadFretRuntimeState(instance, visual);
            bool isHeld = fretState.IsHeld;
            bool popActive = timerPopActive || IsFretPopActive(visual, now, headAboveIdle);
            // Extended sustains: CH can flicker isHeld off for a fret whose sustain
            // the player is still physically anchoring while playing other notes.
            // Vanilla hides that flicker (its piston stays popped); sprite-based held
            // states make it glaring. While this fret's own sustain is active and its
            // head is raised, keep composing the held/lit visual.
            bool isHeldVisual = isHeld || (fretState.IsSustaining && (popActive || headAboveIdle));
            bool showHitLit = popActive && isHeldVisual;
            bool activeApply = fretState.IsHeld || fretState.IsSustaining || fretState.OpenNoteSustaining || popActive || headAboveIdle;
            ClonZonesProfiler.RecordFretApplyKind(activeApply);
            long splitProfile = ClonZonesProfiler.BeginScope(activeApply ? ProfileScope.FretApplyActive : ProfileScope.FretApplyIdle);
            if (isHeld && !popActive)
                RestoreHeadLayerPosition(visual);

            int animationFrame = GetFretAnimationFrame();
            Sprite midSprite = GetFrameSprite(visual.MidFrames, visual.MidSprite, animationFrame);
            Sprite lipSprite = GetFrameSprite(visual.LipFrames, visual.LipSprite, animationFrame);

            if (_useOverlayRenderers)
                ApplyBaseLipOverlay(visual);
            else
                SetRendererSprite(visual.BaseRenderer, midSprite, forceRendererWrites);
            // Keep down.png as the ordinary held-fret state. During an actual note
            // hit pop, show head_lit briefly while the fret is still held.
            Sprite[] targetHeadFrames = showHitLit ? visual.HeadLightFrames : (isHeldVisual ? visual.DownFrames : visual.HeadFrames);
            Sprite targetHeadFallback = showHitLit ? visual.HeadLightSprite : (isHeldVisual ? visual.DownSprite : visual.HeadSprite);
            visual.CurrentHeadFrames = targetHeadFrames;
            visual.CurrentHeadFallback = targetHeadFallback;
            Sprite targetHeadSprite = GetFrameSprite(targetHeadFrames, targetHeadFallback, animationFrame);
            if (!(_useOverlayRenderers && _useHeadOverlayRenderers && ApplyHeadOverlay(visual, targetHeadSprite)))
                SetRendererSprite(visual.HeadRenderer, targetHeadSprite, forceRendererWrites);
            if (!_useOverlayRenderers)
                SetRendererSprite(visual.HalfCoverRenderer, lipSprite, forceRendererWrites);
            if (!_hideVanillaWithForceRenderingOff)
            {
                SetRendererSprite(visual.HeadLightRenderer, visual.HeadLightSprite, forceRendererWrites);
            }

            HideVanillaOverlayLayers(visual, forceRendererWrites);
            if (!(_useOverlayRenderers && _useHeadOverlayRenderers && _useLiftOverlayRenderers))
                SetMaskDisabled(visual, forceRendererWrites && !_useOverlayRenderers);
            ApplyManualOpenPop(visual, now);
            ApplyFretLayering(visual, isHeldVisual, popActive, headAboveIdle, forceRendererWrites);
            NormalizeAllRendererVisuals(visual, forceRendererWrites);

            // Settle-gate bookkeeping: record the held state this apply composed
            // with and whether the fret ended this apply at rest (idle, steadily
            // held, or frozen sustain), so the Update prefix may begin skipping.
            visual.AppliedHeld = isHeld;
            visual.SettledApplied = IsFretSettled(visual, fretState, now, sampleMotion: false);
            ClonZonesProfiler.EndScope(activeApply ? ProfileScope.FretApplyActive : ProfileScope.FretApplyIdle, splitProfile);

            ClonZonesProfiler.EndScope(ProfileScope.FretApply, profile);
        }

        private static bool TryGetVisualCache(object instance, out FretVisualCache visual)
        {
            visual = null;
            if (instance == null || !FretSpriteBank.IsReady)
                return false;

            var ptr = ObjectPointer(instance);
            if (ptr == IntPtr.Zero)
                return false;

            if (VisualCaches.TryGetValue(ptr, out visual) && IsVisualCacheAlive(visual))
                return true;

            if (!TryGetLane(instance, out int laneIndex, out string lane))
                return false;

            visual = new FretVisualCache
            {
                Ptr = ptr,
                LaneIndex = laneIndex,
                FretAnimator = instance as BaseFretAnimator,

                MidSprite = FretSpriteBank.Get(lane, "mid"),
                LipSprite = FretSpriteBank.Get(lane, "lip"),
                HeadSprite = FretSpriteBank.Get(lane, "head"),
                HeadLightSprite = FretSpriteBank.Get(lane, "head_light"),
                DownSprite = FretSpriteBank.Get(lane, "down"),
                MidFrames = FretSpriteBank.GetFrames(lane, "mid"),
                LipFrames = FretSpriteBank.GetFrames(lane, "lip"),
                HeadFrames = FretSpriteBank.GetFrames(lane, "head"),
                HeadLightFrames = FretSpriteBank.GetFrames(lane, "head_light"),
                DownFrames = FretSpriteBank.GetFrames(lane, "down"),

                HookRenderer = GetRenderer(instance, "hook"),
                HeadRenderer = GetRenderer(instance, "head"),
                LiftRenderer = GetRenderer(instance, "lift"),
                BaseRenderer = GetRenderer(instance, "Base"),
                CoverRenderer = GetRenderer(instance, "cover"),
                HalfCoverRenderer = GetRenderer(instance, "halfCover"),
                HeadLightRenderer = GetRenderer(instance, "headLight"),
                HeadCoverRenderer = GetRenderer(instance, "headCover"),
                BaseMask = GetBaseMask(instance)
            };

            visual.HeadTransform = visual.HeadRenderer != null ? visual.HeadRenderer.transform : null;
            if (visual.HeadTransform != null)
            {
                visual.IdleHeadLocalPosition = visual.HeadTransform.localPosition;
                visual.HasIdleHeadLocalPosition = true;
            }

            if (LitFretsUntil.TryGetValue(ptr, out float litUntil))
            {
                visual.LitUntil = litUntil;
                LitFretsUntil.Remove(ptr);
            }

            if (_createOverlayRenderers || _useOverlayRenderers)
                visual.Overlay = BuildOverlayCache(visual);
            VisualCaches[ptr] = visual;
            return IsVisualCacheAlive(visual);
        }

        private static bool IsVisualCacheAlive(FretVisualCache visual)
        {
            return visual != null
                   && (visual.BaseRenderer != null
                       || visual.HeadRenderer != null
                       || visual.HalfCoverRenderer != null
                       || visual.LiftRenderer != null);
        }

        private static FretOverlayCache BuildOverlayCache(FretVisualCache visual)
        {
            if (visual == null)
                return null;

            var overlay = new FretOverlayCache
            {
                BaseRenderer = CreateOverlayRenderer("CZ_Base", visual.BaseRenderer, visual.MidSprite, 0),
                HeadRenderer = CreateOverlayRenderer("CZ_Head", visual.HeadRenderer, visual.HeadSprite, 2),
                LiftRenderer = CreateOverlayRenderer("CZ_Lift", visual.LiftRenderer, visual.LiftRenderer != null ? visual.LiftRenderer.sprite : null, 1),
                HalfCoverRenderer = CreateOverlayRenderer("CZ_HalfCover", visual.HalfCoverRenderer, visual.LipSprite, 5),
                LastHeadSprite = visual.HeadSprite,
                LastLiftVisible = false
            };

            return overlay;
        }

        private static SpriteRenderer CreateOverlayRenderer(string name, SpriteRenderer source, Sprite sprite, int sortingOrder)
        {
            if (source == null)
                return null;

            try
            {
                var go = new GameObject(name);
                go.layer = source.gameObject.layer;
                var transform = go.transform;
                transform.SetParent(source.transform, false);
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
                transform.localScale = Vector3.one;

                var renderer = go.AddComponent<SpriteRenderer>();
                renderer.sprite = sprite;
                renderer.sharedMaterial = source.sharedMaterial;
                renderer.sortingLayerID = source.sortingLayerID;
                renderer.sortingOrder = sortingOrder;
                renderer.color = White;
                renderer.flipX = false;
                renderer.flipY = false;
                renderer.maskInteraction = SpriteMaskInteraction.None;
                renderer.enabled = false;
                renderer.forceRenderingOff = true;
                ClonZonesProfiler.RecordFretOverlayCreated();
                ClonZonesProfiler.RecordFretForceRenderingOffWrite();
                return renderer;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ClonZones] Failed to create fret overlay '{name}': {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static object[] GetNeckAnimatorCache(object neck)
        {
            if (neck == null)
                return EmptyAnimators;

            var ptr = ObjectPointer(neck);
            if (ptr != IntPtr.Zero && NeckAnimatorCaches.TryGetValue(ptr, out var cached))
                return cached;

            var animators = GetMemberValue(neck, "FretAnimators")
                            ?? GetMemberValue(neck, "field_Public_BaseFretAnimator_Array_0");
            if (animators is not IEnumerable enumerable)
                return EmptyAnimators;

            var list = new List<object>(5);
            foreach (var animator in enumerable)
            {
                if (animator != null)
                    list.Add(animator);
            }

            if (list.Count == 0)
                return EmptyAnimators;

            var array = list.ToArray();
            if (ptr != IntPtr.Zero)
                NeckAnimatorCaches[ptr] = array;
            return array;
        }

        private static void RestoreHeadLayerPosition(FretVisualCache visual)
        {
            if (visual == null || !visual.HasIdleHeadLocalPosition)
                return;

            var headTransform = visual.HeadTransform;
            if (headTransform == null)
                return;

            var current = headTransform.localPosition;
            var idle = visual.IdleHeadLocalPosition;
            if (Math.Abs(current.x - idle.x) > 0.0001f
                || Math.Abs(current.y - idle.y) > 0.0001f
                || Math.Abs(current.z - idle.z) > 0.0001f)
            {
                headTransform.localPosition = idle;
                ClonZonesProfiler.RecordFretTransformWrite();
            }
        }

        private static FretRuntimeState ReadFretRuntimeState(object instance, FretVisualCache visual)
        {
            // Vanilla Pressed()/Released() write BaseFretAnimator state. Keep that
            // as the only source so ClonZones cannot drift from CH input state.
            var fretAnimator = visual?.FretAnimator ?? instance as BaseFretAnimator;
            if (fretAnimator != null)
                return new FretRuntimeState(fretAnimator.isHeld, fretAnimator.isSustaining, fretAnimator.openNoteSustaining);

            bool isHeld = TryGetBool(instance, "isHeld", out bool held) && held;
            bool isSustaining = TryGetBool(instance, "isSustaining", out bool sustaining) && sustaining;
            bool openNoteSustaining = TryGetBool(instance, "openNoteSustaining", out bool openSustaining) && openSustaining;
            return new FretRuntimeState(isHeld, isSustaining, openNoteSustaining);
        }

        private static bool IsFretHeld(object instance, FretVisualCache visual)
        {
            return ReadFretRuntimeState(instance, visual).IsHeld;
        }

        private static void MarkFretLit(object instance, float now)
        {
            var ptr = ObjectPointer(instance);
            if (ptr == IntPtr.Zero)
                return;

            float litUntil = now + FretLitSeconds;
            if (VisualCaches.TryGetValue(ptr, out var visual) && IsVisualCacheAlive(visual))
                visual.LitUntil = litUntil;
            else
                LitFretsUntil[ptr] = litUntil;
        }

        private static void MarkManualOpenPop(object instance, float now)
        {
            if (!TryGetVisualCache(instance, out var visual) || !visual.HasIdleHeadLocalPosition)
                return;

            float delta = ResolveOpenPopDelta(instance, visual);
            if (delta <= 0.0001f)
                return;

            visual.ManualOpenPopStart = now;
            visual.ManualOpenPopUntil = now + FretLitSeconds;
            visual.ManualOpenPopDelta = delta;
            ApplyManualOpenPop(visual, now);
        }

        private static float ResolveOpenPopDelta(object instance, FretVisualCache visual)
        {
            if (visual != null && visual.HasOpenPopDelta)
                return visual.OpenPopDelta;

            float delta = 0.0375f;
            if (TryGetFloat(instance, "maxHeight", out float maxHeight)
                && TryGetFloat(instance, "lowHeight", out float lowHeight))
            {
                float resolvedDelta = maxHeight - lowHeight;
                if (resolvedDelta > 0.0001f && resolvedDelta < 1f)
                    delta = resolvedDelta;
            }

            if (visual != null)
            {
                visual.OpenPopDelta = delta;
                visual.HasOpenPopDelta = true;
            }

            return delta;
        }

        private static void ExpireManualOpenPopIfNeeded(FretVisualCache visual, float now)
        {
            if (visual == null || visual.ManualOpenPopUntil <= 0f || visual.ManualOpenPopUntil > now)
                return;

            visual.ManualOpenPopUntil = 0f;
            visual.ManualOpenPopStart = 0f;
            visual.ManualOpenPopDelta = 0f;
            RestoreHeadLayerPosition(visual);
        }

        private static bool ApplyManualOpenPop(FretVisualCache visual, float now)
        {
            if (visual == null || !visual.HasIdleHeadLocalPosition || visual.ManualOpenPopUntil <= now)
                return false;

            var headTransform = visual.HeadTransform;
            if (headTransform == null)
                return false;

            float duration = Math.Max(0.0001f, visual.ManualOpenPopUntil - visual.ManualOpenPopStart);
            float remaining = Math.Max(0f, visual.ManualOpenPopUntil - now);
            float normalized = remaining / duration;

            var popPosition = visual.IdleHeadLocalPosition;
            popPosition.y += visual.ManualOpenPopDelta * normalized;

            var current = headTransform.localPosition;
            if (Math.Abs(current.x - popPosition.x) > 0.0001f
                || Math.Abs(current.y - popPosition.y) > 0.0001f
                || Math.Abs(current.z - popPosition.z) > 0.0001f)
            {
                headTransform.localPosition = popPosition;
                ClonZonesProfiler.RecordFretTransformWrite();
            }

            return true;
        }


        private static bool IsFretPopActive(FretVisualCache visual, float now, bool headAboveIdle)
        {
            if (visual != null && visual.ManualOpenPopUntil > now)
                return true;

            if (visual == null)
                return false;

            if (visual.LitUntil > 0f)
            {
                if (now < visual.LitUntil || headAboveIdle)
                    return true;

                visual.LitUntil = 0f;
            }

            return false;
        }

        private static bool IsHeadAboveIdle(FretVisualCache visual)
        {
            if (visual == null || !visual.HasIdleHeadLocalPosition)
                return false;

            var headTransform = visual.HeadTransform;
            return headTransform != null && headTransform.localPosition.y > visual.IdleHeadLocalPosition.y + 0.001f;
        }

        private static bool IsFretMaskAffected(object instance, ushort changedButtons)
        {
            if (changedButtons == ushort.MaxValue)
                return true;

            if (!TryGetVisualCache(instance, out var visual))
                return true;

            int maskBit = GetLaneMaskBit(visual);
            return maskBit >= 1
                   && maskBit <= LaneNames.Length
                   && (changedButtons & (1 << maskBit)) != 0;
        }

        private static int GetLaneMaskBit(FretVisualCache visual)
        {
            return visual.LaneIndex >= 1 && visual.LaneIndex <= LaneNames.Length
                ? visual.LaneIndex
                : visual.LaneIndex + 1;
        }

        private static bool SetRendererSprite(SpriteRenderer renderer, Sprite sprite, bool force = false)
        {
            if (renderer == null || sprite == null)
                return false;

            if (force)
            {
                renderer.sprite = sprite;
                ClonZonesProfiler.RecordFretSpriteWrite();
                return true;
            }

            var current = renderer.sprite;
            if (ReferenceEquals(current, null) || current.Pointer != sprite.Pointer)
            {
                renderer.sprite = sprite;
                ClonZonesProfiler.RecordFretSpriteWrite();
            }

            return true;
        }

        private static void NormalizeAllRendererVisuals(FretVisualCache visual, bool force = false)
        {
            if (visual == null)
                return;

            // Only normalize layers that ClonZones leaves visible in the final
            // composition. In overlay mode the vanilla base/halfCover are hidden
            // with forceRenderingOff and ClonZones overlay renderers were normalized
            // at creation, so do not keep force-writing those hidden vanilla layers.
            if (!_useOverlayRenderers)
                NormalizeRendererVisuals(visual.BaseRenderer, force);
            if (!(_useOverlayRenderers && _useLiftOverlayRenderers))
                NormalizeRendererVisuals(visual.LiftRenderer, force);
            if (!(_useOverlayRenderers && _useHeadOverlayRenderers))
                NormalizeRendererVisuals(visual.HeadRenderer, force);
            if (!_useOverlayRenderers)
                NormalizeRendererVisuals(visual.HalfCoverRenderer, force);
        }

        private static void NormalizeRendererVisuals(SpriteRenderer renderer, bool force = false)
        {
            if (renderer == null)
                return;

            if (force)
            {
                renderer.flipX = false;
                renderer.flipY = false;
                renderer.color = White;
                renderer.maskInteraction = SpriteMaskInteraction.None;
                ClonZonesProfiler.RecordFretNormalizeWrite();
                ClonZonesProfiler.RecordFretNormalizeWrite();
                ClonZonesProfiler.RecordFretNormalizeWrite();
                ClonZonesProfiler.RecordFretNormalizeWrite();
                return;
            }

            if (renderer.flipX)
            {
                renderer.flipX = false;
                ClonZonesProfiler.RecordFretNormalizeWrite();
            }
            if (renderer.flipY)
            {
                renderer.flipY = false;
                ClonZonesProfiler.RecordFretNormalizeWrite();
            }

            var color = renderer.color;
            if (Math.Abs(color.r - 1f) > 0.0001f
                || Math.Abs(color.g - 1f) > 0.0001f
                || Math.Abs(color.b - 1f) > 0.0001f
                || Math.Abs(color.a - 1f) > 0.0001f)
            {
                renderer.color = White;
                ClonZonesProfiler.RecordFretNormalizeWrite();
            }

            // Vanilla masks the pressed head inside baseMask; ClonZones disables
            // that mask, so force our replacement sprites to render normally.
            if (renderer.maskInteraction != SpriteMaskInteraction.None)
            {
                renderer.maskInteraction = SpriteMaskInteraction.None;
                ClonZonesProfiler.RecordFretNormalizeWrite();
            }
        }

        private static void ApplyAnimatedFrame(FretVisualCache visual, int frame)
        {
            if (visual == null)
                return;

            var overlay = visual.Overlay;
            Sprite baseSprite = GetFrameSprite(visual.MidFrames, visual.MidSprite, frame);
            Sprite lipSprite = GetFrameSprite(visual.LipFrames, visual.LipSprite, frame);

            if (_useOverlayRenderers && overlay != null)
            {
                SetOverlaySprite(overlay.BaseRenderer, ref overlay.LastBaseSprite, baseSprite);
                SetOverlaySprite(overlay.HalfCoverRenderer, ref overlay.LastHalfCoverSprite, lipSprite);

                Sprite headSprite = GetFrameSprite(visual.CurrentHeadFrames, visual.CurrentHeadFallback ?? visual.HeadSprite, frame);
                SetOverlaySprite(overlay.HeadRenderer, ref overlay.LastHeadSprite, headSprite);
            }
            else
            {
                SetRendererSprite(visual.BaseRenderer, baseSprite);
                SetRendererSprite(visual.HalfCoverRenderer, lipSprite);
                Sprite headSprite = GetFrameSprite(visual.CurrentHeadFrames, visual.CurrentHeadFallback ?? visual.HeadSprite, frame);
                SetRendererSprite(visual.HeadRenderer, headSprite);
            }
        }

        private static Sprite GetFrameSprite(Sprite[] frames, Sprite fallback, int frame)
        {
            if (frames == null || frames.Length == 0)
                return fallback;
            int index = frame % frames.Length;
            if (index < 0)
                index += frames.Length;
            return frames[index] ?? fallback;
        }

        private static int GetFretAnimationFrame()
        {
            return Mathf.FloorToInt(Time.time * FretAnimationFps);
        }

        private static void SetOverlaySprite(SpriteRenderer renderer, ref Sprite lastSprite, Sprite sprite)
        {
            if (renderer == null || sprite == null)
                return;

            if (lastSprite == null || lastSprite.Pointer != sprite.Pointer)
            {
                renderer.sprite = sprite;
                lastSprite = sprite;
                ClonZonesProfiler.RecordFretSpriteWrite();
            }
        }

        private static void ApplyBaseLipOverlay(FretVisualCache visual)
        {
            var overlay = visual?.Overlay;
            if (overlay == null || visual.BaseLipOverlayApplied)
                return;

            SetRendererForceRenderingOff(visual.BaseRenderer, true);
            SetRendererForceRenderingOff(visual.HalfCoverRenderer, true);
            SetOverlayRendererVisible(overlay.BaseRenderer, true);
            SetOverlayRendererVisible(overlay.HalfCoverRenderer, true);
            visual.BaseLipOverlayApplied = true;
        }

        private static bool ApplyHeadOverlay(FretVisualCache visual, Sprite targetHeadSprite)
        {
            var overlay = visual?.Overlay;
            var renderer = overlay?.HeadRenderer;
            if (renderer == null || targetHeadSprite == null)
                return false;

            if (!visual.HeadOverlayApplied)
            {
                SetRendererForceRenderingOff(visual.HeadRenderer, true);
                SetOverlayRendererVisible(renderer, true);
                visual.HeadOverlayApplied = true;
            }

            if (overlay.LastHeadSprite == null || overlay.LastHeadSprite.Pointer != targetHeadSprite.Pointer)
            {
                renderer.sprite = targetHeadSprite;
                overlay.LastHeadSprite = targetHeadSprite;
                ClonZonesProfiler.RecordFretSpriteWrite();
            }

            return true;
        }

        private static bool ApplyLiftOverlay(FretVisualCache visual, bool visible)
        {
            var overlay = visual?.Overlay;
            var renderer = overlay?.LiftRenderer;
            if (renderer == null)
                return false;

            if (!visual.LiftOverlayApplied)
            {
                SetRendererForceRenderingOff(visual.LiftRenderer, true);
                visual.LiftOverlayApplied = true;
            }

            if (overlay.LastLiftVisible != visible)
            {
                SetOverlayRendererVisible(renderer, visible);
                overlay.LastLiftVisible = visible;
            }

            return true;
        }

        private static void SetOverlayRendererVisible(SpriteRenderer renderer, bool visible)
        {
            if (renderer == null)
                return;

            renderer.enabled = visible;
            renderer.forceRenderingOff = !visible;
            ClonZonesProfiler.RecordFretEnabledWrite();
            ClonZonesProfiler.RecordFretForceRenderingOffWrite();
        }


        private static void ApplyFretLayering(FretVisualCache visual, bool isHeld = false, bool isLit = false, bool headAboveIdle = false, bool force = false)
        {
            if (visual == null)
                return;

            if (!_useOverlayRenderers)
            {
                SetRendererEnabled(visual.BaseRenderer, true, force);
                SetSortingOrder(visual.BaseRenderer, 0, force);
            }

            // Down-state fret heads can visually overlap the lift/stem area in some
            // themes. Hide the lift only while the displayed head sprite is down.png;
            // keep it visible for hit-lit pop and release/head-above-idle motion.
            bool showingDownSprite = isHeld && !isLit;
            bool stemVisible = !showingDownSprite && (isHeld || isLit || headAboveIdle);
            if (!(_useOverlayRenderers && _useLiftOverlayRenderers && ApplyLiftOverlay(visual, stemVisible)))
            {
                SetRendererEnabled(visual.LiftRenderer, stemVisible, force);
                SetSortingOrder(visual.LiftRenderer, 1, force);
            }

            if (!_useOverlayRenderers)
            {
                SetRendererEnabled(visual.HalfCoverRenderer, true, force);
                SetSortingOrder(visual.HalfCoverRenderer, 5, force);
            }

            if (!(_useOverlayRenderers && _useHeadOverlayRenderers))
            {
                SetRendererEnabled(visual.HeadRenderer, true, force);
                // Moonscraper layers cover/lip above release, press, and anim.
                SetSortingOrder(visual.HeadRenderer, 2, force);
            }

            if (!_hideVanillaWithForceRenderingOff)
            {
                SetRendererEnabled(visual.HeadLightRenderer, false, force);
                SetSortingOrder(visual.HeadLightRenderer, 2, force);
            }
        }

        private static void HideVanillaOverlayLayers(FretVisualCache visual, bool force = false)
        {
            // hook/cover/headCover/headLight are never visible in the ClonZones final
            // composition. forceRenderingOff hides them without fighting vanilla's
            // enabled state every active frame.
            if (_hideVanillaWithForceRenderingOff)
            {
                if (!visual.VanillaOverlayForceOffApplied)
                {
                    SetRendererForceRenderingOff(visual.HookRenderer, true);
                    SetRendererForceRenderingOff(visual.CoverRenderer, true);
                    SetRendererForceRenderingOff(visual.HeadCoverRenderer, true);
                    SetRendererForceRenderingOff(visual.HeadLightRenderer, true);
                    visual.VanillaOverlayForceOffApplied = true;
                }
                return;
            }

            // hook is the vanilla pick. lift is the vanilla fret stem; keep lift visible.
            SetRendererEnabled(visual.HookRenderer, false, force);
            SetRendererEnabled(visual.CoverRenderer, false, force);
            SetRendererEnabled(visual.HeadCoverRenderer, false, force);
        }

        private static void SetRendererForceRenderingOff(SpriteRenderer renderer, bool forceOff)
        {
            if (renderer == null)
                return;

            renderer.forceRenderingOff = forceOff;
            ClonZonesProfiler.RecordFretForceRenderingOffWrite();
        }


        private static void SetRendererEnabled(SpriteRenderer renderer, bool enabled, bool force = false)
        {
            if (renderer == null)
                return;

            if (force || renderer.enabled != enabled)
            {
                renderer.enabled = enabled;
                ClonZonesProfiler.RecordFretEnabledWrite();
            }
        }

        private static void SetSortingOrder(SpriteRenderer renderer, int sortingOrder, bool force = false)
        {
            if (renderer == null)
                return;

            if (force || renderer.sortingOrder != sortingOrder)
            {
                renderer.sortingOrder = sortingOrder;
                ClonZonesProfiler.RecordFretSortingWrite();
            }
        }

        private static void SetMaskDisabled(FretVisualCache visual, bool force = false)
        {
            // GH3-style static frets should not use v1.1's press/lift mask animation.
            var mask = visual?.BaseMask;
            if (mask != null && (force || mask.enabled))
            {
                mask.enabled = false;
                ClonZonesProfiler.RecordFretMaskWrite();
            }
        }

        private static SpriteMask GetBaseMask(object instance)
        {
            if (instance is GuitarFretAnimator fretAnimator)
                return fretAnimator.baseMask;

            return GetMemberValue(instance, "baseMask") as SpriteMask
                   ?? GetMemberValue(instance, "field_Public_SpriteMask_0") as SpriteMask;
        }

        private static bool TryGetLane(object instance, out int laneIndex, out string lane)
        {
            laneIndex = -1;
            lane = null;

            if (instance is BaseFretAnimator fretAnimator)
            {
                laneIndex = fretAnimator.FretIndex;
            }
            else
            {
                var value = GetMemberValue(instance, "FretIndex") ?? GetMemberValue(instance, "field_Public_Int32_0");
                if (value is int i)
                    laneIndex = i;
                else if (value != null && int.TryParse(value.ToString(), out int parsed))
                    laneIndex = parsed;
            }

            // Current CH reports guitar fret indices as 1-based lane masks:
            // 1=green, 2=red, 3=yellow, 4=blue, 5=orange. Keep a 0-based
            // fallback in case another build exposes animator array indices.
            int normalized = laneIndex >= 1 && laneIndex <= LaneNames.Length
                ? laneIndex - 1
                : laneIndex;

            if (normalized < 0 || normalized >= LaneNames.Length)
                return false;

            lane = LaneNames[normalized];
            return true;
        }

        private static SpriteRenderer GetRenderer(object instance, string semanticName)
        {
            if (instance is GuitarFretAnimator fretAnimator)
            {
                return semanticName switch
                {
                    "hook" => fretAnimator.hook,
                    "head" => fretAnimator.head,
                    "lift" => fretAnimator.lift,
                    "Base" => fretAnimator.Base,
                    "cover" => fretAnimator.cover,
                    "halfCover" => fretAnimator.halfCover,
                    "headLight" => fretAnimator.headLight,
                    "headCover" => fretAnimator.headCover,
                    _ => null
                };
            }

            var renderer = GetMemberValue(instance, semanticName) as SpriteRenderer;
            if (renderer != null)
                return renderer;

            // Current CH wrappers may preserve semantic names, but fall back to the
            // observed GuitarFretAnimator public SpriteRenderer field order:
            // hook, head, lift, Base, cover, halfCover, headLight, headCover.
            string fallbackName = semanticName switch
            {
                "hook" => "field_Public_SpriteRenderer_0",
                "head" => "field_Public_SpriteRenderer_1",
                "lift" => "field_Public_SpriteRenderer_2",
                "Base" => "field_Public_SpriteRenderer_3",
                "cover" => "field_Public_SpriteRenderer_4",
                "halfCover" => "field_Public_SpriteRenderer_5",
                "headLight" => "field_Public_SpriteRenderer_6",
                "headCover" => "field_Public_SpriteRenderer_7",
                _ => null
            };

            return fallbackName == null ? null : GetMemberValue(instance, fallbackName) as SpriteRenderer;
        }


        private static bool TryGetFloat(object instance, string name, out float result)
        {
            result = 0f;
            var value = GetMemberValue(instance, name);
            if (value is float f)
            {
                result = f;
                return true;
            }
            if (value != null && float.TryParse(value.ToString(), out float parsed))
            {
                result = parsed;
                return true;
            }
            return false;
        }

        private static bool TryGetBool(object instance, string name, out bool result)
        {
            result = false;

            // Avoid reflection FieldInfo.GetValue for BaseFretAnimator's hot public
            // state bits. These are the exact fields vanilla Update() reads.
            if (instance is BaseFretAnimator fretAnimator)
            {
                switch (name)
                {
                    case "isHeld":
                        result = fretAnimator.isHeld;
                        return true;
                    case "isSustaining":
                        result = fretAnimator.isSustaining;
                        return true;
                    case "openNoteSustaining":
                        result = fretAnimator.openNoteSustaining;
                        return true;
                }
            }

            var value = GetMemberValue(instance, name);
            if (value == null)
            {
                string fallbackName = name switch
                {
                    "isHeld" => "field_Public_Boolean_0",
                    "isSustaining" => "field_Public_Boolean_1",
                    "openNoteSustaining" => "field_Public_Boolean_2",
                    _ => null
                };
                if (fallbackName != null)
                    value = GetMemberValue(instance, fallbackName);
            }
            if (value is bool b)
            {
                result = b;
                return true;
            }
            return false;
        }

        private static object GetMemberValue(object instance, string name)
        {
            if (instance == null || string.IsNullOrEmpty(name))
                return null;

            var type = instance.GetType();
            var key = new MemberCacheKey(type, name);

            if (!MemberLookupCache.TryGetValue(key, out var member))
            {
                if (MissingMemberLookupCache.Contains(key))
                    return null;

                member = ResolveMemberRecursive(type, name);
                if (member == null)
                {
                    MissingMemberLookupCache.Add(key);
                    return null;
                }

                MemberLookupCache[key] = member;
            }

            return member switch
            {
                PropertyInfo property => property.GetValue(instance),
                FieldInfo field => field.GetValue(instance),
                _ => null
            };
        }

        private static MemberInfo ResolveMemberRecursive(Type type, string name)
        {
            while (type != null)
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                    return property;

                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                    return field;

                type = type.BaseType;
            }
            return null;
        }

        private static void ReadFretSettings(string assetRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(assetRoot))
                    return;

                string path = Path.Combine(assetRoot, "settings.ini");
                if (!File.Exists(path))
                    return;

                bool inFret = false;
                foreach (var rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                        continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inFret = string.Equals(line, "[fret]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inFret)
                        continue;

                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                        continue;

                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    if (TryParseBool(value, out bool parsed))
                    {
                        if (string.Equals(key, "create_overlay_renderers", StringComparison.OrdinalIgnoreCase))
                            _createOverlayRenderers = parsed;
                        else if (string.Equals(key, "use_overlay_renderers", StringComparison.OrdinalIgnoreCase))
                            _useOverlayRenderers = parsed;
                        else if (string.Equals(key, "use_head_overlay_renderers", StringComparison.OrdinalIgnoreCase))
                            _useHeadOverlayRenderers = parsed;
                        else if (string.Equals(key, "use_lift_overlay_renderers", StringComparison.OrdinalIgnoreCase))
                            _useLiftOverlayRenderers = parsed;
                        else if (string.Equals(key, "hide_vanilla_with_force_rendering_off", StringComparison.OrdinalIgnoreCase))
                            _hideVanillaWithForceRenderingOff = parsed;
                        else if (string.Equals(key, "skip_base_mask_disable_when_overlay", StringComparison.OrdinalIgnoreCase))
                            _skipBaseMaskDisableWhenOverlay = parsed;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ClonZones] Failed to read fret settings: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool TryParseBool(string value, out bool result)
        {
            result = false;
            if (string.IsNullOrEmpty(value))
                return false;

            if (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }

            if (value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value.Equals("0", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }

            return false;
        }

        private static IntPtr ObjectPointer(object instance)
        {
            return instance switch
            {
                UnityEngine.Object obj when obj != null => obj.Pointer,
                Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj when obj != null => obj.Pointer,
                _ => IntPtr.Zero
            };
        }

        private static Type FindIl2CppType(string typeName)
        {
            var type = Type.GetType("Il2Cpp." + typeName + ", Il2CppCloneHero", false);
            if (type != null)
                return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType("Il2Cpp." + typeName, false) ?? assembly.GetType(typeName, false);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static MethodInfo ResolveStartMethod(Type fretType)
        {
            return fretType.GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
        }

        private static MethodInfo ResolveNamedNoArgVoidMethod(Type fretType, string name)
        {
            return fretType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        }

        private static MethodInfo ResolvePlayMethod(Type fretType)
        {
            return fretType.GetMethod("Play", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool), typeof(bool) }, null)
                   ?? fretType.GetMethod("Method_Public_Virtual_Void_Boolean_Boolean_0", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool), typeof(bool) }, null);
        }

        private static bool InstallFretStateHooks(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            var patched = new HashSet<MethodInfo>();

            var pressed = ResolveNamedNoArgVoidMethod(_fretType, "Pressed");
            var released = ResolveNamedNoArgVoidMethod(_fretType, "Released");
            if (pressed != null)
                patched.Add(pressed);
            if (released != null)
                patched.Add(released);

            // Current v1.1 IL2CPP wrappers expose Pressed/Released as obfuscated
            // overrides. Patch only the no-arg void methods declared by
            // BaseFretAnimator, not arbitrary GuitarFretAnimator helpers.
            foreach (var method in ResolveBaseNoArgFretStateOverrides(_fretType))
                patched.Add(method);

            if (patched.Count == 0)
            {
                log.Warning("[ClonZones] Could not resolve GuitarFretAnimator Pressed/Released overrides; using UpdateFrets reskin fallback.");
                return false;
            }

            foreach (var method in patched)
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(GuitarFretPatch), nameof(FretStatePostfix)));

            log.Msg($"[ClonZones] Installed GuitarFretAnimator state postfixes on {patched.Count} exact Pressed/Released override(s): {string.Join(", ", patched.Select(m => m.Name))}");
            return patched.Count >= 2;
        }

        private static IEnumerable<MethodInfo> ResolveBaseNoArgFretStateOverrides(Type fretType)
        {
            var baseType = fretType?.BaseType;
            if (baseType == null)
                return Array.Empty<MethodInfo>();

            var declaredNoArgVirtuals = fretType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(void)
                            && m.GetParameters().Length == 0
                            && m.IsVirtual)
                .ToArray();

            var baseNoArgStateMethods = baseType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(void)
                            && m.GetParameters().Length == 0
                            && (m.IsAbstract || m.IsVirtual))
                .ToArray();

            var overrides = declaredNoArgVirtuals
                .Where(method => IsOverrideOfBaseStateMethod(method, baseType, baseNoArgStateMethods))
                .ToArray();
            if (overrides.Length > 0)
                return overrides;

            // Some IL2CPP interop wrapper builds do not preserve enough override
            // metadata for GetBaseDefinition(). In the current wrapper metadata, the
            // two Pressed/Released implementations are the only declared no-arg void
            // virtual methods that reuse a base slot; helper/update methods are NewSlot.
            var reuseSlotNoArgVirtuals = declaredNoArgVirtuals
                .Where(method => (method.Attributes & MethodAttributes.NewSlot) == 0)
                .ToArray();

            return reuseSlotNoArgVirtuals.Length == 2
                ? reuseSlotNoArgVirtuals
                : Array.Empty<MethodInfo>();
        }

        private static bool IsOverrideOfBaseStateMethod(MethodInfo method, Type baseType, MethodInfo[] baseNoArgStateMethods)
        {
            MethodInfo baseDefinition;
            try
            {
                baseDefinition = method.GetBaseDefinition();
            }
            catch
            {
                return false;
            }

            if (baseDefinition == null
                || baseDefinition == method
                || baseDefinition.DeclaringType != baseType)
            {
                return false;
            }

            return baseNoArgStateMethods.Any(baseMethod => baseMethod == baseDefinition || baseMethod.MetadataToken == baseDefinition.MetadataToken);
        }

        private static MethodInfo ResolveUpdateFretsMethod(Type neckType)
        {
            return neckType?.GetMethod("UpdateFrets", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(ushort) }, null)
                   // IDA: v1.1 latest UpdateFrets is GuitarNeckController$$____________6445882944,
                   // the second public virtual ushort method. _0 is EndFretHeldState and
                   // must not be used as an UpdateFrets fallback.
                   ?? neckType?.GetMethod("Method_Public_Virtual_Void_UInt16_1", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(ushort) }, null);
        }

        private static MethodInfo ResolvePlayFretMethod(Type neckType)
        {
            return neckType?.GetMethod("PlayFret", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? neckType?.GetMethod("Method_Public_Virtual_Void_ObjectPublicObInObDoSiDoUIInBoInUnique_Boolean_Boolean_0", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
    }
}
