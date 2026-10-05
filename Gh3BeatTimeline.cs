using System;
using System.Collections.Generic;

namespace ClonZones
{
    internal sealed class Gh3BeatTimeline
    {
        internal readonly struct Tempo
        {
            public readonly long Tick;
            public readonly double Time, Bpm;
            public Tempo(long tick, double time, double bpm) { Tick=tick; Time=time; Bpm=bpm; }
        }

        internal readonly struct Bar
        {
            public readonly double Time;
            public readonly int Weight;
            public readonly bool Synthetic;
            public Bar(double time,int weight,bool synthetic=false) { Time=time; Weight=weight; Synthetic=synthetic; }
        }

        public readonly double[] PulseTimes;
        public readonly Bar[] Bars;

        public Gh3BeatTimeline(double[] times, int[] weights, long[] measures, Tempo[] tempos, double resolution, double timingResolution)
        {
            PulseTimes=times;
            List<Bar> bars=new List<Bar>(times.Length);
            for (int i=0;i<times.Length;i++) bars.Add(new Bar(times[i],weights[i]));
            // The game's tick list contains measures, not one tick per rendered bar.
            // Anchor the eighth grid at each measure, including time-signature changes.
            if (times.Length>0 && tempos.Length>0 && resolution>0 && timingResolution>0)
            {
                int tempoIndex=0, existing=0;
                // playback speed changes tick-to-time conversion, not the musical tick grid.
                double step=resolution*0.5;
                for(int measure=0;measure<measures.Length;measure++)
                {
                    double endTick=measure+1<measures.Length ? measures[measure+1] : double.PositiveInfinity;
                    for(double tick=measures[measure];tick<endTick;tick+=step)
                    {
                        while(tempoIndex+1<tempos.Length && tempos[tempoIndex+1].Tick<=tick) tempoIndex++;
                        Tempo tempo=tempos[tempoIndex];
                        double time=tempo.Time+(tick-tempo.Tick)*60.0/(tempo.Bpm*timingResolution);
                        if(time>times[times.Length-1]) break;
                        while(existing<times.Length && times[existing]<time-1e-7) existing++;
                        if(tempo.Bpm<=180.0 && (existing>=times.Length || Math.Abs(times[existing]-time)>1e-7))
                            bars.Add(new Bar(time,2,true));
                    }
                }
            }
            bars.Sort((a,b)=>a.Time.CompareTo(b.Time));
            Bars=bars.ToArray();
        }

        public int FirstVisible(double time)
        {
            int lo=0,hi=Bars.Length;
            while(lo<hi) { int mid=lo+(hi-lo)/2; if(Bars[mid].Time<time) lo=mid+1; else hi=mid; }
            return lo;
        }

        public int PulsesFired(double time)
        {
            int lo=0,hi=PulseTimes.Length;
            while(lo<hi) { int mid=lo+(hi-lo)/2; if(PulseTimes[mid]<=time) lo=mid+1; else hi=mid; }
            return lo;
        }

        public int MaximumVisible(double duration)
        {
            int first=0,max=0;
            for(int last=0;last<Bars.Length;last++)
            {
                while(Bars[last].Time-Bars[first].Time>duration) first++;
                max=Math.Max(max,last-first+1);
            }
            return max;
        }
    }
}
