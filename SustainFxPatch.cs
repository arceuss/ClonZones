using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// Replaces Clone Hero's sustain_spark_anim Animator sprites with GH3 sustain_hold.
    /// Also keeps the companion sustain particles neutral so they do not tint the art.
    /// </summary>
    internal static class SustainFxPatch
    {
        private static MelonLogger.Instance _log;
        private static bool _active;
        private static readonly Dictionary<IntPtr, NeckSustainCache> PatchedNecks = new();
        // Color.white is a boxing invoke in the interop; RefreshNeckColors runs on every hit.
        private static readonly Color White = Color.white;
        private static Il2CppReferenceArray<Sprite> _sustainFrames;
        // Prewarm (6B): the frame cache is built from the neck's real spark template before the
        // first hit; the first real patch still checks it saw the same template object.
        private static readonly Dictionary<IntPtr, Il2Cpp.BeatRenderer> PendingPrewarm = new();
        private static readonly List<IntPtr> PrewarmDone = new();
        private static IntPtr _framesTemplate;
        private static bool _framesUnverified;
        private static bool _warnedPrefixError;
        private static bool _warnedPostfixError;
        private static bool _warnedPrewarmError;

        private sealed class NeckSustainCache
        {
            public SpriteRenderer[] Renderers;
        }

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            _log = log;
            if (!SustainFxBank.IsReady)
                return;

            var neckType = FindIl2CppType("GuitarNeckController");
            var playFret = ResolvePlayFretMethod(neckType);
            if (playFret == null)
            {
                log.Warning("[ClonZones] Could not resolve GuitarNeckController.PlayFret; sustain sparks unchanged.");
                return;
            }

            harmony.Patch(playFret,
                prefix: new HarmonyMethod(typeof(SustainFxPatch), nameof(PlayFretPrefix)),
                postfix: new HarmonyMethod(typeof(SustainFxPatch), nameof(PlayFretPostfix)));
            log.Msg($"[ClonZones] Installed sustain FX PlayFret prefix/postfix: {playFret.Name}");

            var start = AccessTools.Method(typeof(Il2Cpp.BeatRenderer), nameof(Il2Cpp.BeatRenderer.Start));
            if (start != null)
                harmony.Patch(start, postfix: new HarmonyMethod(typeof(SustainFxPatch), nameof(BeatStarted)));
            else
                log.Warning("[ClonZones] BeatRenderer.Start unavailable; sustain frames are built on the first hit.");
        }

        public static void SetActive(bool active)
        {
            _active = active;
        }

        public static void ClearRuntimeState()
        {
            PatchedNecks.Clear();
            _sustainFrames = null;
            PendingPrewarm.Clear();
            _framesTemplate = IntPtr.Zero;
            _framesUnverified = false;
        }

        private static void BeatStarted(Il2Cpp.BeatRenderer __instance)
        {
            if (__instance != null) PendingPrewarm[__instance.Pointer] = __instance;
        }

        /// <summary>LateUpdate: build the frame cache once a pending neck's real template exists.</summary>
        public static void Tick()
        {
            if (PendingPrewarm.Count == 0 || !_active || !SustainFxBank.IsReady)
                return;

            PrewarmDone.Clear();
            foreach (var pair in PendingPrewarm)
            {
                try
                {
                    if (_sustainFrames != null || TryPrewarm(pair.Value))
                        PrewarmDone.Add(pair.Key);
                }
                catch (Exception ex)
                {
                    // leave the cache empty so the first hit builds it exactly as before.
                    PrewarmDone.Add(pair.Key);
                    if (!_warnedPrewarmError)
                    {
                        _warnedPrewarmError = true;
                        _log?.Warning($"[ClonZones] sustain FX prewarm failed, frames will be built on the first hit: {ex.Message}");
                    }
                }
            }
            foreach (IntPtr key in PrewarmDone)
                PendingPrewarm.Remove(key);
        }

        private static bool TryPrewarm(Il2Cpp.BeatRenderer beats)
        {
            if (beats == null)
                return true;
            var neck = beats.field_Private_BasePlayer_0?.neckController?.TryCast<Il2Cpp.GuitarNeckController>();
            if (neck == null)
                return beats.field_Private_BasePlayer_0?.neckController != null; // not a guitar neck: nothing to build
            // same animator the first PatchNeck reaches: sparks first, then sparksOverlay.
            Il2Cpp.Animator animator = FirstAnimator(neck, "sparks") ?? FirstAnimator(neck, "sparksOverlay");
            if (animator == null)
                return false;
            Sprite template = SelectTemplate(animator, animator.GetComponent<SpriteRenderer>());
            // no template yet: wait, never build against the fallback pivot/ppu.
            if (template == null)
                return false;
            BuildFrames(template);
            _framesUnverified = _sustainFrames != null;
            return true;
        }

        private static Il2Cpp.Animator FirstAnimator(object neck, string memberName)
        {
            if (GetMember(neck, memberName) is not IEnumerable enumerable)
                return null;
            foreach (var item in enumerable)
            {
                var animator = item as Il2Cpp.Animator;
                if (animator != null)
                    return animator;
            }
            return null;
        }

        private static void PlayFretPrefix(object __instance)
        {
            if (!_active || !SustainFxBank.IsReady || __instance == null)
                return;
            // interactive use has started: the first patch builds or verifies the cache, so stop retrying.
            if (PendingPrewarm.Count != 0)
                PendingPrewarm.Clear();

            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.SustainPrefix);
            try
            {
                PatchNeck(__instance, false);
            }
            catch (Exception ex)
            {
                if (!_warnedPrefixError)
                {
                    _warnedPrefixError = true;
                    _log?.Warning($"[ClonZones] sustain FX patch error: {ex.Message}");
                }
            }
            finally
            {
                ClonZonesProfiler.EndScope(ProfileScope.SustainPrefix, profileStart);
            }
        }

        private static void PlayFretPostfix(object __instance)
        {
            if (!_active || !SustainFxBank.IsReady || __instance == null)
                return;

            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.SustainPostfix);
            try
            {
                RefreshNeckColors(__instance);
            }
            catch (Exception ex)
            {
                if (!_warnedPostfixError)
                {
                    _warnedPostfixError = true;
                    _log?.Warning($"[ClonZones] sustain FX post patch error: {ex.Message}");
                }
            }
            finally
            {
                ClonZonesProfiler.EndScope(ProfileScope.SustainPostfix, profileStart);
            }
        }

        private static void PatchNeck(object neck, bool forceRefresh)
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.SustainPatchNeck);
            try
            {
                var ptr = ObjectPointer(neck);
                if (!forceRefresh && ptr != IntPtr.Zero && PatchedNecks.ContainsKey(ptr))
                    return;

                // sustain_spark_anim is CH's color + overlay animator pair. Do not
                // patch SPSparks; that has different star-power/end-phrase show rules.
                var renderers = new List<SpriteRenderer>(8);
                PatchAnimatorArray(neck, "sparks", renderers);
                PatchAnimatorArray(neck, "sparksOverlay", renderers);

                // 2guitargame/v1 behavior: sustain_hold replaces the sustain_spark
                // Animator art. sustainParticles are separate particle systems and keep
                // their vanilla texture/activation.

                if (ptr != IntPtr.Zero)
                    PatchedNecks[ptr] = new NeckSustainCache { Renderers = renderers.ToArray() };
            }
            finally
            {
                ClonZonesProfiler.EndScope(ProfileScope.SustainPatchNeck, profileStart);
            }
        }

        private static void RefreshNeckColors(object neck)
        {
            var ptr = ObjectPointer(neck);
            if (ptr == IntPtr.Zero || !PatchedNecks.TryGetValue(ptr, out var cache) || cache.Renderers == null)
                return;

            for (int i = 0; i < cache.Renderers.Length; i++)
            {
                var renderer = cache.Renderers[i];
                if (UnityIcalls.Alive(renderer))
                    renderer.color = White;
            }
        }

        private static int PatchAnimatorArray(object neck, string memberName, List<SpriteRenderer> renderers)
        {
            var animators = GetMember(neck, memberName);
            if (animators is not IEnumerable enumerable)
                return 0;

            int count = 0;
            foreach (var item in enumerable)
            {
                var animator = item as Il2Cpp.Animator;
                if (animator == null)
                    continue;

                if (PatchSparkAnimator(animator, renderers))
                    count++;
            }
            return count;
        }

        private static bool PatchSparkAnimator(Il2Cpp.Animator animator, List<SpriteRenderer> renderers)
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.SustainPatchAnimator);
            try
            {
                var renderer = animator.GetComponent<SpriteRenderer>();
                var frames = GetSustainFrames(animator, renderer);
                if (frames == null || frames.Length == 0)
                    return false;

                // Preserve CH's transform, activation, play calls, startOffset, speed,
                // playLength, and SpriteRenderer state. Only swap the frame list/colors.
                animator.Sprites = frames;
                animator.defaultColor = Color.white;
                animator.alternateColor = Color.white;

                if (renderer != null)
                {
                    renderer.color = Color.white;
                    renderers.Add(renderer);
                }

                return true;
            }
            finally
            {
                ClonZonesProfiler.EndScope(ProfileScope.SustainPatchAnimator, profileStart);
            }
        }

        private static Il2CppReferenceArray<Sprite> GetSustainFrames(Il2Cpp.Animator animator, SpriteRenderer renderer)
        {
            if (_sustainFrames == null || _framesUnverified)
            {
                Sprite template = SelectTemplate(animator, renderer);
                // the first real patch is where the frames used to be built. keep a prewarmed set
                // only if this is the same template object, otherwise build from this one as before.
                if (_sustainFrames == null || ObjectPointer(template) != _framesTemplate)
                {
                    // a rejected prewarm set was never handed to an animator; free it here.
                    var rejected = _framesUnverified ? _sustainFrames : null;
                    BuildFrames(template);
                    if (rejected != null)
                        for (int i = 0; i < rejected.Length; i++)
                            UnityEngine.Object.Destroy(rejected[i]);
                }
                _framesUnverified = false;
            }
            return _sustainFrames;
        }

        private static Sprite SelectTemplate(Il2Cpp.Animator animator, SpriteRenderer renderer)
        {
            Sprite template = null;
            var current = animator.Sprites;
            if (current != null && current.Length > 0)
                template = current[0];
            if (template == null && renderer != null)
                template = renderer.sprite;
            return template;
        }

        private static void BuildFrames(Sprite template)
        {
            var frames = SustainFxBank.CreateFramesLike(template) ?? SustainFxBank.Frames;
            _sustainFrames = frames != null ? new Il2CppReferenceArray<Sprite>(frames) : null;
            _framesTemplate = frames != null ? ObjectPointer(template) : IntPtr.Zero;
        }


        private static object GetMember(object instance, string name)
        {
            if (instance == null)
                return null;

            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
                return property.GetValue(instance);

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(instance);
        }

        private static IntPtr ObjectPointer(object instance)
        {
            return instance switch
            {
                UnityEngine.Object obj when UnityIcalls.Alive(obj) => obj.Pointer,
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

        private static MethodInfo ResolvePlayFretMethod(Type neckType)
        {
            return neckType?.GetMethod("PlayFret", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? neckType?.GetMethod("Method_Public_Virtual_Void_ObjectPublicObInObDoSiDoUIInBoInUnique_Boolean_Boolean_0", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
    }
}
