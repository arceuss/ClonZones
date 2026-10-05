using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    // One hook captures semantic hits. Styles own presentation only; no hook
    // clones CH objects, changes game state, or executes a Unity animation.
    internal static class GuitarFlamePatch
    {
        private static readonly Dictionary<IntPtr, Controller> Controllers = new();
        private static readonly Dictionary<IntPtr, BeatRenderer> Pending = new();
        private static readonly List<IntPtr> Remove = new();
        private static MelonLogger.Instance _log;
        private static bool _active, _installed, _chFlamesEnabled = true;

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            _log=log;
            if (_installed) return;
            Type[] signature = { typeof(ObjectPublicObInObDoSiDoUIInBoInUnique), typeof(bool), typeof(bool) };
            const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
            MethodInfo play=typeof(GuitarNeckController).GetMethod("PlayFret",flags,null,signature,null)
                ?? typeof(GuitarNeckController).GetMethod("Method_Public_Virtual_Void_ObjectPublicObInObDoSiDoUIInBoInUnique_Boolean_Boolean_0",flags,null,signature,null);
            if(play==null || play.ReturnType!=typeof(void))
            {
                _log.Warning("[ClonZones] Verified PlayFret signature unavailable; native flames retained.");return;
            }
            MethodInfo start = AccessTools.Method(typeof(BeatRenderer), nameof(BeatRenderer.Start));
            MethodInfo disable = AccessTools.Method(typeof(BeatRenderer), nameof(BeatRenderer.OnDisable));
            if (start == null || disable == null)
            {
                _log.Warning("[ClonZones] NoteFX lifecycle methods unavailable; native flames retained."); return;
            }
            try
            {
                // Harmony calls the original normally. This prefix never returns false.
                // No controller may hide anything until every hook has installed.
                harmony.Patch(start, postfix:new HarmonyMethod(typeof(GuitarFlamePatch),nameof(Started)));
                harmony.Patch(disable, prefix:new HarmonyMethod(typeof(GuitarFlamePatch),nameof(Disabled)));
                harmony.Patch(play,prefix:new HarmonyMethod(typeof(GuitarFlamePatch),nameof(PlayFretPrefix)));
                _installed = true;
            }
            catch (Exception ex)
            {
                _log.Warning("[ClonZones] NoteFX hook installation failed; native flames retained: " + ex); return;
            }
            _log.Msg($"[ClonZones] NoteFX hit observer: {play.DeclaringType?.FullName}.{play.Name}; styles sampled at gameplay attach.");
        }
        private static void Started(BeatRenderer __instance)
        {
            if(__instance!=null) Pending[__instance.Pointer]=__instance;
        }
        private static void Disabled(BeatRenderer __instance)
        {
            if(__instance==null)return;
            Pending.Remove(__instance.Pointer);
            Remove.Clear();
            foreach(var pair in Controllers)
                if(pair.Value.BeatPointer==__instance.Pointer) {pair.Value.Dispose();Remove.Add(pair.Key);}
            foreach(IntPtr key in Remove)Controllers.Remove(key);
            Remove.Clear();
        }
        public static void SetActive(bool active)
        {
            _active=active;
            if(active) _chFlamesEnabled=ReadChFlamesEnabled();
            else foreach(Controller c in Controllers.Values)c.Suspend();
        }
        public static void ClearRuntimeState()
        {
            foreach(Controller c in Controllers.Values)c.Dispose();
            Controllers.Clear();Pending.Clear();Remove.Clear();
        }
        private static void PlayFretPrefix(GuitarNeckController __instance, ObjectPublicObInObDoSiDoUIInBoInUnique __0, bool __1, bool __2)
        {
            if(!_installed || !_active || !_chFlamesEnabled || !UnityIcalls.Alive(__instance) || __0==null)return;
            if(!Controllers.TryGetValue(__instance.Pointer,out Controller c))return;
            long profile=ClonZonesProfiler.BeginScope(ProfileScope.FlamePlayFret);
            try { c.Capture(__0.field_Public_UInt16_0); }
            catch(Exception ex) { c.FailDeferred(ex); }
            finally { ClonZonesProfiler.EndScope(ProfileScope.FlamePlayFret,profile); }
        }
        public static void Tick()
        {
            if(!_installed || !_active || !_chFlamesEnabled)return;
            long profile=ClonZonesProfiler.BeginScope(ProfileScope.FlameTick);
            try
            {
                Remove.Clear();
                foreach(var pair in Pending)
                {
                    BeatRenderer beats=pair.Value;
                    if(beats==null) {Remove.Add(pair.Key);continue;}
                    try
                    {
                        BasePlayer player=beats.field_Private_BasePlayer_0;
                        if(player==null || player.neckController==null || player.engine==null)continue;
                        GuitarNeckController neck=player.neckController.TryCast<GuitarNeckController>();
                        if(neck==null) {Remove.Add(pair.Key);continue;}
                        if(Controllers.ContainsKey(neck.Pointer)) {Remove.Add(pair.Key);continue;}
                        GuitarNoteRenderer notes=player.GetComponent<GuitarNoteRenderer>();
                        if(notes==null || player.mainCamera==null || player.gameManager==null)continue;
                        if(player.gameManager.actualPlayerCount!=1)
                        {
                            _log.Warning("[ClonZones] NoteFX requires the supported single-player field; native multiplayer flames retained.");
                            Remove.Add(pair.Key);continue;
                        }
                        SpriteRenderer[] leaves=ReadFlameLeaves(neck);
                        if(leaves==null)continue; // templates may be initialised after BeatRenderer.Start
                        PresentationSettings settings=PresentationSettings.Read(FlameSpriteBank.AssetRoot,m=>_log.Warning(m));
                        FlameFxAssets assets=FlameSpriteBank.Get(settings.FlameStyle);
                        Controller controller=new Controller(beats,player,notes,leaves,settings,assets);
                        Controllers.Add(neck.Pointer,controller);Remove.Add(pair.Key);
                        _log.Msg($"[ClonZones] NoteFX attached: flame_style={settings.FlameStyle}, hud_style={settings.HudStyle} (independent), velocity={controller.Velocity:R}, reference=60Hz, waits=100/{(settings.FlameStyle==PresentationStyle.Wormod?167:166)}ms sequential, blend=additive. Native renderers retained until a closed hit is owned.");
                    }
                    catch(Exception error)
                    {
                        Remove.Add(pair.Key);
                        _log.Warning($"[ClonZones] NoteFX attach rejected; native flames retained: {error}");
                    }
                }
                foreach(IntPtr key in Remove)Pending.Remove(key);
                Remove.Clear();
                foreach(Controller c in Controllers.Values)
                {
                    try { c.Tick(); }
                    catch(Exception error) { c.Stop(error); }
                }
            }
            finally { ClonZonesProfiler.EndScope(ProfileScope.FlameTick,profile); }
        }
        private static SpriteRenderer[] ReadFlameLeaves(GuitarNeckController neck)
        {
            object source=ReadMember(neck,"FlameAnimators") ?? ReadMember(neck,"field_Public_Animator_Array_0");
            if(source is not IEnumerable list)return null;
            SpriteRenderer[] leaves=new SpriteRenderer[5];int count=0;
            foreach(object item in list)
            {
                if(count==5)break;
                var animator=item as Il2Cpp.Animator;
                if(animator==null)return null;
                var leaf=animator.GetComponent<SpriteRenderer>();
                if(leaf==null || leaf.sharedMaterial==null)return null;
                for(int i=0;i<count;++i)
                    if(leaves[i].Pointer==leaf.Pointer)throw new InvalidOperationException("Flame lanes alias one renderer; ownership is unsupported.");
                leaves[count++]=leaf;
            }
            if(count!=5)return null;
            // One batch must not erase a genuine per-lane sort/material distinction.
            for(int i=1;i<5;++i)
                if(leaves[i].gameObject.layer!=leaves[0].gameObject.layer || leaves[i].sortingLayerID!=leaves[0].sortingLayerID
                    || leaves[i].sortingOrder!=leaves[0].sortingOrder || leaves[i].sharedMaterial.renderQueue!=leaves[0].sharedMaterial.renderQueue)
                    throw new InvalidOperationException("Native flame lanes have different sorting settings; refusing a misleading single batch.");
            return leaves;
        }
        private static object ReadMember(object o,string name)
        {
            const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
            var p=o.GetType().GetProperty(name,flags);
            if(p!=null)return p.GetValue(o);
            return o.GetType().GetField(name,flags)?.GetValue(o);
        }
        private static bool ReadChFlamesEnabled()
        {
            // Existing CH compatibility policy: its Video/Flames toggle can be
            // changed outside gameplay. Never key this on hud_style.
            try
            {
                string mods=Path.GetDirectoryName(typeof(Core).Assembly.Location);
                string game=string.IsNullOrEmpty(mods)?null:Path.GetDirectoryName(mods);
                string path=game==null?null:Path.Combine(game,"PlayerData","settings.ini");
                if(path==null || !File.Exists(path))return true;
                bool video=false;
                foreach(string raw in File.ReadLines(path))
                {
                    string s=raw.Trim();if(s.Length==0 || s.StartsWith(";") || s.StartsWith("#"))continue;
                    if(s.StartsWith("[") && s.EndsWith("]")) {video=s.Equals("[video]",StringComparison.OrdinalIgnoreCase);continue;}
                    if(!video)continue;int n=s.IndexOf('=');
                    if(n<0 || !s.Substring(0,n).Trim().Equals("flames",StringComparison.OrdinalIgnoreCase))continue;
                    string v=s.Substring(n+1).Trim();return v!="0" && !v.Equals("false",StringComparison.OrdinalIgnoreCase);
                }
            }
            catch(Exception ex) {_log.Warning("[ClonZones] CH Flames setting read failed: "+ex.Message);}
            return true;
        }

        private sealed class Controller : IDisposable
        {
            public readonly IntPtr BeatPointer;
            private readonly BasePlayer _player;
            private readonly GameManager _manager;
            private readonly Camera _camera;
            private readonly Gh3HighwayBridge _bridge;
            private readonly SpriteRenderer[] _leaves;
            private readonly bool[] _owned=new bool[5], _prior=new bool[5];
            private readonly BaseFretAnimator[] _frets=new BaseFretAnimator[5];
            private readonly Vector3[] _fretScreen=new Vector3[5];
            private FlameFxProjection _projection;
            private readonly GuitarNoteRenderer _notes;
            private readonly FlameFxSimulation _sim;
            private readonly FlameFxMesh _mesh;
            private IntPtr _engine;
            private double _songTime;
            private long _stamp;
            private long _practiceStartTick, _practiceEndTick;
            private int _practiceSection;
            private ushort _requestedMask;
            private bool _paused, _disposed;
            private Exception _fault;
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private long _reportedOverflow;
            private bool _anchorsLogged;
            public float Velocity=>_sim.Profile.Velocity;
            public Controller(BeatRenderer beats,BasePlayer player,GuitarNoteRenderer notes,SpriteRenderer[] leaves,PresentationSettings settings,FlameFxAssets assets)
            {
                BeatPointer=beats.Pointer;_player=player;_manager=player.gameManager;_camera=player.mainCamera;
                _leaves=leaves;_notes=notes;
                _bridge=new Gh3HighwayBridge(_camera,player.transform,notes);
                _bridge.Refresh();
                var frets=player.neckController.FretAnimators;
                if(frets==null || frets.Length<5)throw new InvalidOperationException("Five fret anchors are required.");
                for(int i=0;i<5;++i) {_frets[i]=frets[i];if(_frets[i]==null)throw new InvalidOperationException("Missing fret anchor.");}
                _sim=new FlameFxSimulation(settings.FlameStyle);
                _sim.Owner=player.Pointer.ToInt64();
                if(Environment.GetEnvironmentVariable("CLONZONES_FLAME_TRACE")=="1")
                    _sim.Trace=message=>_log.Msg(message);
                RefreshAnchors();
                _songTime=_manager.songTime;_engine=player.engine.Pointer;
                _stamp=Stopwatch.GetTimestamp();_paused=_manager.isPaused;
                PracticeChanged();
                // No native leaf is hidden until construction succeeds completely.
                _mesh=new FlameFxMesh(assets,leaves[0]);
            }
            public void Capture(ushort mask)
            {
                if(_disposed || _fault!=null)return;
                if(Environment.CurrentManagedThreadId!=_thread)
                    throw new InvalidOperationException("PlayFret dispatched off the bound Unity thread.");
                if(!UnityIcalls.Alive(_player) || !UnityIcalls.Alive(_manager) || _manager.isPaused || _manager.isSongOver || _player.engine==null)return;
                // Reset scalar event state here as well as in LateUpdate: the
                // first real hit of a new engine must not be discarded as stale.
                IntPtr engine=_player.engine.Pointer; double songTime=_manager.songTime;
                bool practiceChanged=PracticeChanged();
                if(engine!=_engine || songTime<_songTime-1e-6 || practiceChanged)
                {
                    _sim.Clear(); _requestedMask=0; _engine=engine; _songTime=songTime;
                    _stamp=Stopwatch.GetTimestamp(); _paused=false;
                }
                // GH3 has no closed-lane representation of a pure open hit. Let
                // CH use its own open presentation, including shared flame leaves.
                if((mask&1)!=0)_requestedMask=0;
                ushort closed=(ushort)(mask&0x3e);
                if(closed==0)return;
                double elapsed=_paused?0:(Stopwatch.GetTimestamp()-_stamp)/(double)Stopwatch.Frequency;
                if(!_sim.Queue(closed,_sim.Now+Math.Max(0,elapsed)))
                    throw new InvalidOperationException("NoteFX event queue overflow; restoring native rendering instead of losing hits.");
                _requestedMask|=closed;
                _sim.Diagnostic("callback-mask",_sim.LastEventSerial,mask,-1,0,0,0);
            }
            public void Tick()
            {
                if(_disposed)return;
                if(_fault!=null) {Stop(_fault);return;}
                if(!UnityIcalls.Alive(_player) || !UnityIcalls.Alive(_manager) || !UnityIcalls.Alive(_camera) || _player.engine==null) {Dispose();return;}
                Camera main=_player.mainCamera;
                if(!UnityIcalls.Alive(main) || main.Pointer!=_camera.Pointer)
                    throw new InvalidOperationException("Player camera replaced; new gameplay attach required.");
                long stamp=Stopwatch.GetTimestamp();
                double elapsed=(stamp-_stamp)/(double)Stopwatch.Frequency;_stamp=stamp;
                bool paused=_manager.isPaused;
                IntPtr engine=_player.engine.Pointer;double songTime=_manager.songTime;
                bool practiceChanged=PracticeChanged();
                bool reset=engine!=_engine || songTime<_songTime-1e-6 || _manager.isSongOver || practiceChanged;
                _engine=engine;_songTime=songTime;
                if(reset) {_sim.Clear();_requestedMask=0;elapsed=0;}
                if(paused || _paused)elapsed=0;
                _paused=paused;
                RefreshAnchors();
                // renderer field 0x21C is CH's own SP-active snapshot (BaseGuitarNoteRenderer.Update,
                // 18115A2F0), the same flag the sustain path uses. engine.field_Public_Boolean_0 is not it.
                _sim.Advance(Math.Max(0,elapsed),_notes.field_Private_Boolean_0);
                _mesh.Draw(_sim,_projection);
                ApplyOwnership(); // after a valid replacement has been submitted
                long overflow=_sim.DroppedEmitters+_sim.DroppedParticles;
                if(overflow!=_reportedOverflow)
                {
                    if(_reportedOverflow==0)_log.Warning("[ClonZones] NoteFX reached the source emitter/particle budget; see counters, not a lower detail setting.");
                    _reportedOverflow=overflow;
                }
            }
            private bool PracticeChanged()
            {
                // Same generated fields used by the HUD's verified practice epoch.
                // A forward range change must retire old effects too.
                long start=_manager.practiceStartTick,end=_manager.practiceEndTick;
                int section=_manager.practiceSectionStart;
                bool changed=start!=_practiceStartTick || end!=_practiceEndTick || section!=_practiceSection;
                _practiceStartTick=start;_practiceEndTick=end;_practiceSection=section;
                return changed;
            }
            private void RefreshAnchors()
            {
                bool changed=_bridge.Refresh();
                for(int i=0;i<5;++i)
                {
                    if(!UnityIcalls.Alive(_frets[i]) || !UnityIcalls.Alive(_leaves[i]))throw new InvalidOperationException("Flame/fret owner disappeared.");
                    if(!GuitarFretPatch.TryGetLipBottomAnchor(_frets[i],out Vector3 bottom))
                        throw new InvalidOperationException("Rendered fret lip is unavailable; cannot bind the native NoteFX bottom anchor.");
                    _fretScreen[i]=UnityIcalls.WorldToScreenPoint(_camera,bottom);
                }
                _projection=_bridge.CaptureFlameProjection(_fretScreen[0],_fretScreen[4]);
                for(int i=0;i<5;++i)
                {
                    Vector2 anchor=_projection.VirtualPoint(_fretScreen[i].x,_fretScreen[i].y);
                    _sim.SetLaneAnchor(i,anchor.x,anchor.y);
                    if(_sim.Trace!=null && (!_anchorsLogged || changed))
                    {
                        Vector3 fret=_camera.WorldToScreenPoint(_frets[i].transform.position);
                        Vector3 flame=_camera.WorldToScreenPoint(_leaves[i].transform.position);
                        Vector3 replacement=_camera.WorldToScreenPoint(_projection.World(anchor.x,anchor.y));
                        Vector3 lipBottom=_fretScreen[i];
                        _log.Msg($"[ClonZones] NoteFX anchors owner={_sim.Owner} lane={i} fretScreen={fret} nativeFlameScreen={flame} lipBottomScreen={lipBottom} replacementScreen={replacement}.");
                    }
                }
                _anchorsLogged=true;
            }
            private void ApplyOwnership()
            {
                for(int i=0;i<5;++i)
                {
                    bool own=(_requestedMask&(1<<(i+1)))!=0;
                    SpriteRenderer leaf=_leaves[i];if(!UnityIcalls.Alive(leaf))continue;
                    if(own)
                    {
                        if(!_owned[i]) {_prior[i]=leaf.forceRenderingOff;_owned[i]=true;}
                        // CH can reset this flag, so reassert every LateUpdate.
                        if(!leaf.forceRenderingOff)leaf.forceRenderingOff=true;
                    }
                    else if(_owned[i]) {leaf.forceRenderingOff=_prior[i];_owned[i]=false;}
                }
            }
            public void Suspend()
            {
                if(_disposed)return;
                _sim.Clear();_requestedMask=0;_mesh.Hide();ApplyOwnership();
                _stamp=Stopwatch.GetTimestamp();
            }
            public void FailDeferred(Exception error) { _fault ??= error; }
            public void Stop(Exception error)
            {
                if(_disposed)return;
                _log.Warning("[ClonZones] NoteFX stopped; native flame leaves restored: "+error);
                Dispose();
            }
            public void Dispose()
            {
                if(_disposed)return;_disposed=true;
                _sim.Clear();_requestedMask=0;
                try {ApplyOwnership();}
                catch(Exception ex) {_log.Warning("[ClonZones] NoteFX teardown: "+ex.Message);}
                finally {_mesh.Dispose();}
            }
        }
    }
}
