using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace ClonZones
{
    internal sealed class Gh3HighwayRenderer : IDisposable
    {
        private static readonly Dictionary<IntPtr, Gh3HighwayRenderer> Controllers = new();
        private static readonly Sprite[] BarArt = new Sprite[3];
        private static MelonLogger.Instance _log;
        private static bool _active;
        private static bool _markers;
        private static bool _warned;
        private readonly Gh3HighwayBridge _bridge;
        private readonly Gh3NoteProjection _noteProjection;
        private readonly Gh3HighwayBacking _backing;
        private readonly Gh3HighwayMesh _markerMesh;
        private readonly Gh3HighwayMesh _strings;
        private readonly Gh3HighwayMesh _sides;
        private readonly Gh3HighwayMesh _eighths;
        private readonly BeatRenderer _beats;
        private readonly BasePlayer _player;
        private readonly GuitarNoteRenderer _notes;
        private readonly GameManager _manager;
        private readonly Gh3BeatTimeline _timeline;
        private readonly NativeBar[] _nativeBars;
        private readonly List<HiddenRenderer> _hidden = new();
        private readonly int _stringWidth, _stringHeight, _sideWidth, _sideHeight, _eighthWidth, _eighthHeight;
        private readonly Vector2 _sideCoverage;
        private double _window;
        private int _pulse = -1;
        private bool _disposed;

        private sealed class NativeBar
        {
            public SpriteRenderer Renderer;
            public Transform Transform;
            public GameObject Object;
            public Sprite Sprite;
            public Material Material;
            public Quaternion Rotation;
            public Vector3 Scale;
            public Vector3 ParentScale;
            public int LastWeight = -1;
        }

        private readonly struct HiddenRenderer
        {
            public readonly Renderer Renderer;
            public readonly bool WasOff;
            public HiddenRenderer(Renderer renderer) { Renderer=renderer; WasOff=renderer.forceRenderingOff; }
        }

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            _log=log;
            var category=MelonPreferences.CreateCategory("ClonZonesHighway");
            _markers=category.CreateEntry("Markers",false).Value;
            for(int i=0;i<3;i++)
            {
                Texture2D texture=i==0 ? HighwaySpriteBank.Large : i==1 ? HighwaySpriteBank.Medium : HighwaySpriteBank.Small;
                if(texture == null) continue;
                BarArt[i]=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),1f,0,SpriteMeshType.FullRect);
                BarArt[i].hideFlags=HideFlags.DontUnloadUnusedAsset;
            }
            harmony.Patch(AccessTools.Method(typeof(BeatRenderer), nameof(BeatRenderer.Start)),
                postfix: new HarmonyMethod(typeof(Gh3HighwayRenderer), nameof(Started)));
            harmony.Patch(AccessTools.Method(typeof(BeatRenderer), nameof(BeatRenderer.OnDisable)),
                prefix: new HarmonyMethod(typeof(Gh3HighwayRenderer), nameof(Disabled)));
        }

        public static void SetActive(bool active) { _active=active; }

        private static void Started(BeatRenderer __instance)
        {
            if(Controllers.ContainsKey(__instance.Pointer)) return;
            BasePlayer player=__instance.field_Private_BasePlayer_0;
            if(player == null || player.neckController == null || player.neckController.TryCast<GuitarNeckController>() == null) return;
            GuitarNoteRenderer notes=player.GetComponent<GuitarNoteRenderer>();
            if(notes == null || player.mainCamera == null) return;
            if(player.gameManager.actualPlayerCount != 1)
            {
                _log.Warning("[ClonZones] GH3 field requires the sourced single-player layout; vanilla multiplayer field retained.");
                return;
            }
            Shader shader=Shader.Find("Sprites/Default");
            if(shader == null)
            {
                _log.Warning("[ClonZones] Sprites/Default is unavailable; vanilla highway retained.");
                return;
            }
            Controllers.Add(__instance.Pointer,new Gh3HighwayRenderer(__instance,player,notes,shader));
        }

        private static void Disabled(BeatRenderer __instance)
        {
            if(Controllers.Remove(__instance.Pointer,out Gh3HighwayRenderer renderer)) renderer.Dispose();
        }

        public static void Tick()
        {
            if(!_active) return;
            foreach(Gh3HighwayRenderer renderer in Controllers.Values)
            {
                try { renderer.Update(); }
                catch(Exception error)
                {
                    renderer.Dispose();
                    if(!_warned) { _warned=true; _log.Error($"[ClonZones] GH3 field stopped; vanilla visuals restored: {error}"); }
                }
            }
        }

        public static void Clear()
        {
            _active=false;
            foreach(Gh3HighwayRenderer renderer in Controllers.Values) renderer.Dispose();
            Controllers.Clear();
        }

        private Gh3HighwayRenderer(BeatRenderer beats,BasePlayer player,GuitarNoteRenderer notes,Shader shader)
        {
            _beats=beats; _player=player; _notes=notes; _manager=player.gameManager;
            _bridge=new Gh3HighwayBridge(player.mainCamera,player.transform,notes);
            _bridge.Refresh();
            _noteProjection = new Gh3NoteProjection(notes, _bridge, _log);
            _backing = new Gh3HighwayBacking(player, notes, _bridge, _log);
            var vanilla=beats.field_Protected_Il2CppReferenceArray_1_SpriteRenderer_0;
            _nativeBars=new NativeBar[vanilla.Length];
            SpriteRenderer template=vanilla[0];
            int layer=template.gameObject.layer, sorting=template.sortingLayerID, order=template.sortingOrder, queue=template.sharedMaterial.renderQueue;
            for(int i=0;i<vanilla.Length;i++)
            {
                SpriteRenderer bar=vanilla[i]; Transform transform=bar.transform;
                _nativeBars[i]=new NativeBar { Renderer=bar, Transform=transform, Object=bar.gameObject, Sprite=bar.sprite,
                    Material=bar.sharedMaterial, Rotation=transform.localRotation, Scale=transform.localScale,
                    ParentScale=transform.parent != null ? transform.parent.lossyScale : Vector3.one };
            }
            var timing=beats.field_Private_ObjectPublicLi1DoObLi1InDoInLiUnique_0;
            var nativeTimes=timing.field_Public_List_1_Double_0;
            var nativeWeights=timing.field_Public_List_1_EnumPublicSealedvaMeBe4vBeUnique_0;
            var nativeMeasures=timing.field_Public_List_1_Int64_0;
            double[] times=new double[nativeTimes.Count]; int[] weights=new int[times.Length];
            for(int i=0;i<times.Length;i++) { times[i]=nativeTimes[i]; weights[i]=(int)nativeWeights[i]; }
            _weights=weights;
            long[] measures=new long[nativeMeasures.Count];
            for(int i=0;i<measures.Length;i++) measures[i]=nativeMeasures[i];
            var song=_manager.song;
            var nativeTempos=song.field_Public_List_1_Object1PublicDoDoUnique_0;
            var tempos=new Gh3BeatTimeline.Tempo[nativeTempos.Count];
            for(int i=0;i<tempos.Length;i++)
            {
                var tempo=nativeTempos[i];
                tempos[i]=new Gh3BeatTimeline.Tempo(tempo.field_Public_Int64_0,tempo.field_Public_Double_1,tempo.field_Public_Double_0);
            }
            // CH 1.1.0.6142 keeps chart resolution in Double_1; Double_0 includes song speed.
            _timeline=new Gh3BeatTimeline(times,weights,measures,tempos,song.field_Public_Double_1,song.field_Public_Double_0);
            _window=(notes.noteZPosFarLimit-notes.strikeLine)/notes.noteSpeed;
            if(HighwaySpriteBank.Small != null)
            {
                _eighthWidth=HighwaySpriteBank.Small.width; _eighthHeight=HighwaySpriteBank.Small.height;
                _eighths=new Gh3HighwayMesh("clonzones_gh3_eighths",HighwaySpriteBank.Small,shader,_bridge,layer,sorting,order,queue,
                    _timeline.MaximumVisible(_window*1.125));
            }
            if(HighwaySpriteBank.String != null)
            {
                _stringWidth=HighwaySpriteBank.String.width; _stringHeight=HighwaySpriteBank.String.height;
                _strings=new Gh3HighwayMesh("clonzones_gh3_strings",HighwaySpriteBank.String,shader,_bridge,layer,sorting,order+1,queue,5);
                var strings=player.neckController.fretStrings;
                for(int i=0;strings != null && i<strings.Length;i++) Hide(strings[i].GetComponent<Renderer>());
            }
            if(HighwaySpriteBank.Sidebar != null)
            {
                _sideWidth=HighwaySpriteBank.Sidebar.width; _sideHeight=HighwaySpriteBank.Sidebar.height;
                // GH3 PC (5F8A57): D3DX9_35, default size, FILTER_NONE.
                // The GPU texture rounds up; the sprite retains the TEX dimensions.
                _sideCoverage=new Vector2((float)_sideWidth/Mathf.NextPowerOfTwo(_sideWidth),
                    (float)_sideHeight/Mathf.NextPowerOfTwo(_sideHeight));
                _sides=new Gh3HighwayMesh("clonzones_gh3_sides",HighwaySpriteBank.Sidebar,shader,_bridge,layer,sorting,order+2,queue,2);
                if(player.trackSidebarLeft != null) Hide(player.trackSidebarLeft.GetComponent<Renderer>());
                if(player.trackSidebarRight != null) Hide(player.trackSidebarRight.GetComponent<Renderer>());
            }
            if(_markers)
                _markerMesh=new Gh3HighwayMesh("clonzones_gh3_markers",Texture2D.whiteTexture,shader,_bridge,layer,sorting,order+3,queue,4,false);
            DrawStatic(true);
            _log.Msg($"[ClonZones] GH3 field attached: nativeBarPool={_nativeBars.Length}, chartBars={times.Length}, addedEighths={_timeline.Bars.Length-times.Length}, sorting={sorting}/{order}, queue={queue}.");
        }

        private void Hide(Renderer renderer)
        {
            if(renderer == null) return;
            _hidden.Add(new HiddenRenderer(renderer)); renderer.forceRenderingOff=true;
        }

        private void Update()
        {
            if(_disposed) return;
            bool changed=_bridge.Refresh();
            long profileStart=ClonZonesProfiler.BeginScope(ProfileScope.Gh3NoteProjection);
            _noteProjection.Update();
            ClonZonesProfiler.EndScope(ProfileScope.Gh3NoteProjection,profileStart);
            _backing.Update(changed);
            int pulse=_timeline.PulsesFired(_manager.songTime);
            if(changed || pulse!=_pulse) { _pulse=pulse; DrawStatic(changed); }
            double window=(_notes.noteZPosFarLimit-_notes.strikeLine)/_notes.noteSpeed;
            if(window!=_window)
            {
                _window=window;
                _eighths?.Reserve(_timeline.MaximumVisible(window*1.125));
            }
            double now=_manager.songTimeVideoTime;
            int first=_beats.field_Protected_Int32_1;
            for(int i=0;i<_nativeBars.Length;i++)
            {
                NativeBar bar=_nativeBars[i]; int index=first+i;
                if(index>=_timeline.PulseTimes.Length || !UnityIcalls.ActiveSelf(bar.Object)) break;
                int weight=_weights[index];
                if(weight<0 || weight>=BarArt.Length || !UnityIcalls.Alive(BarArt[weight])) continue;
                if(weight!=bar.LastWeight) { bar.Renderer.sprite=BarArt[weight]; bar.LastWeight=weight; }
                float y=_bridge.TimeY(_timeline.PulseTimes[index]-now);
                float scale=Gh3HighwayLayout.BarScale(y);
                UnityIcalls.SetPosition(bar.Transform,_bridge.World(Gh3HighwayLayout.CenterX,y));
                UnityIcalls.SetRotation(bar.Transform,_bridge.Rotation);
                Vector2 pixel=_bridge.PixelWorldSize;
                UnityIcalls.SetLocalScale(bar.Transform,new Vector3{ x=pixel.x*scale/bar.ParentScale.x, y=pixel.y*scale/bar.ParentScale.y, z=1f });
                bar.Renderer.color=new Color{ r=1f, g=1f, b=1f, a=_bridge.AlphaAtY(y) };
            }
            if(_eighths == null) return;
            _eighths.Begin();
            int start=_timeline.FirstVisible(now-window*.125);
            for(int i=start;i<_timeline.Bars.Length && _timeline.Bars[i].Time<=now+window;i++)
            {
                Gh3BeatTimeline.Bar bar=_timeline.Bars[i];
                if(!bar.Synthetic) continue;
                float y=_bridge.TimeY(bar.Time-now), scale=Gh3HighwayLayout.BarScale(y);
                _eighths.Bar(y,_eighthWidth*scale,_eighthHeight*scale,new Color32(255,255,255,255));
            }
            _eighths.Upload();
        }

        private readonly int[] _weights;

        private void DrawStatic(bool layoutChanged)
        {
            float topY=_bridge.TopY;
            // extend along the authored rays, without changing their angle or near anchors.
            // Mathf.Max(a,b) is maxss (a > b ? a : b) in GameAssembly; the interop call boxes.
            float stringRatio=(Gh3HighwayLayout.Playline-topY)/Gh3HighwayLayout.Height;
            float sideRatio=(742.5f-topY)/(742.5f-Gh3HighwayLayout.TopY);
            float stringLengthScale=1f > stringRatio ? 1f : stringRatio;
            float sideLengthScale=1f > sideRatio ? 1f : sideRatio;
            if(layoutChanged && _strings != null)
            {
                _strings.Begin();
                for(int lane=0;lane<5;lane++)
                {
                    float top=Gh3HighwayLayout.LaneX(lane,305f), bottom=Gh3HighwayLayout.LaneX(lane,655f);
                    _strings.Strip(bottom,655f,top-bottom,-350f,_stringHeight*.8f*stringLengthScale,_stringWidth*.65000004f,false,new Color32(200,200,200,200));
                }
                _strings.Upload();
            }
            if(_sides != null)
            {
                byte red=(byte)(_pulse>0 && (_pulse&1)==0 ? 192 : 255);
                _sides.Begin();
                _sides.Strip(336f,742.5f,176f,-350f,_sideHeight,_sideWidth*.3f,false,new Color32(red,255,255,255),
                    _sideCoverage.x,_sideCoverage.y,sideLengthScale);
                _sides.Strip(944f,742.5f,-176f,-350f,_sideHeight,_sideWidth*.3f,true,new Color32(red,255,255,255),
                    _sideCoverage.x,_sideCoverage.y,sideLengthScale);
                _sides.Upload();
            }
            if(!layoutChanged || _markerMesh == null) return;
            _markerMesh.Begin();
            _markerMesh.Bar(655f,512f,1f,new Color32(0,255,0,255));
            _markerMesh.Bar(topY,Gh3HighwayLayout.WidthAtY(topY),1f,new Color32(255,0,0,255));
            _markerMesh.Strip(640f,655f,0f,-1f,Gh3HighwayLayout.Playline-topY,1f,false,new Color32(255,255,0,255));
            _markerMesh.Upload();
        }

        public void Dispose()
        {
            if(_disposed) return;
            _disposed=true;
            _noteProjection.Dispose();
            _backing.Dispose();
            _markerMesh?.Dispose(); _strings?.Dispose(); _sides?.Dispose(); _eighths?.Dispose();
            foreach(HiddenRenderer hidden in _hidden)
                if(hidden.Renderer != null) hidden.Renderer.forceRenderingOff=hidden.WasOff;
            foreach(NativeBar bar in _nativeBars)
            {
                if(bar.Renderer == null) continue;
                bar.Renderer.sprite=bar.Sprite; bar.Renderer.sharedMaterial=bar.Material;
                bar.Transform.localRotation=bar.Rotation; bar.Transform.localScale=bar.Scale;
            }
        }
    }
}
