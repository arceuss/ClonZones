using Il2Cpp;
using UnityEngine;

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
        public int MissedNotes;    // engine 0xB0: incremented per missed note, untouched by overstrums
        public int Multiplier;     // effective 1..4, 2/4/6/8 while SP is active
        public float Health;       // 0..1, fail at <= 0
        public float StarPower;    // 0..1
        public bool StarPowerActive;
        public bool StarPowerReady;
    }

    /// <summary>
    /// Binds the supported single-player guitar engine. Member names are the interop
    /// names resolved against GameAssembly.dll for CH v1.1.0.6142 (see the HUD report):
    /// engine 0x88 combo, 0x48 effective multiplier, 0x42 SP active, 0x70 health,
    /// total-score getter 0x20F3D30, SP norm getter 0x20F4CB0, ready predicate 0x20F57F0.
    /// GameManager.Update writes the clock and BasePlayer.Update steps the engine, so a
    /// LateUpdate reader sees the frame's final values.
    /// </summary>
    internal sealed class Gh3HudStateBridge
    {
        private readonly BasePlayer _player;
        private readonly GameManager _manager;
        private ObjectPublicAbstractDoBoDoInBoObDoInSiBoUnique _engine;

        public Gh3HudStateBridge(BasePlayer player)
        {
            _player = player;
            _manager = player.gameManager;
        }

        /// <summary>False when the player or engine is gone (scene teardown, engine swap).</summary>
        public bool Read(ref Gh3HudSnapshot s)
        {
            s.Valid = false;
            if (_player == null || _manager == null) return false;
            var engine = _player.engine;
            if (engine == null) return false;
            _engine = engine;
            s.SongTime = _manager.songTime;
            s.Paused = _manager.isPaused;
            s.SongPlaying = _manager.isSongPlaying;
            s.SongOver = _manager.isSongOver;
            s.Score = engine.prop_Int32_2;            // Method_Public_get_Int32_2: base + solo + bonus
            s.Streak = engine.field_Public_Int32_1;
            s.MissedNotes = engine.field_Public_Int32_10;
            s.Multiplier = engine.field_Protected_Int32_0;
            s.Health = engine.field_Protected_Single_0;
            s.StarPower = engine.prop_Single_0;       // Method_Public_get_Single_0: raw / max
            s.StarPowerActive = engine.field_Public_Boolean_0;
            // Readiness: raw accumulator (0x68) against the half-bar threshold (0x1B8); the interop
            // prop_Boolean_1 stayed false at sp=0.567 in a live run, so it is not this predicate.
            s.StarPowerReady = !s.StarPowerActive && engine.field_Public_Int64_0 >= engine.field_Public_Int64_3;
            s.Valid = true;
            return true;
        }

        /// <summary>Diagnostics only: raw star power fields and the missed-note counter.</summary>
        public string RawFields()
        {
            var e = _engine;
            return e == null ? "no engine" : $"spRaw={e.field_Public_Int64_0} spMax={e.field_Public_Int64_2} spReadyAt={e.field_Public_Int64_3} missed={e.field_Public_Int32_10}";
        }

        public bool EngineChanged => _engine != null && _player != null && _player.engine?.Pointer != _engine.Pointer;
    }
}
