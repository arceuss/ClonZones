using Il2Cpp;
using UnityEngine;
using System.Reflection;

namespace ClonZones
{
    /// <summary>
    /// One frame of Clone Hero gameplay state, read once per LateUpdate. Plain data; the
    /// HUD only presents it and never writes back into the engine.
    /// </summary>
    internal struct Gh3HudSnapshot
    {
        public bool Valid;
        public double SongTime;
        public bool Paused, SongPlaying, SongOver;
        public int Score;          // engine total (base + solo + bonus parts)
        public int Streak;         // engine combo, 0 on miss
        public int ComboBreaks;    // engine 0xB0: every combo break (misses and overstrums alike)
        public int MissEvents;     // combo-break callbacks without the overstrum flag (BasePlayer 0x20A070 postfix)
        public int GhostEvents;    // combo-break callbacks with it (overstrum / ghost input)
        public int Multiplier;     // effective 1..4, 2/4/6/8 while SP is active
        public float Health;       // 0..1, fail at <= 0
        public bool NoFail;        // no-fail HUD presentation after the leaderboard override
        public float StarPower;    // 0..1
        public bool StarPowerActive;
        public bool StarPowerReady;
        // WORMod presentation state (read-only; never derived from GH3 score thresholds):
        // SongLength = CloneHero.dll/GameManager.songLength, double, native +0x50, NaN when unknown.
        // IsPractice = CloneHero.dll/GlobalVariables.instance.isPracticeEnabled, bool, instance +0x72.
        // PracticeStartTime/SectionStart/Ticks = GameManager.+0xD0/+0xC8/+0xE0/+0xF0; range applies
        // Stars/Max/Fraction = StarProgress.field_Private_Int32_0/_Int32_1 (+0x58/+0x5C) and
        // _Single_4 (+0x78) of ScoreManager.starProgress, the instance its Update feeds; the
        // count is 0..7 (.ctor terminal 7). Fraction is 1 at Stars>=Max (native 0x1802DFB19
        // full-bar branch bypasses the +0x78 write at 0x1802DFBC8); -1/NaN when the widget is
        // absent or GameManager and ScoreManager disagree about which widget it is.
        public double SongLength;
        public bool IsPractice;
        public double PracticeStartTime;
        public int PracticeSectionStart;
        public long PracticeStartTick;
        public long PracticeEndTick;
        public int Stars;
        public int StarsMax;
        public float StarProgressFraction;
    }

    /// <summary>
    /// Binds the supported single-player guitar engine. Member names are the interop
    /// names resolved against GameAssembly.dll for CH v1.1.0.6142 (see the HUD report):
    /// engine 0x88 combo, 0x48 effective multiplier, 0x42 SP active, 0x70 health,
    /// total-score getter 0x20F3D30, SP norm getter 0x20F4CB0, ready predicate 0x20F57F0.
    /// GameManager.Update writes the clock and BasePlayer.Update steps the engine, so a
    /// LateUpdate reader sees the frame's final values.
    /// Presentation extension: ScoreManager.Update (0x1801286B0) drives its StarProgress
    /// (0x1802DF7F0) before LateUpdate while GameManager re-arms the thresholds through
    /// GameManager.starProgress; SongProgress.FixedUpdate (0x1801F9490) gates the
    /// practice branch on GlobalVariables.instance.isPracticeEnabled (instance +0x72) and
    /// computes (songTime-practiceStart)/(songLength-practiceStart) with a [0,1] clamp.
    /// </summary>
    internal sealed class Gh3HudStateBridge
    {
        // Presentation bindings, validated lazily on the first WORMod request (never on the GH3
        // path): each newly-read field's native offset must equal the v1.1.0.6142 value proven by
        // idalib disassembly + il2cpp.h order. Any mismatch leaves presentation unsupported with a
        // precise error for the controller log; GH3 fields are unaffected. Missing field/pointer
        // yields unsupported, never a guessed read.
        private static bool _presentationChecked;
        private static bool _presentationSupported;
        private static string _presentationBindingError;
        internal static bool PresentationSupported => _presentationChecked && _presentationSupported;
        internal static string PresentationBindingError => _presentationBindingError;
        private static bool EnsurePresentationBindings()
        {
            if (_presentationChecked) return _presentationSupported;
            _presentationChecked = true;
            try
            {
                if (!CheckOffset(typeof(StarProgress), "NativeFieldInfoPtr_field_Private_Int32_0", 0x58, "StarProgress.field_Private_Int32_0")) return false;
                if (!CheckOffset(typeof(StarProgress), "NativeFieldInfoPtr_field_Private_Int32_1", 0x5C, "StarProgress.field_Private_Int32_1")) return false;
                if (!CheckOffset(typeof(StarProgress), "NativeFieldInfoPtr_field_Private_Single_4", 0x78, "StarProgress.field_Private_Single_4")) return false;
                if (!CheckOffset(typeof(GlobalVariables), "NativeFieldInfoPtr_isPracticeEnabled", 0x72, "GlobalVariables.isPracticeEnabled")) return false;
                if (!CheckOffset(typeof(GameManager), "NativeFieldInfoPtr_starProgress", 0x40, "GameManager.starProgress")) return false;
                if (!CheckOffset(typeof(GameManager), "NativeFieldInfoPtr_scoreManager", 0xB8, "GameManager.scoreManager")) return false;
                if (!CheckOffset(typeof(ScoreManager), "NativeFieldInfoPtr_starProgress", 0x90, "ScoreManager.starProgress")) return false;
                if (!CheckOffset(typeof(GameManager), "NativeFieldInfoPtr_songLength", 0x50, "GameManager.songLength")) return false;
                if (!CheckOffset(typeof(GameManager), "NativeFieldInfoPtr_practiceStartTime", 0xD0, "GameManager.practiceStartTime")) return false;
                if (!CheckOffset(typeof(GameManager), "NativeFieldInfoPtr_practiceSectionStart", 0xC8, "GameManager.practiceSectionStart")) return false;
                if (!CheckOffset(typeof(GameManager), "NativeFieldInfoPtr_practiceStartTick", 0xE0, "GameManager.practiceStartTick")) return false;
                if (!CheckOffset(typeof(GameManager), "NativeFieldInfoPtr_practiceEndTick", 0xF0, "GameManager.practiceEndTick")) return false;
            }
            catch (System.Exception ex)
            {
                _presentationBindingError = $"presentation guard: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
            _presentationSupported = true;
            return true;
        }
        private static bool CheckOffset(System.Type type, string nativeFieldInfoName, int expected, string label)
        {
            int actual = GetNativeFieldOffset(type, nativeFieldInfoName);
            if (actual != expected)
            {
                _presentationBindingError = $"{label}: expected 0x{expected:X}, actual {(actual < 0 ? "missing" : $"0x{actual:X}")}";
                return false;
            }
            return true;
        }
        private static int GetNativeFieldOffset(System.Type type, string nativeFieldInfoName)
        {
            var field = type.GetField(nativeFieldInfoName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
                return -1;
            var fieldInfoPtr = (System.IntPtr)field.GetValue(null);
            if (fieldInfoPtr == System.IntPtr.Zero)
                return -1;
            return (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(fieldInfoPtr);
        }
        private readonly BasePlayer _player;
        private readonly GameManager _manager;
        private ObjectPublicAbstractDoBoDoInBoObDoInSiBoUnique _engine;
        public Gh3HudStateBridge(BasePlayer player)
        {
            _player = player;
            _manager = player.gameManager;
        }

        private static bool ReadNoFail()
        {
            GlobalVariables globals = GlobalVariables.instance;
            if (!Alive(globals)) return false;
            if (globals.isPracticeEnabled) return true;
            var noFail = ObjectPublicAbstractSealedBoObObObObObObObObObUnique
                .field_Public_Static_Object2PublicBoSiInSiDoStSiStStUnique_27;
            // CH 1.1.0.6142 BasePlayer 0x180209C70 restores the normal meter in
            // leaderboard mode, except in practice. The saved setting alone is wrong.
            return noFail != null && noFail.prop_Boolean_0 &&
                (!Alive(LeaderboardsOnlineManager.instance) || !LeaderboardsOnlineManager.prop_Boolean_1);
        }

        /// <summary>False when the player or engine is gone (scene teardown, engine swap).</summary>
        public bool Read(ref Gh3HudSnapshot s, bool presentation = false)
        {
            s.Valid = false;
            if (!Alive(_player) || !Alive(_manager)) return false;
            var engine = _player.engine;
            if (engine == null) return false;
            _engine = engine;
            s.SongTime = _manager.songTime;
            s.Paused = _manager.isPaused;
            s.SongPlaying = _manager.isSongPlaying;
            s.SongOver = _manager.isSongOver;
            s.Score = engine.prop_Int32_2;            // Method_Public_get_Int32_2: base + solo + bonus
            s.Streak = engine.field_Public_Int32_1;
            s.ComboBreaks = engine.field_Public_Int32_10;
            s.MissEvents = Gh3HudBreakHook.Misses(_player);
            s.GhostEvents = Gh3HudBreakHook.Ghosts(_player);
            s.Multiplier = engine.field_Protected_Int32_0;
            s.Health = engine.field_Protected_Single_0;
            s.NoFail = ReadNoFail();
            s.StarPower = engine.prop_Single_0;       // Method_Public_get_Single_0: raw / max
            s.StarPowerActive = engine.field_Public_Boolean_0;
            // Readiness: raw accumulator (0x68) against the half-bar threshold (0x1B8); the interop
            // prop_Boolean_1 stayed false at sp=0.567 in a live run, so it is not this predicate.
            s.StarPowerReady = !s.StarPowerActive && engine.field_Public_Int64_0 >= engine.field_Public_Int64_3;
            // Presentation fields stay invalid unless the WORMod caller opts in and the lazy guard
            // passes. Explicit null checks only; no per-frame discovery or catches. Unsupported
            // presentation returns false so no caller can mistake invalid sentinels for success.
            s.SongLength = double.NaN;
            s.IsPractice = false;
            s.PracticeStartTime = double.NaN;
            s.PracticeSectionStart = -1;
            s.PracticeStartTick = -1;
            s.PracticeEndTick = -1;
            s.Stars = -1;
            s.StarsMax = -1;
            s.StarProgressFraction = float.NaN;
            if (presentation)
            {
                if (!EnsurePresentationBindings())
                    return false;
                double length = _manager.songLength;
                if (length > 0.0 && !double.IsNaN(length) && !double.IsInfinity(length))
                    s.SongLength = length;
                GlobalVariables globals = GlobalVariables.instance;
                if (!Alive(globals))
                    return false;
                s.IsPractice = globals.isPracticeEnabled;
                if (s.IsPractice)
                {
                    s.PracticeStartTime = _manager.practiceStartTime;
                    s.PracticeSectionStart = _manager.practiceSectionStart;
                    s.PracticeStartTick = _manager.practiceStartTick;
                    s.PracticeEndTick = _manager.practiceEndTick;
                }
                // Init goes through GameManager.starProgress, per-frame updates through
                // ScoreManager.starProgress. They're one widget in a working scene; if they ever
                // differ or either is gone, hide the WOR stars rather than mirror the wrong one.
                // Score, health and completion stay valid. m_CachedPtr is a field read, unlike
                // the Il2Cpp == operator, which is a native invoke that boxes its result.
                StarProgress initialized = _manager.starProgress;
                ScoreManager scores = _manager.scoreManager;
                StarProgress stars = scores?.starProgress;
                if (Alive(initialized) && Alive(scores) && Alive(stars) && stars.Pointer == initialized.Pointer)
                {
                    s.Stars = stars.field_Private_Int32_0;
                    s.StarsMax = stars.field_Private_Int32_1;
                    s.StarProgressFraction = stars.field_Private_Single_4;
                    // Max-stars display branch (0x1802DFB19) draws the bar full and bypasses the
                    // +0x78 write (0x1802DFBC8): expose the native display fraction 1, not the stale field.
                    if (s.StarsMax > 0 && s.Stars >= s.StarsMax)
                        s.StarProgressFraction = 1.0f;
                }
            }
            s.Valid = true;
            return true;
        }

        private static bool Alive(UnityEngine.Object value) => value is not null && value.m_CachedPtr != System.IntPtr.Zero;

        /// <summary>Diagnostics only: raw star power fields and the missed-note counter.</summary>
        public string RawFields()
        {
            var e = _engine;
            return e == null ? "no engine" : $"spRaw={e.field_Public_Int64_0} spMax={e.field_Public_Int64_2} spReadyAt={e.field_Public_Int64_3} breaks={e.field_Public_Int32_10} misses={Gh3HudBreakHook.Misses(_player)} ghosts={Gh3HudBreakHook.Ghosts(_player)}";
        }

        public bool EngineChanged => _engine != null && Alive(_player) && _player.engine?.Pointer != _engine.Pointer;
    }
}
