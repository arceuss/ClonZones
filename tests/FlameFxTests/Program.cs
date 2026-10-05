using System;
using System.IO;
using System.Globalization;
using ClonZones;
using UnityEngine;

static class Program
{
    private static int _passed;
    private static void Test(string name,Action action)
    {
        action();++_passed;Console.WriteLine("PASS "+name);
    }
    private static void Check(bool value,string why="assertion failed") {if(!value)throw new Exception(why);}
    private static void Near(double a,double b,double tolerance=1e-5) {Check(Math.Abs(a-b)<=tolerance,$"{a:R} != {b:R}");}
    private static void Throws<T>(Action action) where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
    private static FlameFxSimulation Start(PresentationStyle style=PresentationStyle.Gh3,ushort mask=2)
    {var s=new FlameFxSimulation(style,123);Check(s.Queue(mask,0));s.Advance(FlameFxProfile.StepSeconds,false);return s;}
    private static void Same(FlameFxSimulation a,FlameFxSimulation b)
    {
        Check(a.ParticleCount==b.ParticleCount && a.SpriteCount==b.SpriteCount && a.PendingCount==b.PendingCount);
        for(int e=0;e<FlameFxSimulation.MaxEmitters;++e)
        {
            Check(a.EmitterParticleCount(e)==b.EmitterParticleCount(e));
            for(int i=0;i<a.EmitterParticleCount(e);++i)
            {
                var p=a.Particle(e,i);var q=b.Particle(e,i);
                Check(BitConverter.SingleToInt32Bits(p.X)==BitConverter.SingleToInt32Bits(q.X));
                Check(BitConverter.SingleToInt32Bits(p.Y)==BitConverter.SingleToInt32Bits(q.Y));
                Check(p.A==q.A && p.R==q.R && p.G==q.G && p.B==q.B);
            }
        }
        for(int i=0;i<FlameFxSimulation.MaxSprites;++i)
            if(a.SpriteVisible(i)){Check(b.SpriteVisible(i));Check(a.Sprite(i).Frame(a.Now)==b.Sprite(i).Frame(b.Now));}
    }
    public static int Main()
    {
        string temp=Path.Combine(Path.GetTempPath(),"ClonZones_FlameTests_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Test("one-shot material never replays ignition after cell 15",()=>{var f=new FlameFxSprite{Born=0};Check(f.Frame(16.0/60)==15,"expired one-shot replayed its first cell");});
            Test("native countdown first poll and strict crossing",()=>{var w=FlameFxWait.Milliseconds(100);Check(!w.Poll(.2f));Check(w.Poll(.001f));w=FlameFxWait.Milliseconds(100);Check(!w.Poll(w.Remaining));Check(!w.Poll(0));Check(w.Poll(.001f));});
            Test("mixed HUD/flame styles",()=>{File.WriteAllText(Path.Combine(temp,"settings.ini"),"[presentation]\nhud_style=wormod\nflame_style=gh3\n");var p=PresentationSettings.Read(temp,null);Check(p.HudStyle==PresentationStyle.Wormod && p.FlameStyle==PresentationStyle.Gh3);});
            Test("reverse mixed styles",()=>{File.WriteAllText(Path.Combine(temp,"settings.ini"),"[presentation]\nhud_style=gh3\nflame_style=wormod\n");var p=PresentationSettings.Read(temp,null);Check(p.HudStyle==PresentationStyle.Gh3 && p.FlameStyle==PresentationStyle.Wormod);});
            Test("native emitter origin",()=>{var p=FlameFxParticleMath.Spawn(100,200,false,10,5120,32);Near(p.X,100);Near(p.Y,179.5);Near(p.VY,-10);});
            Test("velocity is per source update",()=>{var p=FlameFxParticleMath.Spawn(100,200,false,10,5120,0);Check(FlameFxParticleMath.Step(ref p,1f/60));Near(p.Y,170);Near(p.VY,-10+50f/60);});
            Test("WOR slower sparks",()=>{var p=FlameFxParticleMath.Spawn(100,200,false,4.8f,5120,0);Check(FlameFxParticleMath.Step(ref p,1f/60));Near(p.Y,175.2);});
            Test("normal 12-bit colour interpolation",()=>{var p=FlameFxParticleMath.Spawn(0,0,false,10,5120,0);FlameFxParticleMath.Step(ref p,.125f);Check(p.R==255 && p.G==64 && p.B==0 && p.A==127);Near(p.Scale,.75);});
            Test("SP colour separate from material tint",()=>{var p=FlameFxParticleMath.Spawn(0,0,true,10,5120,0);FlameFxParticleMath.Step(ref p,.125f);Check(p.R==0 && p.G==255 && p.B==255 && p.A==127);});
            Test("particle exact lifetime boundary",()=>{var p=FlameFxParticleMath.Spawn(0,0,false,10,5120,0);Check(FlameFxParticleMath.Step(ref p,.25f));Check(p.A==0);Check(!FlameFxParticleMath.Step(ref p,.0001f));});
            Test("bad RNG samples rejected",()=>{Throws<ArgumentOutOfRangeException>(()=>FlameFxParticleMath.Spawn(0,0,false,10,10240,0));});
            Test("caller waits for a logic poll, not a 60 Hz interval",()=>{var s=new FlameFxSimulation(PresentationStyle.Gh3);s.Queue(2,0);Check(s.SpriteCount==0);s.Advance(0,false);Check(s.SpriteCount==0);s.Advance(1.0/240,false);Check(s.SpriteCount==1);});
            Test("SP is sampled at delayed dispatch, not replayed into history",()=>{var s=Start();s.Queue(32,s.Now);s.Advance(.1,true);Check(!s.Sprite(0).Star && s.Sprite(1).Star);Near(s.Sprite(1).Born,s.Now);});
            Test("overlapping hits do not restart the old sprite",()=>{var s=Start();double born=s.Sprite(0).Born;s.Queue(2,s.Now);s.Advance(FlameFxProfile.StepSeconds,false);Check(s.SpriteCount==2);Near(s.Sprite(0).Born,born);});
            Test("chord dispatch",()=>{var s=Start(PresentationStyle.Gh3,0x3e);Check(s.SpriteCount==5);Near(s.Sprite(0).X,435.2,1e-4);Near(s.Sprite(4).X,844.8,1e-4);}); // anchors are float32
            Test("pure opens are not five fabricated GH3 flames",()=>{var s=Start(PresentationStyle.Gh3,1);Check(s.SpriteCount==0);});
            Test("mixed open chord retains closed lanes",()=>{var s=Start(PresentationStyle.Gh3,3);Check(s.SpriteCount==1);});
            Test("sprite does not receive particle offset",()=>{var s=Start();Near(s.Sprite(0).Y,655);});
            Test("first frame",()=>{var s=Start();Check(s.Sprite(0).Frame(s.Now)==0);});
            Test("sheet frame rate, not normalised life",()=>{var s=Start(PresentationStyle.Wormod);s.Advance(5.0/60,false);Check(s.Sprite(0).Frame(s.Now)==5);});
            Test("sequential converted waits retain style-specific cleanup boundary",()=>{var a=Start();var b=Start(PresentationStyle.Wormod);a.Advance(16.0/60,false);b.Advance(16.0/60,false);Check(a.SpriteCount==1 && b.SpriteCount==1);a.Advance(1.0/60,false);b.Advance(1.0/60,false);Check(a.SpriteCount==0 && b.SpriteCount==1);b.Advance(1.0/60,false);Check(b.SpriteCount==0);});
            Test("particle drain outlives flame",()=>{var s=Start();s.Advance(17.0/60,false);Check(s.SpriteCount==0 && s.ParticleCount>0);s.Advance(.2,false);Check(s.ParticleCount==0);});
            Test("paused delta preserves state",()=>{var s=Start();s.Advance(.05,false);int p=s.ParticleCount;double n=s.Now;for(int i=0;i<200;++i)s.Advance(0,true);Check(s.ParticleCount==p && s.Now==n);});
            Test("empty simulation clock still advances",()=>{var s=new FlameFxSimulation(PresentationStyle.Gh3);s.Advance(1,false);Near(s.Now,1);});
            Test("restart drops old epoch",()=>{var s=Start();s.Advance(.05,false);s.Clear();Check(s.ParticleCount==0 && s.SpriteCount==0 && s.Now==0);s.Queue(2,0);s.Advance(FlameFxProfile.StepSeconds,true);Check(s.SpriteCount==1 && s.Sprite(0).Star);});
            Test("pause holds pending dispatch and seek discards it",()=>
            {
                var s=Start();s.Queue(32,s.Now);double now=s.Now;
                s.Advance(0,true);Check(s.PendingCount==1 && s.SpriteCount==1);Near(s.Now,now);
                s.Clear();s.Queue(4,0);s.Advance(.001,true);
                Check(s.PendingCount==0 && s.SpriteCount==1 && s.Sprite(0).Lane==1 && s.Sprite(0).Star);
            });
            Test("bad delta rejected",()=>{var s=Start();Throws<ArgumentOutOfRangeException>(()=>s.Advance(-1,false));Throws<ArgumentOutOfRangeException>(()=>s.Advance(double.NaN,false));});
            Test("source emitter budget does not delete hit sprites",()=>{var s=new FlameFxSimulation(PresentationStyle.Gh3);for(int i=0;i<81;++i)s.Queue(2,0);s.Advance(FlameFxProfile.StepSeconds,false);Check(s.DroppedEmitters==1 && s.SpriteCount==81);});
            Test("event queue reports overflow",()=>{var s=new FlameFxSimulation(PresentationStyle.Gh3);for(int i=0;i<FlameFxSimulation.MaxPending;++i)Check(s.Queue(2,1));Check(!s.Queue(2,1) && s.DroppedPending==1);});
            foreach(int hz in new[]{60,120,144,165,180,240,360,1200})
            {
                int rate=hz;
                Test("fixed simulation independent of render Hz "+rate,()=>
                {
                    var a=Start(PresentationStyle.Wormod,0x3e);var b=Start(PresentationStyle.Wormod,0x3e);
                    a.Advance(.2,true);
                    double remaining=.2;
                    while(remaining>0){double dt=Math.Min(remaining,1.0/rate);b.Advance(dt,true);remaining-=dt;}
                    Same(a,b);
                });
            }
            Test("stable sprite creation order after reuse",()=>{var s=Start();s.Advance(.5,false);for(int k=0;k<300;++k){s.Queue(2,s.Now);s.Advance(.005,false);}int[] order=new int[FlameFxSimulation.MaxSprites];int n=s.GetSpriteOrder(order);for(int i=1;i<n;++i)Check(s.Sprite(order[i-1]).Serial<s.Sprite(order[i]).Serial);});
            Test("alternating lanes retain captured anchors and independent material phase",()=>
            {
                var s=Start(PresentationStyle.Wormod);var green=s.Sprite(0);
                s.Queue(32,s.Now);s.SetLaneAnchor(4,-100,200);s.Advance(1.0/60,false);
                Check(s.Sprite(0).Serial==green.Serial && s.Sprite(0).Lane==0 && s.Sprite(1).Lane==4);
                Near(s.Sprite(0).X,green.X);Near(s.Sprite(1).X,844.8,1e-4);
                Check(s.Sprite(0).Frame(s.Now)==1 && s.Sprite(1).Frame(s.Now)==0);
            });
            Test("old cleanup cannot touch reused sprite emitter or epoch",()=>
            {
                var s=Start();var old=s.Sprite(0).Context;s.Advance(.5,false);
                for(int i=0;i<FlameFxSimulation.MaxSprites;++i){s.Queue(32,s.Now);s.Advance(.001,false);s.DestroySprite(s.Sprite((i+1)%FlameFxSimulation.MaxSprites).Context.Sprite);}
                s.Queue(2,s.Now);s.Advance(.001,false);
                Check(!s.DestroySprite(old.Sprite));Check(!s.StopEmission(old.Emitter));
                var newer=s.Sprite(s.NextSpriteSlot==0?FlameFxSimulation.MaxSprites-1:s.NextSpriteSlot-1).Context;
                s.Clear();s.Queue(32,0);s.Advance(.01,false);
                Check(!s.DestroySprite(newer.Sprite) && !s.StopEmission(newer.Emitter));Check(s.SpriteCount==1);
            });
            Test("compaction carries previous position with particle identity",()=>
            {
                var s=Start();s.Advance(.1,false);
                var before=new System.Collections.Generic.Dictionary<long,FlameFxParticle>();
                for(int i=0;i<s.EmitterParticleCount(0);++i)before.Add(s.Particle(0,i).Serial,s.Particle(0,i));
                long first=s.Particle(0,0).Serial;s.Advance(.19,false);
                Check(s.EmitterParticleCount(0)>0 && s.Particle(0,0).Serial!=first,"expiry did not exercise swap removal");
                for(int i=0;i<s.EmitterParticleCount(0);++i)
                {
                    var p=s.Particle(0,i);var expected=before[p.Serial];
                    for(int step=0;step<11;++step)Check(FlameFxParticleMath.Step(ref expected,1f/60));
                    Near(p.PreviousX,expected.PreviousX);Near(p.PreviousY,expected.PreviousY);
                    var view=s.PresentedParticle(0,i);Check(view.Serial==p.Serial);
                    Near(view.X,p.PreviousX+(p.X-p.PreviousX)*.4f,1e-4);
                }
            });
            Test("one emission stop drains and cannot restart",()=>
            {
                var s=Start();s.Advance(.05,false);var h=s.Sprite(0).Context.Emitter;int count=s.ParticleCount;
                Check(count>0 && s.StopEmission(h));Check(!s.StopEmission(h));s.Advance(.02,false);Check(s.ParticleCount==count);
                s.Advance(.5,false);Check(s.ParticleCount==0);
            });
            Test("destroying sprite does not cancel emitter cleanup script",()=>
            {
                var s=Start();var h=s.Sprite(0).Context;Check(s.DestroySprite(h.Sprite));
                s.Advance(.05,false);Check(s.SpriteCount==0 && s.ParticleCount>0);
                s.Advance(.5,false);Check(s.ParticleCount==0 && !s.StopEmission(h.Emitter));
            });
            Test("live FPS transitions and hitch preserve recurrence",()=>
            {
                var a=Start(PresentationStyle.Wormod,0x3e);var b=Start(PresentationStyle.Wormod,0x3e);
                double total=0;foreach(int rate in new[]{120,240,60})for(int i=0;i<6;++i){double dt=1.0/rate;b.Advance(dt,false);total+=dt;}
                a.Advance(total,false);Same(a,b);a.Advance(.5,false);b.Advance(.5,false);Same(a,b);Check(a.ParticleCount==0);
            });
            string theme=Path.Combine(temp,"theme"),fallback=Path.Combine(temp,"fallback");
            Directory.CreateDirectory(Path.Combine(fallback,"fx","gh3"));Directory.CreateDirectory(Path.Combine(fallback,"fx","wormod"));
            string fg=Path.Combine(fallback,"fx","gh3","note_hit.png"),fw=Path.Combine(fallback,"fx","wormod","note_hit.png");
            File.WriteAllText(fg,"g");File.WriteAllText(fw,"w");
            Test("same-style fallback paths",()=>{Check(FlameAssetPaths.Resolve(theme,fallback,PresentationStyle.Gh3,"note_hit.png")==fg);Check(FlameAssetPaths.Resolve(theme,fallback,PresentationStyle.Wormod,"note_hit.png")==fw);});
            Test("no cross-style fallback",()=>{File.Delete(fw);Throws<FileNotFoundException>(()=>FlameAssetPaths.Resolve(theme,fallback,PresentationStyle.Wormod,"note_hit.png"));File.WriteAllText(fw,"w");});
            Directory.CreateDirectory(Path.Combine(theme,"FX"));string shared=Path.Combine(theme,"FX","note_hit.png");File.WriteAllText(shared,"shared");
            Test("existing zone FX overrides still work",()=>{Check(FlameAssetPaths.Resolve(theme,fallback,PresentationStyle.Wormod,"note_hit.png")==shared);});
            Directory.CreateDirectory(Path.Combine(theme,"FX","wormod"));string scoped=Path.Combine(theme,"FX","wormod","note_hit.png");File.WriteAllText(scoped,"scoped");
            Test("scoped theme beats shared theme",()=>{Check(FlameAssetPaths.Resolve(theme,fallback,PresentationStyle.Wormod,"note_hit.png")==scoped);Check(FlameAssetPaths.Resolve(theme,fallback,PresentationStyle.Gh3,"note_hit.png")==shared);});
            Matrix4x4 m=default;m.m00=m.m11=m.m22=m.m33=1;
            Rect rect=new Rect{x=0,y=0,width=1280,height=720};
            Test("projection anchor and Y direction",()=>{var p=new FlameFxProjection(m,640,65,1,1,0,rect);Near(p.World(640,655).x,0);Near(p.World(640,655).y,65f*2/720-1);Check(p.World(640,635).y>p.World(640,655).y);});
            Test("projective divide retained",()=>{var q=m;q.m30=.3f;var p=new FlameFxProjection(q,640,65,1,1,.5f,rect);float nx=100f*2/1280;Near(p.World(740,655).x,nx/(1+.3f*nx));});
            Test("projection changes move presentation without reassigning live lanes",()=>
            {
                var s=Start(PresentationStyle.Gh3,34);var green=s.Sprite(0);var orange=s.Sprite(1);
                var before=new FlameFxProjection(m,640,65,1,1,0,rect);
                var after=new FlameFxProjection(m,700,80,.75f,.75f,0,rect);
                var g=after.World(green.X,green.Y);var o=after.World(orange.X,orange.Y);
                Check(g.x<o.x && g.x!=before.World(green.X,green.Y).x);
                Near((g.x+o.x)*.5,700f*2/1280-1,1e-4);
                Near(o.x-g.x,(orange.X-green.X)*.75f*2/1280,1e-4);
                Check(s.Sprite(0).Serial==green.Serial && s.Sprite(0).Lane==0);
                Check(s.Sprite(1).Serial==orange.Serial && s.Sprite(1).Lane==4);
            });
            Test("measured fret correspondence preserves native flame aspect and pivot",()=>
            {
                var viewport=new Rect{width=1920,height=1080};
                var first=new Vector3{x=664.56f,y=90.62f};var last=new Vector3{x=1255.44f,y=90.62f};
                var projection=FlameFxProjection.AtFrets(m,first,last,0,viewport);
                var anchor=projection.VirtualPoint(first.x,first.y);
                var bottom=projection.World(anchor.x,anchor.y);
                var top=projection.World(anchor.x,anchor.y-256);
                var right=projection.World(anchor.x+128,anchor.y);
                Near((bottom.x+1)*960,first.x,.0001);Near((bottom.y+1)*540,first.y,.0001);
                // Native centers are 652.8..1267.2 at 1080p. Scale both axes by
                // the measured CH/native fret-span ratio, not by highway height.
                double scale=(1255.44-664.56)/409.6;
                Near((right.x-bottom.x)*960,128*scale,.0001);
                Near((top.y-bottom.y)*540,256*scale,.0001);
            });
            Test("mirrored fret order does not mirror or invert flame artwork",()=>
            {
                var projection=FlameFxProjection.AtFrets(m,new Vector3{x=900,y=80},new Vector3{x=380,y=80},0,rect);
                var first=projection.VirtualPoint(900,80);var last=projection.VirtualPoint(380,80);
                Check(first.x>last.x);
                Check(projection.World(first.x+10,first.y).x>projection.World(first.x,first.y).x);
                Check(projection.World(first.x,first.y-10).y>projection.World(first.x,first.y).y);
            });
            Test("bad viewport rejected",()=>{var r=rect;r.width=0;Throws<InvalidOperationException>(()=>new FlameFxProjection(m,640,65,1,1,0,r));});
            Test("nonfinite projection rejected",()=>{var q=default(Matrix4x4);var p=new FlameFxProjection(q,640,65,1,1,0,rect);Throws<InvalidOperationException>(()=>p.World(640,655));});
            Test("native-arithmetic C oracle fixtures",()=>
            {
                int count=0;
                foreach(string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory,"particle_oracle.csv")))
                {
                    if(line.Length==0 || line[0]=='#')continue;string[] f=line.Split(',');
                    float speed=float.Parse(f[0],CultureInfo.InvariantCulture);bool star=f[1]=="1";
                    int a=int.Parse(f[2]),r=int.Parse(f[3]),steps=int.Parse(f[4]);
                    var p=FlameFxParticleMath.Spawn(640,655,star,speed,a,r);
                    bool alive=true;for(int i=0;i<steps && alive;++i)alive=FlameFxParticleMath.Step(ref p,1f/60);
                    Check(alive==(f[5]=="1"));Near(p.X,float.Parse(f[6],CultureInfo.InvariantCulture),.00015);Near(p.Y,float.Parse(f[7],CultureInfo.InvariantCulture),.00015);
                    Check(p.R==byte.Parse(f[8]) && p.G==byte.Parse(f[9]) && p.B==byte.Parse(f[10]) && p.A==byte.Parse(f[11]));
                    Near(p.Scale,float.Parse(f[12],CultureInfo.InvariantCulture));++count;
                }
                Check(count==1440,"oracle fixture count changed");
            });
            Console.WriteLine($"{_passed} cases passed.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally {Directory.Delete(temp,true);}
    }
}
