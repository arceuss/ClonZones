// Only the host boundary is stubbed. Gameplay snapshots are synthetic, textures/fonts
// are placeholders, and no Unity/IL2CPP renderer or visibility flag is exercised here.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HarmonyLib
{
    public class Harmony { public void Patch(MethodInfo m, HarmonyMethod prefix = null, HarmonyMethod postfix = null) {} }
    public class HarmonyMethod { public HarmonyMethod(Type t, string method) {} }
    public static class AccessTools { public static MethodInfo Method(Type t, string n) => t.GetMethod(n); }
}
namespace MelonLoader
{
    public static class MelonLogger
    {
        public class Instance { public void Msg(string s) {} public void Warning(string s) {} public void Error(string s) {} }
    }
    public static class MelonPreferences
    {
        public class Entry<T> { public T Value; }
        public class Category { public Entry<T> CreateEntry<T>(string name, T value, string description = null) => new() { Value = value }; }
        public static Category CreateCategory(string name) => new();
    }
}
namespace UnityEngine
{
    public class Object { public IntPtr m_CachedPtr = (IntPtr)1; }
    public class GameObject : Object { public int layer; }
    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
        public static bool operator ==(Rect a, Rect b) => a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;
        public static bool operator !=(Rect a, Rect b) => !(a == b);
        public override bool Equals(object o) => o is Rect r && this == r;
        public override int GetHashCode() => HashCode.Combine(x, y, width, height);
    }
    public class Camera : Object { public Rect pixelRect = new(0, 0, 1280, 720); public GameObject gameObject = new(); }
    public class Shader : Object { public static Shader Find(string name) => new(); }
    public static class SortingLayer
    {
        public static int[] GetSortingLayerIDsInternal() => Array.Empty<int>();
        public static int GetLayerValueFromID(int id) => 0;
        public static int NameToID(string name) => 0;
        public static string IDToName(int id) => "";
    }
    public static class Time { public static float unscaledDeltaTime, unscaledTime; }
    public static class Input
    {
        public static bool RightDown, RightUp;
        public static bool GetMouseButtonDown(int b) => b == 1 && RightDown;
        public static bool GetMouseButtonUp(int b) => b == 1 && RightUp;
    }
}
namespace Il2CppInterop.Runtime
{
    // Synthetic field handles for this host fixture, not evidence of the game's ABI.
    public static class IL2CPP { public static uint il2cpp_field_get_offset(IntPtr field) => (uint)field.ToInt64(); }
}
namespace Il2Cpp
{
    public class NativeObject : UnityEngine.Object
    {
        public IntPtr Pointer = (IntPtr)1;
        public T TryCast<T>() where T : class => this as T;
    }
    public class GuitarNeckController : NativeObject {}
    public class GameManager : NativeObject
    {
        public double songTime;
        public bool isPaused, isSongPlaying = true, isSongOver;
        public int actualPlayerCount = 1;
        public double songLength = 300, practiceStartTime;
        public int practiceSectionStart;
        public long practiceStartTick, practiceEndTick;
        public StarProgress starProgress = new();
        public ScoreManager scoreManager;
        public GameManager() { scoreManager = new ScoreManager { starProgress = starProgress }; }
        public static readonly IntPtr NativeFieldInfoPtr_starProgress = (IntPtr)0x40;
        public static readonly IntPtr NativeFieldInfoPtr_scoreManager = (IntPtr)0xB8;
        public static readonly IntPtr NativeFieldInfoPtr_songLength = (IntPtr)0x50;
        public static readonly IntPtr NativeFieldInfoPtr_practiceStartTime = (IntPtr)0xD0;
        public static readonly IntPtr NativeFieldInfoPtr_practiceSectionStart = (IntPtr)0xC8;
        public static readonly IntPtr NativeFieldInfoPtr_practiceStartTick = (IntPtr)0xE0;
        public static readonly IntPtr NativeFieldInfoPtr_practiceEndTick = (IntPtr)0xF0;
    }
    public class ScoreManager : NativeObject
    {
        public StarProgress starProgress;
        public static readonly IntPtr NativeFieldInfoPtr_starProgress = (IntPtr)0x90;
    }
    public class StarProgress : NativeObject
    {
        // 7 is the .ctor terminal (0x1802E0990); -1 fraction is its pre-update sentinel.
        public int field_Private_Int32_0, field_Private_Int32_1 = 7;
        public float field_Private_Single_4;
        public static readonly IntPtr NativeFieldInfoPtr_field_Private_Int32_0 = (IntPtr)0x58;
        public static readonly IntPtr NativeFieldInfoPtr_field_Private_Int32_1 = (IntPtr)0x5C;
        public static readonly IntPtr NativeFieldInfoPtr_field_Private_Single_4 = (IntPtr)0x78;
    }
    public class Object2PublicBoSiInSiDoStSiStStUnique
    {
        public bool prop_Boolean_0;
    }
    public static class ObjectPublicAbstractSealedBoObObObObObObObObObUnique
    {
        public static Object2PublicBoSiInSiDoStSiStStUnique field_Public_Static_Object2PublicBoSiInSiDoStSiStStUnique_27 = new();
    }
    public class LeaderboardsOnlineManager : NativeObject
    {
        public static LeaderboardsOnlineManager instance = new();
        public bool field_Private_Boolean_0;
        public static bool prop_Boolean_1 => instance.field_Private_Boolean_0;
    }
    public class GlobalVariables : NativeObject
    {
        public static GlobalVariables instance = new();
        public bool isPracticeEnabled;
        public bool failed;
        public static readonly IntPtr NativeFieldInfoPtr_isPracticeEnabled = (IntPtr)0x72;
    }
    public class ObjectPublicAbstractDoBoDoInBoObDoInSiBoUnique : NativeObject
    {
        public int prop_Int32_2, field_Public_Int32_1, field_Protected_Int32_0 = 1, field_Public_Int32_10;
        public float field_Protected_Single_0 = 1f, prop_Single_0;
        public bool field_Public_Boolean_0;
        public long field_Public_Int64_0, field_Public_Int64_2 = 2000, field_Public_Int64_3 = 1000;
    }
    public class BasePlayer : NativeObject
    {
        public GameManager gameManager = new();
        public NativeObject neckController = new GuitarNeckController();
        public Camera mainCamera = new();
        public ObjectPublicAbstractDoBoDoInBoObDoInSiBoUnique engine = new();
    }
    public class BeatRenderer : NativeObject
    {
        public BasePlayer field_Private_BasePlayer_0;
        public void Start() {} public void OnDisable() {}
    }
}
namespace ClonZones
{
    internal enum BenchmarkEvent { HudRebuild = 1 }
    internal static class ClonZonesBenchmark
    {
        public static void RecordSongTime(double songTime) {}
        public static void Mark(BenchmarkEvent kind) {}
    }
    internal enum ProfileScope { HudDraw, HudUpload, HudVisibility }
    internal static class ClonZonesProfiler
    {
        public static long BeginScope(ProfileScope scope) => 0;
        public static void EndScope(ProfileScope scope, long start) {}
    }
    // the host fixture has no IL2CPP heap; the real UnityIcalls only replaces boxing invokes.
    internal static class UnityIcalls
    {
        public static float UnscaledDeltaTime => UnityEngine.Time.unscaledDeltaTime;
        public static bool MouseButtonDown(int button) => UnityEngine.Input.GetMouseButtonDown(button);
        public static bool MouseButtonUp(int button) => UnityEngine.Input.GetMouseButtonUp(button);
    }
    internal static class Gh3HighwayRenderer
    {
        public static Gh3HudLayerSlot? UnderSidesSlot(Il2Cpp.BeatRenderer beats) => null;
    }
    internal sealed class Gh3HudAssets : IDisposable
    {
        public static readonly string[] ImageNames = Array.Empty<string>();
        public static readonly string[] FontNames = Array.Empty<string>();
        public bool IsComplete { get; private set; } = true;
        public string MissingSummary { get; } = "";
        public object Atlas { get; } = null;
        public PresentationStyle Style { get; }
        public string AssetRoot { get; }
        public long Generation { get; }

        public Gh3HudAssets(PresentationStyle style, string assetRoot, long generation,
            IReadOnlyList<string> imageNames, IReadOnlyList<string> fontNames,
            MelonLoader.MelonLogger.Instance log)
        {
            Style = style; AssetRoot = assetRoot; Generation = generation;
        }

        public Gh3HudRegion Region(string name) => new(128, 128, 0, 0, 1, 1);
        private static readonly Gh3HudFont StubFont = Gh3HudFont.Parse("test-only",
            "page 64 64\nlineheight 35\nspacewidth 4\nyorigin 0\npre 0\npost 0\nglyph 0 0 0 23 30 0 0 0\n", 64, 64);
        public Gh3HudFont Font(string name) => StubFont;
        public void Dispose() { IsComplete = false; }
    }
    internal sealed class Gh3HudMesh : IGh3HudQuadSink, IDisposable
    {
        private readonly Camera _camera;
        public bool ProjectionDirty;
        public int Uploads;
        public Rect Viewport { get; private set; }
        public Gh3HudMesh(string name, object atlas, Shader shader, Camera camera, int layer, int sorting, int order, int queue, int quads, Shader additiveShader = null)
        { _camera = camera; }
        public bool RefreshViewport()
        {
            bool changed = ProjectionDirty || Viewport != _camera.pixelRect;
            Viewport = _camera.pixelRect; ProjectionDirty = false; return changed;
        }
        public void Begin() {}
        public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float u0, float v0, float u1, float v1, Color32 color, Gh3HudBlend blend = Gh3HudBlend.Alpha) {}
        public void Upload() { Uploads++; }
        public void Dispose() {}
    }
    internal sealed class Gh3HudVisibility
    {
        public int Reassertions, Discoveries;
        public Gh3HudVisibility(Il2Cpp.BasePlayer player, MelonLoader.MelonLogger.Instance log) {}
        public void Hide() {}
        public void Reassert(bool rediscover = false) { Reassertions++; if (rediscover) Discoveries++; }
        public void Restore() {}
    }
    internal static class Gh3HudBreakHook
    {
        public static int MissCount, GhostCount;
        public static int Misses(Il2Cpp.BasePlayer p) => MissCount;
        public static int Ghosts(Il2Cpp.BasePlayer p) => GhostCount;
        public static void Clear() { MissCount = GhostCount = 0; }
    }
    internal static class Gh3HudDiagnostics
    {
        public static void Configure() {}
        public static void Snapshot(Gh3HudSnapshot s, Gh3HudMesh m, int count, Gh3HudStateBridge b, MelonLoader.MelonLogger.Instance log) {}
        public static void RendererInventory(MelonLoader.MelonLogger.Instance log) {}
    }
}
