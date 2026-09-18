using System;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    internal static class Gh3HighwayDiagnostics
    {
        private static MelonLogger.Instance _log;
        private static bool _enabled;

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            _log = log;
            _enabled = MelonPreferences.CreateCategory("ClonZonesHighway").CreateEntry("Diagnostics", false).Value
                || Environment.GetEnvironmentVariable("CLONZONES_HIGHWAY_DIAGNOSTICS") == "1";
            if (_enabled)
                harmony.Patch(AccessTools.Method(typeof(BeatRenderer), nameof(BeatRenderer.Start)),
                    postfix: new HarmonyMethod(typeof(Gh3HighwayDiagnostics), nameof(Started)));
        }

        private static void Started(BeatRenderer __instance)
        {
            BasePlayer player = __instance.field_Private_BasePlayer_0;
            if (player == null) return;
            Camera camera = player.mainCamera;
            GuitarNoteRenderer notes = player.GetComponent<GuitarNoteRenderer>();
            StringBuilder dump = new StringBuilder();
            dump.AppendLine("[ClonZones][Highway] diagnostic field inventory");
            dump.AppendLine($"beat strike={__instance.beatStrikeLine} width={__instance.beatWidth} offset={__instance.beatZOffset} speed={__instance.field_Private_Single_0} far={__instance.field_Private_Double_0} close={__instance.field_Private_Double_1}");
            if (notes != null)
                dump.AppendLine($"notes speed={notes.noteSpeed} strike={notes.strikeLine} far={notes.noteZPosFarLimit} close={notes.noteZPosCloseLimit} scale={notes.noteScaleX},{notes.noteScaleY}");
            if (notes != null)
            {
                var sustains = notes.Sustains;
                for (int i = 0; sustains != null && i < sustains.Length; i++)
                    DumpSprite(dump, $"Sustains[{i}]", sustains[i]);
                var bodies = notes.field_Private_Il2CppReferenceArray_1_SpriteRenderer_0;
                var glows = notes.field_Private_Il2CppReferenceArray_1_SpriteRenderer_1;
                if (bodies != null && bodies.Length > 0) DumpSprite(dump, "sustain body", bodies[0].sprite);
                if (glows != null && glows.Length > 0)
                {
                    DumpSprite(dump, "sustain glow", glows[0].sprite);
                    dump.AppendLine($"glow shader={glows[0].sharedMaterial.shader.name} queue={glows[0].sharedMaterial.renderQueue}");
                }
                dump.AppendLine($"sustain UVs={notes.field_Private_Vector4_0};{notes.field_Private_Vector4_1};{notes.field_Private_Vector4_2}");
            }
            if (camera != null)
                dump.AppendLine($"camera position={camera.transform.position} rotation={camera.transform.eulerAngles} fov={camera.fieldOfView} aspect={camera.aspect} rect={camera.pixelRect}");
            DumpTransform(dump, player.transform, camera);
            BaseNeckController neck = player.neckController;
            if (neck != null)
            {
                DumpTransform(dump, neck.transform, camera);
                var frets = neck.FretAnimators;
                for (int i = 0; frets != null && i < frets.Length; i++)
                    if (frets[i] != null) DumpTransform(dump, frets[i].transform, camera);
                var strings = neck.fretStrings;
                for (int i = 0; strings != null && i < strings.Length; i++)
                    if (strings[i] != null) DumpTransform(dump, strings[i].transform, camera);
            }
            var bars = __instance.field_Protected_Il2CppReferenceArray_1_SpriteRenderer_0;
            if (bars != null && bars.Length > 0 && bars[0] != null)
            {
                SpriteRenderer bar = bars[0];
                DumpTransform(dump, bar.transform, camera);
                dump.AppendLine($"bar sorting={bar.sortingLayerID}/{bar.sortingOrder} shader={bar.sharedMaterial.shader.name} queue={bar.sharedMaterial.renderQueue}");
            }
            if (player.trackSidebarLeft != null) DumpTransform(dump, player.trackSidebarLeft.transform, camera);
            if (player.trackSidebarRight != null) DumpTransform(dump, player.trackSidebarRight.transform, camera);
            var timing = __instance.field_Private_ObjectPublicLi1DoObLi1InDoInLiUnique_0;
            if (timing != null)
            {
                var times = timing.field_Public_List_1_Double_0;
                var weights = timing.field_Public_List_1_EnumPublicSealedvaMeBe4vBeUnique_0;
                var ticks = timing.field_Public_List_1_Int64_0;
                var other = timing.field_Public_List_1_Double_1;
                dump.AppendLine($"beat records={times.Count} weights={weights.Count} ticks={ticks.Count} other={other.Count}");
                for (int i = 0; i < Math.Min(12, times.Count); i++)
                    dump.AppendLine($"beat {i}: seconds={times[i]} weight={weights[i]} tick={(i < ticks.Count ? ticks[i] : -1)} other={(i < other.Count ? other[i] : -1)}");
            }
            _log.Msg(dump.ToString());
        }

        private static void DumpSprite(StringBuilder dump, string label, Sprite sprite)
        {
            if (sprite == null) return;
            dump.AppendLine($"{label}: {sprite.name} rect={sprite.rect} texture={sprite.texture.name}/{sprite.texture.width}x{sprite.texture.height} ppu={sprite.pixelsPerUnit} pivot={sprite.pivot} border={sprite.border}");
        }

        private static void DumpTransform(StringBuilder dump, Transform transform, Camera camera)
        {
            Vector3 screen = camera != null ? camera.WorldToScreenPoint(transform.position) : Vector3.zero;
            dump.AppendLine($"{transform.name}: local={transform.localPosition} world={transform.position} rotation={transform.eulerAngles} scale={transform.lossyScale} screen={screen}");
        }
    }
}
