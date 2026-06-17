using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    internal enum ProfileScope
    {
        CoreInitialize = 0,
        CoreUpdate = 1,
        CoreScene = 2,
        LoadNoteBank = 3,
        LoadFretBank = 4,
        LoadFlameBank = 5,
        LoadSustainBank = 6,
        InstallNote = 7,
        InstallFret = 8,
        InstallFlame = 9,
        InstallSustain = 10,
        NoteBeginFrame = 11,
        NoteSetup = 12,
        NoteApply = 13,
        FretStart = 14,
        FretState = 15,
        FretPlay = 16,
        FretPlayFret = 17,
        FretUpdate = 18,
        FretUpdateFrets = 19,
        FretApply = 20,
        FretApplyActive = 21,
        FretApplyIdle = 22,
        FretCacheBuild = 23,
        FlamePlayFret = 24,
        FlameTick = 25,
        FlamePrepare = 26,
        FlameCacheBuild = 27,
        FlameCreate = 28,
        SustainPrefix = 29,
        SustainPostfix = 30,
        SustainPatchNeck = 31,
        SustainPatchAnimator = 32,
        Count = 33
    }

    internal static class ClonZonesProfiler
    {
        private static readonly string[] ScopeNames =
        {
            "coreInit",
            "coreUpdate",
            "coreScene",
            "loadNoteBank",
            "loadFretBank",
            "loadFlameBank",
            "loadSustainBank",
            "installNote",
            "installFret",
            "installFlame",
            "installSustain",
            "noteBeginFrame",
            "noteSetup",
            "noteApply",
            "fretStart",
            "fretState",
            "fretPlay",
            "fretPlayFret",
            "fretUpdate",
            "fretUpdateFrets",
            "fretApply",
            "fretApplyActive",
            "fretApplyIdle",
            "fretCacheBuild",
            "flamePlayFret",
            "flameTick",
            "flamePrepare",
            "flameCacheBuild",
            "flameCreate",
            "sustainPrefix",
            "sustainPostfix",
            "sustainPatchNeck",
            "sustainPatchAnimator"
        };

        private static readonly long[] TotalTicks = new long[(int)ProfileScope.Count];
        private static readonly long[] MaxTicks = new long[(int)ProfileScope.Count];
        private static readonly int[] Calls = new int[(int)ProfileScope.Count];
        private static readonly StringBuilder ReportBuilder = new(2048);

        private static MelonLogger.Instance _log;
        private static long _nextReportTicks;
        private static float _intervalSeconds = 5f;
        private static int _frames;
        private static float _totalFrameMs;
        private static float _maxFrameMs;

        // Classifies per-frame fret Update re-skins as "settled" (fret fully at rest,
        // so the work is skippable) vs "active". Measures how much of the fret Update
        // load a settle-gate could remove before we add the gate.
        private static int _fretRestSettledCalls;
        private static int _fretRestActiveCalls;
        private static int _fretApplyActiveCalls;
        private static int _fretApplyIdleCalls;
        private static int _fretSpriteWrites;
        private static int _fretEnabledWrites;
        private static int _fretSortingWrites;
        private static int _fretMaskWrites;
        private static int _fretNormalizeWrites;
        private static int _fretTransformWrites;
        private static int _fretBlockHeld;
        private static int _fretBlockSustain;
        private static int _fretBlockOpenSustain;
        private static int _fretBlockManualPop;
        private static int _fretBlockLit;
        private static int _fretBlockHeadMoving;
        private static int _fretOverlaysCreated;
        private static int _fretForceRenderingOffWrites;

        public static bool Enabled { get; private set; }

        public static void Initialize(string assetRoot, MelonLogger.Instance log)
        {
            _log = log;
            Enabled = false;
            _intervalSeconds = 5f;
            ReadSettings(assetRoot);

            if (!Enabled)
                return;

            ResetCounters();
            _nextReportTicks = Stopwatch.GetTimestamp() + SecondsToTicks(_intervalSeconds);
            _log?.Msg($"[ClonZones][PROFILE] Enabled; interval={_intervalSeconds:0.###}s. Set [profiling] enabled=false in settings.ini to disable.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long BeginScope(ProfileScope scope)
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EndScope(ProfileScope scope, long startTicks)
        {
            if (startTicks == 0 || !Enabled)
                return;

            int index = (int)scope;
            long elapsed = Stopwatch.GetTimestamp() - startTicks;
            Calls[index]++;
            TotalTicks[index] += elapsed;
            if (elapsed > MaxTicks[index])
                MaxTicks[index] = elapsed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretRest(bool atRest)
        {
            if (!Enabled)
                return;

            if (atRest)
                _fretRestSettledCalls++;
            else
                _fretRestActiveCalls++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretApplyKind(bool active)
        {
            if (!Enabled)
                return;

            if (active)
                _fretApplyActiveCalls++;
            else
                _fretApplyIdleCalls++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretSpriteWrite()
        {
            if (Enabled) _fretSpriteWrites++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretEnabledWrite()
        {
            if (Enabled) _fretEnabledWrites++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretSortingWrite()
        {
            if (Enabled) _fretSortingWrites++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretMaskWrite()
        {
            if (Enabled) _fretMaskWrites++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretNormalizeWrite()
        {
            if (Enabled) _fretNormalizeWrites++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretTransformWrite()
        {
            if (Enabled) _fretTransformWrites++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretSettleBlocker(bool held, bool sustain, bool openSustain, bool manualPop, bool lit, bool headMoving)
        {
            if (!Enabled)
                return;

            if (held) _fretBlockHeld++;
            if (sustain) _fretBlockSustain++;
            if (openSustain) _fretBlockOpenSustain++;
            if (manualPop) _fretBlockManualPop++;
            if (lit) _fretBlockLit++;
            if (headMoving) _fretBlockHeadMoving++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretOverlayCreated()
        {
            if (Enabled) _fretOverlaysCreated++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordFretForceRenderingOffWrite()
        {
            if (Enabled) _fretForceRenderingOffWrites++;
        }

        public static void TickFrame()
        {
            if (!Enabled)
                return;

            float frameMs = Time.unscaledDeltaTime * 1000f;
            _frames++;
            _totalFrameMs += frameMs;
            if (frameMs > _maxFrameMs)
                _maxFrameMs = frameMs;

            long now = Stopwatch.GetTimestamp();
            if (now < _nextReportTicks)
                return;

            LogReport();
            ResetCounters();
            _nextReportTicks = now + SecondsToTicks(_intervalSeconds);
        }

        private static void LogReport()
        {
            double tickToMs = 1000.0 / Stopwatch.Frequency;
            float avgFrame = _frames > 0 ? _totalFrameMs / _frames : 0f;

            ReportBuilder.Clear();
            ReportBuilder.Append("[ClonZones][PROFILE] frames=");
            ReportBuilder.Append(_frames);
            ReportBuilder.Append(" avgFrameMs=");
            ReportBuilder.Append(avgFrame.ToString("0.###", CultureInfo.InvariantCulture));
            ReportBuilder.Append(" maxFrameMs=");
            ReportBuilder.Append(_maxFrameMs.ToString("0.###", CultureInfo.InvariantCulture));

            for (int i = 0; i < (int)ProfileScope.Count; i++)
            {
                int calls = Calls[i];
                double totalMs = TotalTicks[i] * tickToMs;
                double avgMs = calls > 0 ? totalMs / calls : 0.0;
                double maxMs = MaxTicks[i] * tickToMs;

                ReportBuilder.Append(" | ");
                ReportBuilder.Append(ScopeNames[i]);
                ReportBuilder.Append(" c=");
                ReportBuilder.Append(calls);
                ReportBuilder.Append(" totalMs=");
                ReportBuilder.Append(totalMs.ToString("0.###", CultureInfo.InvariantCulture));
                ReportBuilder.Append(" avgMs=");
                ReportBuilder.Append(avgMs.ToString("0.####", CultureInfo.InvariantCulture));
                ReportBuilder.Append(" maxMs=");
                ReportBuilder.Append(maxMs.ToString("0.####", CultureInfo.InvariantCulture));
            }

            int restTotal = _fretRestSettledCalls + _fretRestActiveCalls;
            if (restTotal > 0)
            {
                float settledPct = 100f * _fretRestSettledCalls / restTotal;
                ReportBuilder.Append(" | fretRest settled=");
                ReportBuilder.Append(_fretRestSettledCalls);
                ReportBuilder.Append(" active=");
                ReportBuilder.Append(_fretRestActiveCalls);
                ReportBuilder.Append(" settledPct=");
                ReportBuilder.Append(settledPct.ToString("0.#", CultureInfo.InvariantCulture));
            }

            int applyTotal = _fretApplyActiveCalls + _fretApplyIdleCalls;
            if (applyTotal > 0)
            {
                float activePct = 100f * _fretApplyActiveCalls / applyTotal;
                ReportBuilder.Append(" | fretApplySplit active=");
                ReportBuilder.Append(_fretApplyActiveCalls);
                ReportBuilder.Append(" idle=");
                ReportBuilder.Append(_fretApplyIdleCalls);
                ReportBuilder.Append(" activePct=");
                ReportBuilder.Append(activePct.ToString("0.#", CultureInfo.InvariantCulture));
            }

            int writeTotal = _fretSpriteWrites + _fretEnabledWrites + _fretSortingWrites + _fretMaskWrites + _fretNormalizeWrites + _fretTransformWrites;
            if (writeTotal > 0)
            {
                ReportBuilder.Append(" | fretWrites sprite=");
                ReportBuilder.Append(_fretSpriteWrites);
                ReportBuilder.Append(" enabled=");
                ReportBuilder.Append(_fretEnabledWrites);
                ReportBuilder.Append(" sorting=");
                ReportBuilder.Append(_fretSortingWrites);
                ReportBuilder.Append(" mask=");
                ReportBuilder.Append(_fretMaskWrites);
                ReportBuilder.Append(" normalize=");
                ReportBuilder.Append(_fretNormalizeWrites);
                ReportBuilder.Append(" transform=");
                ReportBuilder.Append(_fretTransformWrites);
            }

            int overlayTotal = _fretOverlaysCreated + _fretForceRenderingOffWrites;
            if (overlayTotal > 0)
            {
                ReportBuilder.Append(" | fretOverlay created=");
                ReportBuilder.Append(_fretOverlaysCreated);
                ReportBuilder.Append(" forceOff=");
                ReportBuilder.Append(_fretForceRenderingOffWrites);
            }

            int blockerTotal = _fretBlockHeld + _fretBlockSustain + _fretBlockOpenSustain + _fretBlockManualPop + _fretBlockLit + _fretBlockHeadMoving;
            if (blockerTotal > 0)
            {
                ReportBuilder.Append(" | fretBlock held=");
                ReportBuilder.Append(_fretBlockHeld);
                ReportBuilder.Append(" sustain=");
                ReportBuilder.Append(_fretBlockSustain);
                ReportBuilder.Append(" openSustain=");
                ReportBuilder.Append(_fretBlockOpenSustain);
                ReportBuilder.Append(" manualPop=");
                ReportBuilder.Append(_fretBlockManualPop);
                ReportBuilder.Append(" lit=");
                ReportBuilder.Append(_fretBlockLit);
                ReportBuilder.Append(" headMoving=");
                ReportBuilder.Append(_fretBlockHeadMoving);
            }

            // Console + file writes block for ~10-15ms on Windows and this runs
            // inside OnUpdate, so a synchronous write turns every report interval
            // into a visible frame hitch. Hand the finished string to a worker
            // thread; MelonLogger is thread-safe.
            var log = _log;
            if (log == null)
                return;

            string report = ReportBuilder.ToString();
            System.Threading.ThreadPool.QueueUserWorkItem(_ => log.Msg(report));
        }

        private static void ResetCounters()
        {
            Array.Clear(TotalTicks, 0, TotalTicks.Length);
            Array.Clear(MaxTicks, 0, MaxTicks.Length);
            Array.Clear(Calls, 0, Calls.Length);
            _frames = 0;
            _totalFrameMs = 0f;
            _maxFrameMs = 0f;
            _fretRestSettledCalls = 0;
            _fretRestActiveCalls = 0;
            _fretApplyActiveCalls = 0;
            _fretApplyIdleCalls = 0;
            _fretSpriteWrites = 0;
            _fretEnabledWrites = 0;
            _fretSortingWrites = 0;
            _fretMaskWrites = 0;
            _fretNormalizeWrites = 0;
            _fretTransformWrites = 0;
            _fretBlockHeld = 0;
            _fretBlockSustain = 0;
            _fretBlockOpenSustain = 0;
            _fretBlockManualPop = 0;
            _fretBlockLit = 0;
            _fretBlockHeadMoving = 0;
            _fretOverlaysCreated = 0;
            _fretForceRenderingOffWrites = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long SecondsToTicks(float seconds)
        {
            return (long)(seconds * Stopwatch.Frequency);
        }

        private static void ReadSettings(string assetRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(assetRoot))
                    return;

                string path = Path.Combine(assetRoot, "settings.ini");
                if (!File.Exists(path))
                    return;

                bool inProfiling = false;
                foreach (var rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                        continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inProfiling = string.Equals(line, "[profiling]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inProfiling)
                        continue;

                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                        continue;

                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();

                    if (string.Equals(key, "enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        Enabled = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                                  || value.Equals("1", StringComparison.OrdinalIgnoreCase)
                                  || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                                  || value.Equals("on", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (string.Equals(key, "interval_seconds", StringComparison.OrdinalIgnoreCase)
                             && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                             && parsed >= 1f)
                    {
                        _intervalSeconds = parsed;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"[ClonZones] Could not read profiling settings.ini section: {ex.Message}");
            }
        }
    }
}
