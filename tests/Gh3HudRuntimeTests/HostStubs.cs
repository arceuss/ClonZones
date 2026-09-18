// Only the host boundary is stubbed. Gameplay snapshots are synthetic, textures/fonts
// are placeholders, and no Unity/IL2CPP renderer or visibility flag is exercised here.
using System;
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
    public class Object {}
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
    public static class SortingLayer { public static int NameToID(string name) => 0; }
    public static class Time { public static float unscaledDeltaTime, unscaledTime; }
    public static class Input
    {
        public static bool RightDown, RightUp;
        public static bool GetMouseButtonDown(int b) => b == 1 && RightDown;
        public static bool GetMouseButtonUp(int b) => b == 1 && RightUp;
    }
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
    internal static class Gh3HudAssets
    {
        public static bool IsComplete => true;
        public static string MissingSummary => "";
        public static object Atlas => null;
        public static Gh3HudRegion Region(string name) => new(128, 128, 0, 0, 1, 1);
        private static readonly Gh3HudFont StubFont = Gh3HudFont.Parse("test-only",
            "page 64 64\nlineheight 35\nspacewidth 4\nyorigin 0\npre 0\npost 0\nglyph 0 0 0 23 30 0 0 0\n", 64, 64);
        public static Gh3HudFont Font(string name) => StubFont;
    }
    internal sealed class Gh3HudMesh : IGh3HudQuadSink, IDisposable
    {
        private readonly Camera _camera;
        public bool ProjectionDirty;
        public int Uploads;
        public Rect Viewport { get; private set; }
        public Gh3HudMesh(string name, object atlas, Shader shader, Camera camera, int layer, int sorting, int order, int queue, int quads)
        { _camera = camera; }
        public bool RefreshViewport()
        {
            bool changed = ProjectionDirty || Viewport != _camera.pixelRect;
            Viewport = _camera.pixelRect; ProjectionDirty = false; return changed;
        }
        public void Begin() {}
        public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float u0, float v0, float u1, float v1, Color32 color) {}
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
    internal static class Gh3HudDiagnostics
    {
        public static void Configure() {}
        public static void Snapshot(Gh3HudSnapshot s, Gh3HudMesh m, int count, Gh3HudStateBridge b, MelonLoader.MelonLogger.Instance log) {}
        public static void RendererInventory(MelonLoader.MelonLogger.Instance log) {}
    }
}
