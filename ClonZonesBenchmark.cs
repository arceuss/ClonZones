using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using UnityEngine;

namespace ClonZones
{
    /// <summary>Per-update attribution flags; a set bit means the work happened in that update.</summary>
    [Flags]
    internal enum BenchmarkEvent
    {
        HudRebuild = 1,
        Discovery = 2,
        MeshGrowth = 4,
        MaterialArray = 8,
        SustainFrames = 16,
        FadeMaterial = 32,
    }

    // opt-in LateUpdate samples, not input latency or display-present measurements.
    internal static class ClonZonesBenchmark
    {
        private struct Sample
        {
            public long Ticks, Allocated, Highway, Sustain, Flame, Hud, HudDraw, HudUpload, Visibility, Discovery;
            // Unity's IL2CPP (Boehm) heap, which CoreCLR's counters don't see; a drop in used size is a collection.
            public long Il2CppUsed, Il2CppHeap;
            public int Frame, Rendered, Gen0, Gen1, Gen2, Events;
            public sbyte WillRender;
            public double SongTime;
        }

        private static Sample[] _samples;
        private static Sample _current;
        private static string _output;
        private static long _start, _duration, _armed, _delay;
        private static DateTime _sceneStartUtc;
        private static int _count;
        private static int _targetFrameRate, _vSyncCount, _screenWidth, _screenHeight;
        private static int _warmRenderFrameInterval, _finalRenderFrameInterval;
        private static string _graphicsDevice, _graphicsApi;
        private static bool _started, _finished, _exported, _willRenderAvailable, _il2cppHeapAvailable;
        // per-scope profiler totals at capture start/end, so the export covers only the window.
        private static int[] _callsAtStart, _callsAtEnd;
        private static long[] _ticksAtStart, _ticksAtEnd, _bytesAtStart, _bytesAtEnd, _il2cppAtStart, _il2cppAtEnd;
        public static bool Enabled => _samples != null && !_finished;
        public static bool Configured => _samples != null;
        public static bool CaptureScopes { get; private set; }

        public static void Initialize()
        {
            _output = Environment.GetEnvironmentVariable("CLONZONES_BENCHMARK_OUTPUT");
            if (string.IsNullOrEmpty(_output)) return;
            _output = Path.GetFullPath(_output);
            Directory.CreateDirectory(Path.GetDirectoryName(_output));
            CaptureScopes = Environment.GetEnvironmentVariable("CLONZONES_BENCHMARK_SCOPES") == "1";
            _samples = new Sample[262144];
            // touch capture pages before gameplay rather than faulting them in during a trial.
            for (int i = 0; i < _samples.Length; i += 32) _samples[i].Ticks = -1;
            _duration = Stopwatch.Frequency * 100L;
            // CLONZONES_BENCHMARK_START_SECONDS moves the 100 s window later into the song
            // (e.g. onto the sustain/star-power sections); default is gameplay scene init.
            if (double.TryParse(Environment.GetEnvironmentVariable("CLONZONES_BENCHMARK_START_SECONDS"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double delay) && delay > 0)
                _delay = (long)(delay * Stopwatch.Frequency);
            int scopes = (int)ProfileScope.Count;
            _callsAtStart = new int[scopes]; _callsAtEnd = new int[scopes];
            _ticksAtStart = new long[scopes]; _ticksAtEnd = new long[scopes];
            _bytesAtStart = new long[scopes]; _bytesAtEnd = new long[scopes];
            _il2cppAtStart = new long[scopes]; _il2cppAtEnd = new long[scopes];
            var assembly = typeof(Core).Assembly;
            var context = AssemblyLoadContext.GetLoadContext(assembly);
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(_output + ".host.json", JsonSerializer.Serialize(new
            {
                ProcessId = process.Id,
                MeasurementKind = "LateUpdate wall intervals and inclusive managed scopes; not display presents",
                ProcessStartUtc = process.StartTime.ToUniversalTime(),
                ObservedUtc = DateTime.UtcNow,
                Framework = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Assembly = assembly.Location,
                LoadContext = context?.Name,
                Collectible = context?.IsCollectible,
                StopwatchFrequency = Stopwatch.Frequency,
                CaptureScopes,
                SampleCapacity = _samples.Length,
                CaptureSeconds = 100,
                // these are observations, not proof that a method received optimized PGO code.
                DotnetTieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                ComplusTieredCompilation = Environment.GetEnvironmentVariable("COMPlus_TieredCompilation"),
                DotnetTieredPGO = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                ComplusTieredPGO = Environment.GetEnvironmentVariable("COMPlus_TieredPGO"),
                TieredCompilationConfig = AppContext.GetData("System.Runtime.TieredCompilation"),
                TieredPGOConfig = AppContext.GetData("System.Runtime.TieredPGO")
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static void SceneInitialized(string sceneName)
        {
            if (!Enabled || _started || _armed != 0 || sceneName != "Gameplay") return;
            // Diagnostic column only: a stripped player ICall must not stop the capture.
            try { _ = UnityEngine.Rendering.OnDemandRendering.willCurrentFrameRender; _willRenderAvailable = true; }
            catch (Exception error) when (error is NotSupportedException || error is Il2CppInterop.Runtime.Il2CppException) { _willRenderAvailable = false; }
            try { _ = Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_get_used_size(); _il2cppHeapAvailable = true; }
            catch (EntryPointNotFoundException) { _il2cppHeapAvailable = false; }
            _armed = Stopwatch.GetTimestamp();
            _sceneStartUtc = DateTime.UtcNow;
            if (_delay == 0) Begin(_armed);
        }

        private static void Begin(long now)
        {
            _start = now;
            _started = true;
            ClonZonesProfiler.CopyCounters(_callsAtStart, _ticksAtStart, _bytesAtStart, _il2cppAtStart);
        }

        public static void RecordSongTime(double songTime)
        {
            if (Enabled && _started) _current.SongTime = songTime;
        }

        public static void Mark(BenchmarkEvent kind)
        {
            if (Enabled && _started) _current.Events |= (int)kind;
        }

        public static void RecordDiscovery(long elapsed)
        {
            if (Enabled && _started) _current.Discovery += elapsed;
        }

        public static void RecordScope(ProfileScope scope, long elapsed)
        {
            if (!Enabled || !_started) return;
            switch (scope)
            {
                case ProfileScope.Gh3HighwayUpdate: _current.Highway += elapsed; break;
                case ProfileScope.Gh3SustainUpdate: _current.Sustain += elapsed; break;
                case ProfileScope.FlameTick: _current.Flame += elapsed; break;
                case ProfileScope.Gh3HudUpdate: _current.Hud += elapsed; break;
                case ProfileScope.HudDraw: _current.HudDraw += elapsed; break;
                case ProfileScope.HudUpload: _current.HudUpload += elapsed; break;
                case ProfileScope.HudVisibility: _current.Visibility += elapsed; break;
            }
        }

        public static void EndUpdate()
        {
            if (!Enabled) return;
            long now = Stopwatch.GetTimestamp();
            if (!_started)
            {
                // waiting out CLONZONES_BENCHMARK_START_SECONDS; nothing accumulates until Begin.
                if (_armed != 0 && now - _armed >= _delay) Begin(now);
                return;
            }
            _current.Ticks = now - _start;
            _current.Allocated = GC.GetAllocatedBytesForCurrentThread();
            _current.Frame = Time.frameCount;
            _current.Rendered = Time.renderedFrameCount;
            _current.Gen0 = GC.CollectionCount(0);
            _current.Gen1 = GC.CollectionCount(1);
            _current.Gen2 = GC.CollectionCount(2);
            if (_il2cppHeapAvailable)
            {
                _current.Il2CppUsed = Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_get_used_size();
                _current.Il2CppHeap = Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_get_heap_size();
            }
            // Whether Unity will render this update (render interval > 1 skips some); -1 when unavailable.
            _current.WillRender = _willRenderAvailable ? (sbyte)(UnityEngine.Rendering.OnDemandRendering.willCurrentFrameRender ? 1 : 0) : (sbyte)-1;
            if (_warmRenderFrameInterval == 0 && now - _start >= Stopwatch.Frequency * 30L)
                _warmRenderFrameInterval = UnityEngine.Rendering.OnDemandRendering.renderFrameInterval;
            _samples[_count++] = _current;
            _current = default;
            if (_count == _samples.Length || now - _start >= _duration)
            {
                // Capture over. The samples stay in memory; nothing is formatted or written
                // until the gameplay scene ends or the game quits (Export).
                _finished = true;
                CaptureSettings();
            }
        }

        private static void CaptureSettings()
        {
            _targetFrameRate = Application.targetFrameRate;
            _vSyncCount = QualitySettings.vSyncCount;
            _screenWidth = Screen.width; _screenHeight = Screen.height;
            _finalRenderFrameInterval = UnityEngine.Rendering.OnDemandRendering.renderFrameInterval;
            _graphicsDevice = SystemInfo.graphicsDeviceName;
            _graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            ClonZonesProfiler.CopyCounters(_callsAtEnd, _ticksAtEnd, _bytesAtEnd, _il2cppAtEnd);
        }

        /// <summary>
        /// Writes the capture once, on the main thread, after play: gameplay scene unload or
        /// quit. A capture cut short by either is written and marked truncated.
        /// </summary>
        public static void Export(string reason)
        {
            if (_samples == null || _exported || !_started || _count == 0) return;
            _exported = true;
            bool truncated = !_finished;
            if (truncated) { _finished = true; CaptureSettings(); }
            try
            {
                using (var writer = new StreamWriter(_output + ".csv"))
                {
                    writer.WriteLine("ticks,allocated,frame,rendered,gen0,gen1,gen2,highway,sustain,flame,hud,hudDraw,hudUpload,visibility,songTime,discovery,events,willRender,il2cppUsed,il2cppHeap");
                    for (int i = 0; i < _count; i++)
                    {
                        Sample s = _samples[i];
                        writer.WriteLine(FormattableString.Invariant($"{s.Ticks},{s.Allocated},{s.Frame},{s.Rendered},{s.Gen0},{s.Gen1},{s.Gen2},{s.Highway},{s.Sustain},{s.Flame},{s.Hud},{s.HudDraw},{s.HudUpload},{s.Visibility},{s.SongTime:R},{s.Discovery},{s.Events},{s.WillRender},{s.Il2CppUsed},{s.Il2CppHeap}"));
                    }
                }
                string assembly = typeof(Core).Assembly.Location;
                string sha;
                using (var stream = File.OpenRead(assembly))
                using (var hash = System.Security.Cryptography.SHA256.Create())
                    sha = Convert.ToHexString(hash.ComputeHash(stream));
                int session;
                using (var self = Process.GetCurrentProcess()) session = self.SessionId;
                // inclusive managed scopes inside the window only; zero unless scopes were captured.
                var scopes = new System.Collections.Generic.Dictionary<string, object>();
                for (int i = 0; i < _callsAtEnd.Length; i++)
                {
                    int calls = _callsAtEnd[i] - _callsAtStart[i];
                    if (calls == 0) continue;
                    long bytes = _bytesAtEnd[i] - _bytesAtStart[i];
                    long il2cpp = _il2cppAtEnd[i] - _il2cppAtStart[i];
                    scopes[((ProfileScope)i).ToString()] = new { Calls = calls, TotalMs = (_ticksAtEnd[i] - _ticksAtStart[i]) * 1000.0 / Stopwatch.Frequency,
                        AllocatedBytes = bytes, BytesPerCall = bytes / (double)calls, Il2CppBytes = il2cpp, Il2CppBytesPerCall = il2cpp / (double)calls };
                }
                File.WriteAllText(_output + ".complete.json", JsonSerializer.Serialize(new
                {
                    Samples = _count,
                    SceneStartUtc = _sceneStartUtc,
                    StartDelaySeconds = _delay / (double)Stopwatch.Frequency,
                    SceneToCaptureSeconds = (_start - _armed) / (double)Stopwatch.Frequency,
                    Scopes = scopes,
                    DurationSeconds = _samples[_count - 1].Ticks / (double)Stopwatch.Frequency,
                    CaptureSecondsLimit = _duration / (double)Stopwatch.Frequency,
                    CapacityReached = _count == _samples.Length,
                    Truncated = truncated,
                    ExportReason = reason,
                    ExportDelaySeconds = (Stopwatch.GetTimestamp() - _start) / (double)Stopwatch.Frequency,
                    CommandLine = Environment.GetCommandLineArgs(),
                    ModSha256 = sha,
                    TargetFrameRate = _targetFrameRate, VSyncCount = _vSyncCount,
                    ScreenWidth = _screenWidth, ScreenHeight = _screenHeight,
                    WarmRenderFrameInterval = _warmRenderFrameInterval, FinalRenderFrameInterval = _finalRenderFrameInterval,
                    WillRenderAvailable = _willRenderAvailable,
                    GraphicsDevice = _graphicsDevice, GraphicsApi = _graphicsApi,
                    WindowsSessionId = session,
                }));
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Error("[ClonZones] benchmark output failed: " + ex);
            }
        }
    }
}
