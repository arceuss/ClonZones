using System.IO;
using MelonLoader;

[assembly: MelonInfo(typeof(ClonZones.Core), "ClonZones", "2.0.0", "arceus", null)]
[assembly: MelonGame("srylain Inc.", "Clone Hero")]

namespace ClonZones
{
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("[ClonZones] Initializing.");

            var assetRoot = LocateAssetRoot();
            if (assetRoot == null)
            {
                LoggerInstance.Error("[ClonZones] Could not find ClonZones/Assets, GH3Zones/Assets, or Assets beside the mod DLL. Note heads will not be replaced.");
                return;
            }
            LoggerInstance.Msg($"[ClonZones] Asset root: {assetRoot}");
            ClonZonesProfiler.Initialize(assetRoot, LoggerInstance);

            long coreProfile = ClonZonesProfiler.BeginScope(ProfileScope.CoreInitialize);

            // Load sprites once. Banks persist for the process lifetime.
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.LoadNoteBank);
            NoteHeadSpriteBank.LoadAll(assetRoot, LoggerInstance);
            ClonZonesProfiler.EndScope(ProfileScope.LoadNoteBank, profileStart);

            profileStart = ClonZonesProfiler.BeginScope(ProfileScope.LoadFretBank);
            FretSpriteBank.LoadAll(assetRoot, LoggerInstance);
            ClonZonesProfiler.EndScope(ProfileScope.LoadFretBank, profileStart);

            profileStart = ClonZonesProfiler.BeginScope(ProfileScope.LoadFlameBank);
            FlameSpriteBank.LoadAll(assetRoot, LoggerInstance);
            ClonZonesProfiler.EndScope(ProfileScope.LoadFlameBank, profileStart);

            profileStart = ClonZonesProfiler.BeginScope(ProfileScope.LoadSustainBank);
            SustainFxBank.LoadAll(assetRoot, LoggerInstance);
            ClonZonesProfiler.EndScope(ProfileScope.LoadSustainBank, profileStart);

            // Install Harmony patches.
            profileStart = ClonZonesProfiler.BeginScope(ProfileScope.InstallNote);
            GuitarNoteHeadPatch.Install(HarmonyInstance, LoggerInstance);
            ClonZonesProfiler.EndScope(ProfileScope.InstallNote, profileStart);

            GuitarFretPatch.Configure(assetRoot, LoggerInstance);
            profileStart = ClonZonesProfiler.BeginScope(ProfileScope.InstallFret);
            GuitarFretPatch.Install(HarmonyInstance, LoggerInstance);
            ClonZonesProfiler.EndScope(ProfileScope.InstallFret, profileStart);

            profileStart = ClonZonesProfiler.BeginScope(ProfileScope.InstallFlame);
            GuitarFlamePatch.Install(HarmonyInstance, LoggerInstance);
            ClonZonesProfiler.EndScope(ProfileScope.InstallFlame, profileStart);

            profileStart = ClonZonesProfiler.BeginScope(ProfileScope.InstallSustain);
            SustainFxPatch.Install(HarmonyInstance, LoggerInstance);
            ClonZonesProfiler.EndScope(ProfileScope.InstallSustain, profileStart);

            ClonZonesProfiler.EndScope(ProfileScope.CoreInitialize, coreProfile);

            LoggerInstance.Msg("[ClonZones] Initialized.");
        }

        public override void OnUpdate()
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.CoreUpdate);
            GuitarNoteHeadPatch.BeginFrame();
            GuitarFretPatch.TickAnimations();
            GuitarFlamePatch.Tick();
            if (ClonZonesProfiler.Enabled)
                ClonZonesProfiler.TickFrame();
            ClonZonesProfiler.EndScope(ProfileScope.CoreUpdate, profileStart);
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.CoreScene);
            // Conservatively stop new substitutions the moment a scene transition begins.
            // The sprite bank and already-applied states are untouched.
            GuitarNoteHeadPatch.SetMode(RenderPatchMode.Inactive);
            GuitarFretPatch.ClearRuntimeState();
            GuitarFlamePatch.SetActive(false);
            GuitarFlamePatch.ClearRuntimeState();
            SustainFxPatch.SetActive(false);
            SustainFxPatch.ClearRuntimeState();
            ClonZonesProfiler.EndScope(ProfileScope.CoreScene, profileStart);
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.CoreScene);
            GuitarNoteHeadPatch.BeginSongTransition($"scene unloaded: {sceneName}");
            GuitarFretPatch.ClearRuntimeState();
            GuitarFlamePatch.SetActive(false);
            GuitarFlamePatch.ClearRuntimeState();
            SustainFxPatch.SetActive(false);
            SustainFxPatch.ClearRuntimeState();
            ClonZonesProfiler.EndScope(ProfileScope.CoreScene, profileStart);
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            long profileStart = ClonZonesProfiler.BeginScope(ProfileScope.CoreScene);
            bool isGameplay = string.Equals(sceneName, "Gameplay", System.StringComparison.OrdinalIgnoreCase);
            GuitarNoteHeadPatch.SetMode(isGameplay ? RenderPatchMode.Gameplay : RenderPatchMode.Inactive);
            GuitarFlamePatch.SetActive(isGameplay);
            SustainFxPatch.SetActive(isGameplay);
            LoggerInstance.Msg($"[ClonZones] Scene initialized: '{sceneName}' (buildIndex={buildIndex}) → mode={(isGameplay ? "Gameplay" : "Inactive")}");
            ClonZonesProfiler.EndScope(ProfileScope.CoreScene, profileStart);
        }

        public override void OnApplicationQuit()
        {
            GuitarNoteHeadPatch.SetMode(RenderPatchMode.ShuttingDown);
            GuitarFretPatch.ClearRuntimeState();
            GuitarFlamePatch.SetActive(false);
            GuitarFlamePatch.ClearRuntimeState();
            SustainFxPatch.SetActive(false);
            SustainFxPatch.ClearRuntimeState();
        }

        public override void OnDeinitializeMelon()
        {
            GuitarNoteHeadPatch.SetMode(RenderPatchMode.ShuttingDown);
            GuitarFretPatch.ClearRuntimeState();
            GuitarFlamePatch.SetActive(false);
            GuitarFlamePatch.ClearRuntimeState();
            SustainFxPatch.SetActive(false);
            SustainFxPatch.ClearRuntimeState();
        }

        // ─────────────────────────────────────────────────────────────────────

        private string LocateAssetRoot()
        {
            var modDir = Path.GetDirectoryName(typeof(Core).Assembly.Location);
            if (string.IsNullOrEmpty(modDir)) return null;

            // <ModsDir>/ClonZones/current.txt holds the name of the active asset
            // subfolder (default "Assets"), so multiple themes can sit side by side
            // and be switched without touching the mod.
            var clonZonesDir = Path.Combine(modDir, "ClonZones");
            var currentTxt = Path.Combine(clonZonesDir, "current.txt");
            if (File.Exists(currentTxt))
            {
                try
                {
                    foreach (var rawLine in File.ReadAllLines(currentTxt))
                    {
                        var name = rawLine.Trim();
                        if (name.Length == 0 || name.StartsWith(";") || name.StartsWith("#"))
                            continue;

                        var candidate = Path.Combine(clonZonesDir, name);
                        if (Directory.Exists(candidate))
                        {
                            LoggerInstance.Msg($"[ClonZones] current.txt selected asset folder: {name}");
                            return candidate;
                        }

                        LoggerInstance.Warning($"[ClonZones] current.txt names missing folder '{name}'; falling back to Assets.");
                        break;
                    }
                }
                catch (System.Exception ex)
                {
                    LoggerInstance.Warning($"[ClonZones] Could not read current.txt: {ex.Message}");
                }
            }

            // Preferred: <ModsDir>/ClonZones/Assets
            var preferred = Path.Combine(clonZonesDir, "Assets");
            if (Directory.Exists(preferred)) return preferred;

            // Back-compat fallback: <ModsDir>/GH3Zones/Assets
            var gh3Zones = Path.Combine(modDir, "GH3Zones", "Assets");
            if (Directory.Exists(gh3Zones)) return gh3Zones;

            // Fallback: <ModsDir>/Assets
            var fallback = Path.Combine(modDir, "Assets");
            if (Directory.Exists(fallback)) return fallback;

            return null;
        }
    }
}
