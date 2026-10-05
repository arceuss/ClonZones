using System;

namespace ClonZones
{
    internal readonly struct FlameFxProfile
    {
        public readonly PresentationStyle Style;
        public readonly float Velocity;
        public readonly int EmitMilliseconds, CleanupMilliseconds;
        // CH adaptation: retain the native recurrence at 60 Hz. Better's wait
        // operands are not a particle clock, and no render-FPS factor belongs here.
        public const double StepSeconds = 1.0 / 60.0, ReferenceHz = 60.0;
        public const float ParticleSeconds = .25f, EmitInterval = .02f;
        public const int Columns = 4, Rows = 4;
        public const float SpriteFps = 60f;
        public FlameFxProfile(PresentationStyle style)
        {
            Style = style;
            Velocity = style == PresentationStyle.Wormod ? 4.8f : 10f;
            EmitMilliseconds = 100;
            CleanupMilliseconds = style == PresentationStyle.Wormod ? 167 : 166;
        }
    }

    // Ordinary GH3 wait, 0x493040 + 0x5B0B20. The caller owns poll order.
    // This doesn't change the working HUD's intentional millisecond scheduler.
    internal struct FlameFxWait
    {
        public float Remaining;
        private bool _visited;
        public static FlameFxWait Milliseconds(int value) => new() { Remaining = (float)(value * (double).001f) };
        public bool Poll(float delta)
        {
            if (delta <= Remaining) { Remaining -= delta; _visited = true; return false; }
            if (_visited) return true;
            _visited = true; Remaining = 0; return false;
        }
    }

    internal readonly struct FlameFxHandle
    {
        public readonly int Slot;
        public readonly long Generation, Epoch;
        public FlameFxHandle(int slot, long generation, long epoch) { Slot = slot; Generation = generation; Epoch = epoch; }
    }

    internal readonly struct FlameFxContext
    {
        public readonly FlameFxHandle Sprite, Emitter, Container;
        public FlameFxContext(FlameFxHandle sprite, FlameFxHandle emitter, FlameFxHandle container)
        { Sprite = sprite; Emitter = emitter; Container = container; }
    }

    internal struct FlameFxParticle
    {
        public float X, Y, VX, VY, Age;
        public float PreviousX, PreviousY;
        public long Serial, EventSerial;
        public int Lane;
        public bool Star;
        public byte R, G, B, A;
        public float Scale;
    }

    internal struct FlameFxSprite
    {
        public bool Alive, Star;
        public float X, Y;
        public double Born;
        public long Serial, EventSerial;
        public int Lane;
        public FlameFxContext Context;
        internal FlameFxWait Wait;
        internal bool Draining, ScriptActive;
        public int Frame(double now)
        {
            int frame = (int)Math.Max(0, Math.Floor((now - Born) * FlameFxProfile.SpriteFps + 1e-9));
            // AnimatedTexture_UI / OneShot_ZeroStart: material-library VS
            // 0x2FFE54 clamps (time-creationTime)*FPS to UCells*VCells-1.
            return Math.Min(frame, FlameFxProfile.Columns * FlameFxProfile.Rows - 1);
        }
    }

    internal static class FlameFxParticleMath
    {
        public static FlameFxParticle Spawn(float x, float y, bool star, float speed, int angleDraw, int radiusDraw)
        {
            if ((uint)angleDraw >= 10240u || (uint)radiusDraw >= 64u)
                throw new ArgumentOutOfRangeException(nameof(angleDraw));
            float angle = (-80f + angleDraw / 64f) * 0.01745329238474369f;
            float sin = (float)Math.Sin(angle), up = -(float)Math.Cos(angle);
            float radius = radiusDraw / 64f;
            float px = x + radius * sin, py = y - 20f + radius * up;
            return new FlameFxParticle
            {
                X = px, Y = py, PreviousX = px, PreviousY = py,
                VX = speed * sin, VY = speed * up, Star = star,
                R = star ? (byte)0 : (byte)255, G = star ? (byte)255 : (byte)128,
                B = star ? (byte)255 : (byte)0, A = 255, Scale = 1f
            };
        }

        public static bool Step(ref FlameFxParticle p, float dt)
        {
            p.PreviousX = p.X; p.PreviousY = p.Y;
            p.Age += dt;
            if (p.Age > FlameFxProfile.ParticleSeconds) return false;
            // 0x429157: displacement first, then additive friction * delta.
            p.X += p.VX; p.Y += p.VY; p.VY += 50f * dt;
            float f = p.Age / FlameFxProfile.ParticleSeconds;
            int t12 = (int)(f * 4096f);
            p.R = p.Star ? (byte)0 : (byte)255;
            p.G = p.Star ? (byte)255 : (byte)(128 + ((-128 * t12) >> 12));
            p.B = p.Star ? (byte)255 : (byte)0;
            p.A = (byte)(255 + ((-255 * t12) >> 12));
            p.Scale = 1f + (.5f - 1f) * f;
            return true;
        }
    }

    internal sealed class FlameFxSimulation
    {
        public const int MaxEmitters = 80, MaxParticlesPerEmitter = 64, MaxParticles = 2000;
        public const int MaxSprites = 5 * 257, MaxPending = 1024;
        private struct Pending
        {
            public ushort Mask;
            public double Time;
            public long Serial, Poll;
            public float X0, X1, X2, X3, X4, Y0, Y1, Y2, Y3, Y4;
            public float X(int lane) => lane switch { 0 => X0, 1 => X1, 2 => X2, 3 => X3, _ => X4 };
            public float Y(int lane) => lane switch { 0 => Y0, 1 => Y1, 2 => Y2, 3 => Y3, _ => Y4 };
        }
        private sealed class Emitter
        {
            public readonly FlameFxParticle[] Particles = new FlameFxParticle[MaxParticlesPerEmitter];
            public bool Active, Star, Draining;
            public float X, Y, Time, LastEmission;
            public long Generation, EventSerial;
            public int Count, Lane;
        }
        private readonly Emitter[] _emitters = new Emitter[MaxEmitters];
        private readonly Pending[] _pending = new Pending[MaxPending];
        private readonly FlameFxSprite[] _sprites = new FlameFxSprite[MaxSprites];
        private readonly float[] _laneX = new float[5], _laneY = new float[5];
        private int _pendingHead, _pendingCount, _spriteSlot, _traceCount;
        private long _serial, _eventSerial, _poll, _step;
        private uint _rng;
        public readonly FlameFxProfile Profile;
        public double Now { get; private set; }
        public long Epoch { get; private set; } = 1;
        public long LastEventSerial => _eventSerial;
        public long DroppedPending { get; private set; }
        public long DroppedEmitters { get; private set; }
        public long DroppedParticles { get; private set; }
        public int ParticleCount { get; private set; }
        public int PendingCount => _pendingCount;
        public int NextSpriteSlot => _spriteSlot;
        public Action<string> Trace;
        public long Owner;
        public int SpriteCount
        {
            get { int n = 0; for (int i = 0; i < _sprites.Length; ++i) if (_sprites[i].Alive) ++n; return n; }
        }
        public FlameFxSimulation(PresentationStyle style, uint seed = 0x413A5F01u)
        {
            Profile = new FlameFxProfile(style); _rng = seed == 0 ? 1 : seed;
            for (int i = 0; i < _emitters.Length; ++i) _emitters[i] = new Emitter();
            for (int i = 0; i < 5; ++i) SetLaneAnchor(i, 640f + (i - 2) * 102.4f, 655f);
        }
        public void Diagnostic(string stage, long serial, int lane, int slot, long generation, float x, float y, int frame = -1)
        {
            if (Trace == null || _traceCount >= 12000) return;
            ++_traceCount;
            Trace($"NoteFX trace {stage} owner={Owner:X} epoch={Epoch} event={serial} lane={lane} slot={slot} generation={generation} parent={Owner:X}/{Epoch} x={x:R} y={y:R} frame={frame} time={Now:R}");
        }
        public void SetLaneAnchor(int lane, float x, float y)
        {
            if ((uint)lane >= 5u || !float.IsFinite(x) || !float.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(lane));
            _laneX[lane] = x; _laneY[lane] = y;
        }
        public bool Queue(ushort physicalMask, double eventTime)
        {
            physicalMask &= 0x003e;
            if (physicalMask == 0) return true;
            if (!double.IsFinite(eventTime)) return false;
            if (_pendingCount == MaxPending) { ++DroppedPending; return false; }
            int slot = (_pendingHead + _pendingCount) % MaxPending;
            _pending[slot] = new Pending
            {
                Mask = physicalMask, Time = Math.Max(Now, eventTime), Serial = ++_eventSerial, Poll = _poll + 1,
                X0 = _laneX[0], X1 = _laneX[1], X2 = _laneX[2], X3 = _laneX[3], X4 = _laneX[4],
                Y0 = _laneY[0], Y1 = _laneY[1], Y2 = _laneY[2], Y3 = _laneY[3], Y4 = _laneY[4]
            };
            ++_pendingCount;
            Diagnostic("capture-mask", _eventSerial, physicalMask, slot, 0, 0, 0);
            return true;
        }
        public void Advance(double seconds, bool starPowerAtDispatch)
        {
            if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (seconds == 0) return;
            double target = Now + seconds;
            if (!double.IsFinite(target) || target > long.MaxValue / FlameFxProfile.ReferenceHz - 1)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            ++_poll;
            // Bring existing effects to the observation time before reading new
            // delayed SP state. Never replay this sample over past catch-up hits.
            long finalStep = (long)Math.Floor(target * FlameFxProfile.ReferenceHz + 1e-9);
            while (_step < finalStep)
            {
                Now = ++_step / FlameFxProfile.ReferenceHz;
                for (int i = 0; i < MaxSprites; ++i)
                {
                    ref FlameFxSprite sprite = ref _sprites[i];
                    if (!sprite.ScriptActive) continue;
                    if (!sprite.Wait.Poll((float)FlameFxProfile.StepSeconds)) continue;
                    if (!sprite.Draining)
                    {
                        StopEmission(sprite.Context.Emitter);
                        sprite.Draining = true;
                        sprite.Wait = FlameFxWait.Milliseconds(Profile.CleanupMilliseconds);
                        sprite.Wait.Poll(0);
                    }
                    else { DestroySprite(sprite.Context.Sprite); sprite.ScriptActive = false; }
                }
                TickParticles();
            }
            Now = target;
            while (_pendingCount > 0)
            {
                Pending hit = _pending[_pendingHead];
                if (hit.Poll > _poll || hit.Time > Now) break;
                _pendingHead = (_pendingHead + 1) % MaxPending; --_pendingCount;
                for (int lane = 0; lane < 5; ++lane)
                    if ((hit.Mask & (1 << (lane + 1))) != 0) Spawn(hit, lane, starPowerAtDispatch);
            }
        }
        private int Random(int range)
        {
            uint x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
            return (int)(((ulong)x * (uint)range) >> 32);
        }
        private FlameFxContext Spawn(Pending hit, int lane, bool star)
        {
            int attempts = 0;
            while (_sprites[_spriteSlot].Alive || _sprites[_spriteSlot].ScriptActive)
            {
                _spriteSlot = (_spriteSlot + 1) % MaxSprites;
                if (++attempts == MaxSprites) throw new InvalidOperationException("NoteFX sprite pool exhausted.");
            }
            int slot = _spriteSlot, emitterSlot = -1;
            long generation = ++_serial;
            for (int i = 0; i < MaxEmitters; ++i) if (!_emitters[i].Active) { emitterSlot = i; break; }
            FlameFxContext context = new(new FlameFxHandle(slot, generation, Epoch),
                new FlameFxHandle(emitterSlot, generation, Epoch), new FlameFxHandle(0, Owner, Epoch));
            FlameFxWait wait = FlameFxWait.Milliseconds(Profile.EmitMilliseconds);
            // Native Run may poll an armed wait inline. The CH reference policy
            // samples it at arm with zero elapsed, then once per reference step.
            wait.Poll(0);
            _sprites[slot] = new FlameFxSprite
            {
                Alive = true, Star = star, X = hit.X(lane), Y = hit.Y(lane), Born = Now,
                Serial = generation, EventSerial = hit.Serial, Lane = lane, Context = context, Wait = wait, ScriptActive = true
            };
            _spriteSlot = (_spriteSlot + 1) % MaxSprites;
            Diagnostic(star ? "sprite-star" : "sprite-normal", hit.Serial, lane, slot, generation, hit.X(lane), hit.Y(lane), 0);
            if (emitterSlot < 0) { ++DroppedEmitters; return context; }
            Emitter emitter = _emitters[emitterSlot];
            emitter.Active = true; emitter.Draining = false; emitter.Count = 0; emitter.Star = star;
            emitter.X = hit.X(lane); emitter.Y = hit.Y(lane); emitter.Lane = lane;
            emitter.Time = emitter.LastEmission = 0;
            emitter.Generation = generation; emitter.EventSerial = hit.Serial;
            Diagnostic("emitter", hit.Serial, lane, emitterSlot, generation, emitter.X, emitter.Y - 20);
            return context;
        }
        public bool StopEmission(FlameFxHandle handle)
        {
            if (handle.Epoch != Epoch || (uint)handle.Slot >= MaxEmitters) return false;
            Emitter e = _emitters[handle.Slot];
            if (!e.Active || e.Generation != handle.Generation || e.Draining) return false;
            e.Draining = true;
            Diagnostic("stop", e.EventSerial, e.Lane, handle.Slot, e.Generation, e.X, e.Y);
            return true;
        }
        public bool DestroySprite(FlameFxHandle handle)
        {
            if (handle.Epoch != Epoch || (uint)handle.Slot >= MaxSprites) return false;
            ref FlameFxSprite sprite = ref _sprites[handle.Slot];
            if (!sprite.Alive || sprite.Serial != handle.Generation) return false;
            sprite.Alive = false;
            Diagnostic("destroy", sprite.EventSerial, sprite.Lane, handle.Slot, sprite.Serial, sprite.X, sprite.Y);
            return true;
        }
        private void TickParticles()
        {
            const float dt = (float)FlameFxProfile.StepSeconds;
            for (int e = 0; e < MaxEmitters; ++e)
            {
                Emitter emitter = _emitters[e];
                if (!emitter.Active) continue;
                emitter.Time += dt;
                if (!emitter.Draining)
                    while (emitter.Time - emitter.LastEmission > FlameFxProfile.EmitInterval)
                    {
                        emitter.LastEmission += FlameFxProfile.EmitInterval;
                        if (emitter.Count == MaxParticlesPerEmitter || ParticleCount == MaxParticles)
                        { ++DroppedParticles; continue; }
                        FlameFxParticle particle = FlameFxParticleMath.Spawn(emitter.X, emitter.Y, emitter.Star,
                            Profile.Velocity, Random(10240), Random(64));
                        particle.Serial = ++_serial; particle.EventSerial = emitter.EventSerial; particle.Lane = emitter.Lane;
                        emitter.Particles[emitter.Count++] = particle; ++ParticleCount;
                        Diagnostic("particle", particle.EventSerial, particle.Lane, e, particle.Serial, particle.X, particle.Y);
                    }
                for (int i = 0; i < emitter.Count;)
                {
                    if (!FlameFxParticleMath.Step(ref emitter.Particles[i], dt))
                    {
                        FlameFxParticle dead = emitter.Particles[i];
                        Diagnostic("expire", dead.EventSerial, dead.Lane, e, dead.Serial, dead.X, dead.Y);
                        --emitter.Count; --ParticleCount;
                        // Previous/current travel together. Dense index is never identity.
                        emitter.Particles[i] = emitter.Particles[emitter.Count];
                    }
                    else ++i;
                }
                if (emitter.Draining && emitter.Count == 0)
                {
                    emitter.Active = false;
                    Diagnostic("drained", emitter.EventSerial, emitter.Lane, e, emitter.Generation, emitter.X, emitter.Y);
                }
            }
        }
        public FlameFxParticle PresentedParticle(int emitter, int index)
        {
            Emitter e = _emitters[emitter];
            FlameFxParticle p = e.Particles[index];
            float fraction = (float)Math.Clamp(Now * FlameFxProfile.ReferenceHz - _step, 0, 1);
            p.X = p.PreviousX + (p.X - p.PreviousX) * fraction;
            p.Y = p.PreviousY + (p.Y - p.PreviousY) * fraction;
            return p;
        }
        public int GetSpriteOrder(int[] output)
        {
            if (output == null || output.Length < MaxSprites) throw new ArgumentException("Sprite order scratch buffer too small.");
            int count = 0;
            for (int i = 0; i < MaxSprites; ++i)
                if (_sprites[i].Alive)
                {
                    int at = count;
                    while (at > 0 && _sprites[output[at - 1]].Serial > _sprites[i].Serial)
                    { output[at] = output[at - 1]; --at; }
                    output[at] = i; ++count;
                }
            return count;
        }
        public bool SpriteVisible(int index) => _sprites[index].Alive;
        public ref readonly FlameFxSprite Sprite(int index) => ref _sprites[index];
        public int EmitterParticleCount(int index) => _emitters[index].Active ? _emitters[index].Count : 0;
        public ref readonly FlameFxParticle Particle(int emitter, int index) => ref _emitters[emitter].Particles[index];
        public void Clear()
        {
            Diagnostic("epoch-end", 0, -1, -1, 0, 0, 0);
            Array.Clear(_sprites, 0, _sprites.Length);
            for (int i = 0; i < MaxEmitters; ++i) { _emitters[i].Active = false; _emitters[i].Count = 0; }
            _pendingHead = _pendingCount = _spriteSlot = ParticleCount = 0;
            Now = 0; _poll = _step = 0; ++Epoch;
            // Generations, serials, trace budget and RNG survive restart. Handles don't.
        }
    }
}
