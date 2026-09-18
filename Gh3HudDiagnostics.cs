using System.Text;
using MelonLoader;
using UnityEngine;

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
        private static bool _inventoryDone;

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

        /// <summary>
        /// One-time scene inventory (a few seconds into gameplay) of every renderer whose
        /// hierarchy path looks like a HUD widget, to identify CH visuals that are not owned by
        /// the components the mod hides. Non-generic lookup; diagnostics only.
        /// </summary>
        public static void RendererInventory(MelonLogger.Instance log)
        {
            if (!Enabled || _inventoryDone || _frames < 400) return;
            _inventoryDone = true;
            var d = new StringBuilder("[ClonZones][Hud] renderer inventory\n");
            var all = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Renderer>(), true);
            int shown = 0;
            for (int i = 0; i < all.Length && shown < 400; i++)
            {
                var r = all[i].TryCast<Renderer>();
                if (r == null) continue;
                Transform t = r.transform;
                string path = t.name;
                for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
                string lower = path.ToLowerInvariant();
                if (!(lower.Contains("mult") || lower.Contains("combo") || lower.Contains("health") || lower.Contains("life") || lower.Contains("sp") ||
                      lower.Contains("star") || lower.Contains("score") || lower.Contains("hud") || lower.Contains("ghost") || lower.Contains("leader"))) continue;
                shown++;
                d.Append(path).Append(" | ").Append(r.GetIl2CppType().Name).Append(" active=").Append(r.gameObject.activeInHierarchy)
                 .Append(" enabled=").Append(r.enabled).Append(" forceOff=").Append(r.forceRenderingOff)
                 .Append(" layer=").Append(r.gameObject.layer).Append(" sort=").Append(r.sortingLayerID).Append('/').Append(r.sortingOrder).Append('\n');
            }
            log.Msg(d.ToString());
        }
    }
}
