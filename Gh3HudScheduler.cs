using System.Collections.Generic;

namespace ClonZones
{
    /// <summary>What a ported GH3 script yields: `Wait n seconds`, `Wait n gameframe`, or a blocking `:DoMorph`.</summary>
    internal readonly struct Gh3Wait
    {
        public enum Kind { Ms, GameFrames, Morph }
        public readonly Kind What;
        public readonly int Amount;
        public readonly Gh3HudElement Element;
        private Gh3Wait(Kind what, int amount, Gh3HudElement element) { What = what; Amount = amount; Element = element; }
        /// <summary>`Wait t seconds` -> WaitMilliseconds((int)(t*1000)).</summary>
        public static Gh3Wait Seconds(float seconds) => new(Kind.Ms, (int)(seconds * 1000f), null);
        public static Gh3Wait GameFrames(int frames) => new(Kind.GameFrames, frames, null);
        /// <summary>`elem :DoMorph ...` blocks until the element's shared timer is done.</summary>
        public static Gh3Wait Morph(Gh3HudElement element) => new(Kind.Morph, 0, element);
    }

    /// <summary>
    /// Bounded replacement for the GH3 script scheduler: each ported script is an
    /// iterator that yields <see cref="Gh3Wait"/>. `spawnscriptnow` runs the body up to
    /// its first wait immediately; `KillSpawnedScript name=/id=` removes instances.
    /// Time is the HUD millisecond clock the renderer advances (paused with CH).
    /// </summary>
    internal sealed class Gh3HudScheduler
    {
        private sealed class Running
        {
            public string Name;
            public string SpawnId;
            public IEnumerator<Gh3Wait> Body;
            public Gh3Wait Wait;
            public long WaitStartMs;
            public int FramesLeft;
            public bool Dead;
        }

        private readonly List<Running> _scripts = new(16);
        private readonly List<Running> _pending = new(4);
        private long _nowMs;
        private bool _ticking;

        public long NowMs => _nowMs;
        public int Count => _scripts.Count;

        public void Reset() { _scripts.Clear(); _pending.Clear(); _nowMs = 0; }

        public bool IsRunning(string name)
        {
            for (int i = 0; i < _scripts.Count; i++)
                if (!_scripts[i].Dead && _scripts[i].Name == name) return true;
            return false;
        }

        /// <summary>spawnscriptnow: the body runs synchronously until its first wait.</summary>
        public void Spawn(string name, IEnumerator<Gh3Wait> body, string spawnId = null)
        {
            var script = new Running { Name = name, SpawnId = spawnId, Body = body };
            if (!Step(script)) return;
            if (_ticking) _pending.Add(script); else _scripts.Add(script);
        }

        /// <summary>KillSpawnedScript name = ...</summary>
        public void Kill(string name)
        {
            for (int i = 0; i < _scripts.Count; i++) if (_scripts[i].Name == name) _scripts[i].Dead = true;
            for (int i = 0; i < _pending.Count; i++) if (_pending[i].Name == name) _pending[i].Dead = true;
        }

        /// <summary>KillSpawnedScript id = ...</summary>
        public void KillId(string spawnId)
        {
            for (int i = 0; i < _scripts.Count; i++) if (_scripts[i].SpawnId == spawnId) _scripts[i].Dead = true;
            for (int i = 0; i < _pending.Count; i++) if (_pending[i].SpawnId == spawnId) _pending[i].Dead = true;
        }

        /// <summary>Advance the clock by one rendered frame and resume every script whose wait elapsed.</summary>
        public void Tick(int deltaMs)
        {
            _nowMs += deltaMs;
            _ticking = true;
            for (int i = 0; i < _scripts.Count; i++)
            {
                Running s = _scripts[i];
                if (s.Dead) continue;
                if (!Satisfied(s)) continue;
                if (!Step(s)) s.Dead = true;
            }
            _ticking = false;
            for (int i = _scripts.Count - 1; i >= 0; i--) if (_scripts[i].Dead) _scripts.RemoveAt(i);
            for (int i = 0; i < _pending.Count; i++) if (!_pending[i].Dead) _scripts.Add(_pending[i]);
            _pending.Clear();
        }

        private bool Satisfied(Running s)
        {
            switch (s.Wait.What)
            {
                case Gh3Wait.Kind.Ms: return _nowMs - s.WaitStartMs >= s.Wait.Amount;
                case Gh3Wait.Kind.GameFrames: return --s.FramesLeft <= 0;
                default: return s.Wait.Element == null || !s.Wait.Element.Alive || s.Wait.Element.MorphDone(_nowMs);
            }
        }

        /// <summary>Runs the body until it yields a wait that is not already satisfied. False when finished.</summary>
        private bool Step(Running s)
        {
            while (true)
            {
                if (s.Dead || !s.Body.MoveNext()) return false;
                s.Wait = s.Body.Current;
                s.WaitStartMs = _nowMs;
                s.FramesLeft = s.Wait.Amount;
                // A satisfied wait (zero seconds, finished/absent morph) does not yield a frame.
                if (s.Wait.What == Gh3Wait.Kind.GameFrames) return true;
                if (s.Wait.What == Gh3Wait.Kind.Ms && s.Wait.Amount > 0) return true;
                if (s.Wait.What == Gh3Wait.Kind.Morph && s.Wait.Element != null && s.Wait.Element.Alive && !s.Wait.Element.MorphDone(_nowMs)) return true;
            }
        }
    }
}
