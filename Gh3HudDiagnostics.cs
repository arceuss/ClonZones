using MelonLoader;

namespace ClonZones
{
    /// <summary>
    /// Optional periodic snapshot log (about every two seconds) of the Clone Hero values
    /// the GH3 HUD presents, for binding validation. Off unless ClonZonesHud.Diagnostics
    /// is true. No scene inventory: generic Unity object lookups crashed the IL2CPP player.
    /// </summary>
    internal static class Gh3HudDiagnostics
    {
        public static bool Enabled { get; private set; }
        private static int _frames;

        public static void Configure()
        {
            Enabled = MelonPreferences.CreateCategory("ClonZonesHud").CreateEntry("Diagnostics", false).Value
                || System.Environment.GetEnvironmentVariable("CLONZONES_HUD_DIAGNOSTICS") == "1";
        }

        public static void Snapshot(in Gh3HudSnapshot s, Gh3HudMesh mesh, int scriptCount, Gh3HudStateBridge bridge, MelonLogger.Instance log)
        {
            if (!Enabled || (_frames++ % 600) != 0) return;
            log.Msg($"[ClonZones][Hud] t={s.SongTime:F2} paused={s.Paused} playing={s.SongPlaying} over={s.SongOver} score={s.Score} streak={s.Streak} mult={s.Multiplier} health={s.Health:F3} sp={s.StarPower:F3} active={s.StarPowerActive} ready={s.StarPowerReady} scripts={scriptCount} quads={mesh.QuadCount} viewport={mesh.Viewport.width}x{mesh.Viewport.height} scale={mesh.Scale:F4}");
            log.Msg("[ClonZones][Hud] raw " + bridge.RawFields());
        }
    }
}
