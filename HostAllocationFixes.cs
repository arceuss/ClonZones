using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;

namespace ClonZones
{
    /// <summary>
    /// Per-frame managed garbage made by the mod host, not by ClonZones' own code. Measured with
    /// EventPipe allocation ticks on CH 1.1.0.6142 / Unity 2022.3.62f2, MelonLoader 0.8.0-ci.2526,
    /// Il2CppInterop 1.5.1-ci.845 (steady play, drunken2): Il2CppInterop's injected-class trampolines
    /// call ClassInjectorBase.GetMonoObjectFromIl2CppPointer for every callback into MelonLoader's
    /// component (Update, LateUpdate, FixedUpdate, OnGUI), and it wraps the native class struct on the
    /// heap just to read its instance size: 29% of all managed allocation. That garbage paces the
    /// CoreCLR GCs, and every GC is followed by a finalizer burst that stalls the main thread.
    /// The fix reads the size directly; it checks the host layout first and compares the result with
    /// the original on a live object, and anything unexpected leaves the host code alone.
    /// (MelonLoader's own per-frame string + delegate in ComponentSiblingFix.FindMethod, the other big
    /// one, can't be fixed from a mod: its support module is [assembly: PatchShield].)
    /// </summary>
    internal static class HostAllocationFixes
    {
        private static bool _installed;

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            if (_installed) return;
            _installed = true;
            InstallInjectedLookup(harmony, log);
        }

        private static Type FindType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            return null;
        }

        // ---- Il2CppInterop ClassInjectorBase.GetMonoObjectFromIl2CppPointer ----

        private static void InstallInjectedLookup(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            MethodInfo lookup = typeof(Il2CppInterop.Runtime.Runtime.ClassInjectorBase).GetMethod("GetMonoObjectFromIl2CppPointer",
                BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(IntPtr) }, null);
            object component = FindType("MelonLoader.Support.Main")?.GetField("component", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
            if (lookup == null || lookup.ReturnType != typeof(object) || component is not Il2CppObjectBase injected)
            {
                log.Msg("[ClonZones] Injected-object lookup fix not installed: Il2CppInterop/MelonLoader layout differs.");
                return;
            }
            try
            {
                // compare the raw handle with Il2CppInterop's own before touching it: a wrong layout must
                // fail the check, not hand GCHandle.FromIntPtr a garbage value.
                IntPtr pointer = injected.Pointer;
                IntPtr handle = ReadHandle(pointer);
                if (handle == IntPtr.Zero || handle != Il2CppInterop.Runtime.Runtime.ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(pointer)
                    || !ReferenceEquals(GCHandle.FromIntPtr(handle).Target, lookup.Invoke(null, new object[] { pointer }))
                    || !ReferenceEquals(GCHandle.FromIntPtr(handle).Target, component))
                {
                    log.Msg("[ClonZones] Injected-object lookup fix not installed: result differs from Il2CppInterop's.");
                    return;
                }
            }
            catch (Exception error) when (error is EntryPointNotFoundException || error is TargetInvocationException)
            {
                log.Msg($"[ClonZones] Injected-object lookup fix not installed: {error.GetType().Name}.");
                return;
            }
            harmony.Patch(lookup, prefix: new HarmonyMethod(typeof(HostAllocationFixes), nameof(LookupPrefix)));
            log.Msg("[ClonZones] Injected-object lookup reads the class size directly (was a heap wrapper per MelonLoader callback).");
        }

        // Il2CppInterop appends InjectedClassData { IntPtr managedGcHandle } to every injected class, so the
        // handle sits IntPtr.Size before the end of the instance (ClassInjectorBase.GetInjectedData).
        // il2cpp_class_instance_size returns the same Il2CppClass.instance_size field it wraps to read.
        private static IntPtr ReadHandle(IntPtr pointer)
        {
            IntPtr klass = IL2CPP.il2cpp_object_get_class(pointer);
            return Marshal.ReadIntPtr(pointer, IL2CPP.il2cpp_class_instance_size(klass) - IntPtr.Size);
        }

        // a zero handle is Il2CppInterop's delegate m_target fallback; let the original handle it.
        private static bool LookupPrefix(IntPtr pointer, ref object __result)
        {
            if (pointer == IntPtr.Zero) return true;
            IntPtr handle = ReadHandle(pointer);
            if (handle == IntPtr.Zero) return true;
            __result = GCHandle.FromIntPtr(handle).Target;
            return false;
        }
    }
}
