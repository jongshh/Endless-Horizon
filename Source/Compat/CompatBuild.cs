using System;
using System.Collections.Generic;
using System.Diagnostics;
using Verse;

namespace EndlessHorizon
{
    public interface ICompatBuild
    {
        bool Step(double budgetMs);
        IOutsideCompatLayer Result { get; }
        HashSet<SectionLayer> Reused { get; }
        void Abort();
        double SpentMs { get; }
        double LongestStepMs { get; }
    }

    public interface ICompatWatch
    {
        void Poll(HorizonState s, List<CellRect> areas, ref bool global);

        bool Relevant(HorizonState s, IntVec3 loc);
    }

    public class StripLayer : IOutsideCompatLayer
    {
        public readonly List<(CellRect rect, SectionLayer layer)> Layers;

        public StripLayer(List<(CellRect, SectionLayer)> layers) => Layers = layers;

        public virtual int Draw(CellRect view) => CompatSections.DrawLayers(Layers, view);

        public void Dispose() => DisposeExcept(null);

        public void DisposeExcept(HashSet<SectionLayer> keep)
        {
            foreach ((CellRect _, SectionLayer layer) in Layers)
            {
                if (keep != null && keep.Contains(layer)) continue;
                foreach (LayerSubMesh sm in layer.subMeshes)
                {
                    if (sm.mesh != null) UnityEngine.Object.Destroy(sm.mesh);
                }
            }
            Layers.Clear();
        }
    }

    public sealed class StripBuild<T> : ICompatBuild where T : class
    {
        public const int ReuseDistance = 40;

        public sealed class Holder
        {
            public T Value;
        }

        private readonly HorizonState state;
        private readonly IEnumerator<bool> field;
        private readonly Holder holder = new Holder();
        private readonly Func<T, CellRect, bool> wanted;
        private readonly Action<T> enter;
        private readonly Action exit;
        private readonly Type layerType;
        private readonly Func<List<(CellRect, SectionLayer)>, StripLayer> make;
        private readonly Action<T> learned;
        private readonly StripLayer previous;
        private readonly List<CellRect> areas;
        private List<CellRect> strips;
        private int next;
        private bool fieldDone, done;
        private readonly List<(CellRect, SectionLayer)> built = new List<(CellRect, SectionLayer)>();
        private readonly List<SectionLayer> made = new List<SectionLayer>();

        public IOutsideCompatLayer Result { get; private set; }
        public HashSet<SectionLayer> Reused { get; } = new HashSet<SectionLayer>();
        public double SpentMs { get; private set; }
        public double LongestStepMs { get; private set; }

        public StripBuild(HorizonState state, Func<Holder, IEnumerable<bool>> field, Func<T, CellRect, bool> wanted, Action<T> enter,
            Action exit, Type layerType, Func<List<(CellRect, SectionLayer)>, StripLayer> make, Action<T> learned,
            IOutsideCompatLayer previous, List<CellRect> areas)
        {
            this.state = state;
            this.field = field(holder).GetEnumerator();
            this.wanted = wanted;
            this.enter = enter;
            this.exit = exit;
            this.layerType = layerType;
            this.make = make;
            this.learned = learned;
            this.previous = previous as StripLayer;
            this.areas = areas;
        }

        public bool Step(double budgetMs)
        {
            if (done) return true;
            var sw = Stopwatch.StartNew();
            try
            {
                while (!fieldDone)
                {
                    if (!field.MoveNext())
                    {
                        fieldDone = true;
                        learned?.Invoke(holder.Value);
                        if (holder.Value == null)
                        {
                            Finish();
                            return true;
                        }
                        strips = CompatSections.OutsideStrips(state.map, state.RingWidth);
                        break;
                    }
                    if (sw.Elapsed.TotalMilliseconds >= budgetMs) return false;
                }
                while (next < strips.Count)
                {
                    CellRect strip = strips[next++];
                    if (!wanted(holder.Value, strip)) continue;
                    SectionLayer old = Reusable(strip);
                    if (old != null)
                    {
                        built.Add((strip, old));
                        Reused.Add(old);
                        continue;
                    }
                    var layer = (SectionLayer)Activator.CreateInstance(layerType, CompatSections.StandIn(state.map, strip));
                    built.Add((strip, layer));
                    made.Add(layer);
                    enter(holder.Value);
                    try
                    {
                        layer.Regenerate();
                    }
                    finally
                    {
                        exit();
                    }
                    if (sw.Elapsed.TotalMilliseconds >= budgetMs) return false;
                }
                Finish();
                return true;
            }
            finally
            {
                double ms = sw.Elapsed.TotalMilliseconds;
                SpentMs += ms;
                if (ms > LongestStepMs) LongestStepMs = ms;
            }
        }

        private SectionLayer Reusable(CellRect strip)
        {
            if (areas == null || previous == null) return null;
            foreach (CellRect a in areas)
            {
                if (CompatSections.Gap(a, strip) <= ReuseDistance) return null;
            }
            foreach ((CellRect rect, SectionLayer layer) in previous.Layers)
            {
                if (rect == strip) return layer;
            }
            return null;
        }

        private void Finish()
        {
            done = true;
            Result = built.Count == 0 ? null : make(built);
        }

        public void Abort()
        {
            var own = new List<(CellRect, SectionLayer)>();
            foreach (SectionLayer l in made) own.Add((default, l));
            CompatSections.Destroy(own);
            built.Clear();
            made.Clear();
            Reused.Clear();
            done = true;
        }
    }
}
