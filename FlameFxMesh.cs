using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClonZones
{
    internal sealed class FlameFxMesh : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex { public Vector3 Position; public Color32 Color; public Vector2 UV; }
        private GameObject _object;
        private Mesh _mesh;
        private MeshRenderer _renderer;
        private Material _material;
        private readonly FlameFxAssets _assets;
        private readonly Vertex[] _vertices = new Vertex[(FlameFxSimulation.MaxParticles + FlameFxSimulation.MaxSprites) * 6];
        private Il2CppStructArray<int> _indices;
        private Il2CppSystem.Array _indexArray;
        private readonly int _atlasWidth, _atlasHeight;
        private int _count, _lastCount = -1;
        private readonly int[] _spriteOrder = new int[FlameFxSimulation.MaxSprites];
        private bool _enabled, _disposed;
        private long _traceFrame = -1;
        private Vector3 _min, _max;
        private FlameFxProjection _projection;
        public FlameFxMesh(FlameFxAssets assets, Renderer template)
        {
            _assets = assets; _atlasWidth = assets.Atlas.width; _atlasHeight = assets.Atlas.height;
            try
            {
                _object = new GameObject("ClonZones_NoteFX") { layer = template.gameObject.layer };
                _mesh = new Mesh { name = "ClonZones_NoteFX" }; _mesh.MarkDynamic();
                _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
                _renderer = _object.AddComponent<MeshRenderer>();
                _material = new Material(assets.Shader)
                {
                    name = "ClonZones_NoteFX_Add", mainTexture = assets.Atlas,
                    renderQueue = template.sharedMaterial.renderQueue, hideFlags = HideFlags.DontUnloadUnusedAsset
                };
                // Legacy additive multiplies its colour by 2. The neutral tint
                // is .5, including alpha; keeping texture alpha is essential.
                Color neutral = default; neutral.r=neutral.g=neutral.b=neutral.a=.5f;
                _material.SetColor("_TintColor", neutral);
                _material.DisableKeyword("SOFTPARTICLES_ON");
                _material.DisableKeyword("FOG_LINEAR");
                _material.DisableKeyword("FOG_EXP");
                _material.DisableKeyword("FOG_EXP2");
                _renderer.sharedMaterial = _material;
                _renderer.sortingLayerID = template.sortingLayerID;
                _renderer.sortingOrder = template.sortingOrder;
                _renderer.shadowCastingMode = ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
                _renderer.lightProbeUsage = LightProbeUsage.Off;
                _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                _renderer.enabled = false;
                _indices = new Il2CppStructArray<int>(_vertices.Length);
                for (int i=0; i<_vertices.Length; ++i) _indices[i]=i;
                _indexArray = _indices.Cast<Il2CppSystem.Array>();
                _mesh.indexFormat = IndexFormat.UInt32;
                _mesh.SetVertexBufferParams(_vertices.Length, new VertexAttributeDescriptor[] {
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                    new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 0),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 0)
                });
            }
            catch { Dispose(); throw; }
        }
        public void Draw(FlameFxSimulation sim, FlameFxProjection projection)
        {
            if (_disposed) return;
            _projection=projection; _count=0;
            // Native NoteFX: sparks at z=8, hit sprites at z=10. Keep that order
            // in one atlas batch rather than grouping all warm/cyan textures.
            for (int e=0; e<FlameFxSimulation.MaxEmitters; ++e)
                for (int i=0; i<sim.EmitterParticleCount(e); ++i)
                {
                    FlameFxParticle p = sim.PresentedParticle(e,i);
                    float w=_assets.Spark.Width*p.Scale, h=_assets.Spark.Height*p.Scale;
                    Color32 color=default; color.r=p.R; color.g=p.G; color.b=p.B; color.a=p.A;
                    Quad(p.X-w*.5f,p.Y-h*.5f,w,h,_assets.Spark,0,1,1,color);
                    if(sim.Trace!=null && _traceFrame!=(long)(sim.Now*60))
                        sim.Diagnostic("vertex-particle",p.EventSerial,p.Lane,e,p.Serial,p.X,p.Y);
                }
            // Recycling a slot is not a draw-order change.
            int liveSprites = sim.GetSpriteOrder(_spriteOrder);
            for (int k=0; k<liveSprites; ++k)
            {
                int i=_spriteOrder[k];
                ref readonly FlameFxSprite f=ref sim.Sprite(i);
                FlameFxAssets.Image image=f.Star ? _assets.Star : _assets.Normal;
                float w=image.Width/4f,h=image.Height/4f;
                Color32 white=default; white.r=white.g=white.b=white.a=255;
                // Center-bottom, scale 1; no CH template rotation or arbitrary PPU.
                Quad(f.X-w*.5f,f.Y-h,w,h,image,f.Frame(sim.Now),4,4,white);
                if(sim.Trace!=null && _traceFrame!=(long)(sim.Now*60))
                    sim.Diagnostic(f.Star?"vertex-star":"vertex-normal",f.EventSerial,f.Lane,i,f.Serial,f.X,f.Y,f.Frame(sim.Now));
            }
            Upload();
            _traceFrame=(long)(sim.Now*60);
        }
        private void Quad(float x,float y,float w,float h,FlameFxAssets.Image image,int frame,int cols,int rows,Color32 color)
        {
            if (_count+6>_vertices.Length) throw new InvalidOperationException("NoteFX mesh capacity contract failed.");
            float cellW=image.Width/(float)cols,cellH=image.Height/(float)rows;
            int col=frame%cols,row=frame/cols;
            float u0=(image.X+col*cellW)/_atlasWidth,u1=(image.X+(col+1)*cellW)/_atlasWidth;
            float v0=(image.Y+image.Height-row*cellH)/_atlasHeight;
            float v1=(image.Y+image.Height-(row+1)*cellH)/_atlasHeight;
            Vertex a=VertexAt(x,y,u0,v0,color), b=VertexAt(x+w,y,u1,v0,color);
            Vertex c=VertexAt(x+w,y+h,u1,v1,color), d=VertexAt(x,y+h,u0,v1,color);
            Add(a);Add(b);Add(c);Add(a);Add(c);Add(d);
        }
        private Vertex VertexAt(float x,float y,float u,float v,Color32 color)
        {
            Vertex vertex=default; vertex.Position=_projection.World(x,y); vertex.Color=color;
            vertex.UV.x=u;vertex.UV.y=v;return vertex;
        }
        private void Add(Vertex v)
        {
            if (_count==0) _min=_max=v.Position;
            else
            {
                if(v.Position.x<_min.x)_min.x=v.Position.x;if(v.Position.x>_max.x)_max.x=v.Position.x;
                if(v.Position.y<_min.y)_min.y=v.Position.y;if(v.Position.y>_max.y)_max.y=v.Position.y;
                if(v.Position.z<_min.z)_min.z=v.Position.z;if(v.Position.z>_max.z)_max.z=v.Position.z;
            }
            _vertices[_count++]=v;
        }
        private unsafe void Upload()
        {
            bool show=_count!=0;
            if (_enabled!=show) {_renderer.enabled=show;_enabled=show;}
            if(!show)return;
            fixed(Vertex* vertices=_vertices)
                _mesh.InternalSetVertexBufferData(0,(IntPtr)vertices,0,0,_count,sizeof(Vertex),MeshUpdateFlags.DontRecalculateBounds);
            if(_lastCount!=_count)
            {
                _mesh.SetIndicesImpl(0,MeshTopology.Triangles,IndexFormat.UInt32,_indexArray,0,_count,false,0);
                _lastCount=_count;
            }
            Bounds bounds=default;bounds.SetMinMax(_min,_max);_mesh.bounds=bounds;
        }
        public void Hide() {if(_renderer!=null)_renderer.enabled=false;_enabled=false;}
        public void Dispose()
        {
            if(_disposed)return;_disposed=true;Hide();
            if(_object!=null)UnityEngine.Object.Destroy(_object);
            if(_mesh!=null)UnityEngine.Object.Destroy(_mesh);
            if(_material!=null)UnityEngine.Object.Destroy(_material);
        }
    }
}
