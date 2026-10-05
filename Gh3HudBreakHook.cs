using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace ClonZones
{
    /// <summary>
    /// Counts the engine's combo-break callbacks per player. The engine's miss path
    /// (0x20F4B60) ends in BasePlayer.___(bool overstrum, bool, int combo, bool firstBreak)
    /// (0x20A070); the first flag is true for an overstrum/ghost input and false for a
    /// missed note (verified live: flag1=True on overstrums, False on misses). CH keeps no
    /// running counter of either. Read-only: nothing is changed.
    /// </summary>
    internal static class Gh3HudBreakHook
    {
        private sealed class Counts { public int Misses, Ghosts; }
        private static readonly Dictionary<IntPtr, Counts> Players = new();
        private static MelonLogger.Instance _log;
        private static int _logged;

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            _log = log;
            harmony.Patch(AccessTools.Method(typeof(BasePlayer), nameof(BasePlayer.Method_Public_Virtual_New_Void_Boolean_Boolean_Int32_Boolean_0)),
                postfix: new HarmonyMethod(typeof(Gh3HudBreakHook), nameof(Broke)));
        }

        private static void Broke(BasePlayer __instance, bool param_1, bool param_2, int param_3, bool param_4)
        {
            if (!Players.TryGetValue(__instance.Pointer, out Counts c)) Players[__instance.Pointer] = c = new Counts();
            if (param_1) c.Ghosts++; else c.Misses++;
            if (_logged < 12 && Gh3HudDiagnostics.Enabled)
            {
                _logged++;
                _log.Msg($"[ClonZones][Hud] combo break: flag1={param_1} flag2={param_2} combo={param_3} first={param_4}");
            }
        }

        public static int Misses(BasePlayer player) => UnityIcalls.Alive(player) && Players.TryGetValue(player.Pointer, out Counts c) ? c.Misses : 0;
        public static int Ghosts(BasePlayer player) => UnityIcalls.Alive(player) && Players.TryGetValue(player.Pointer, out Counts c) ? c.Ghosts : 0;

        public static void Clear() { Players.Clear(); _logged = 0; }
    }
}
