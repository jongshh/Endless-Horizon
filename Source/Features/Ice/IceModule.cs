using System.Collections.Generic;
using System.Linq;
using Verse;

namespace EndlessHorizon
{
    internal sealed class IceModule : ILiveModule
    {
        private readonly HorizonDomain domain;
        private readonly LandGrid grid;
        private bool[] frozen;
        private bool requested;

        internal IceModule(HorizonDomain domain, LandGrid grid)
        {
            this.domain = domain;
            this.grid = grid;
        }

        internal OutsideIce Calculator { get; private set; }

        internal int FrozenCount => frozen?.Count(f => f) ?? 0;

        internal bool FrozenAt(IntVec3 c) => frozen != null && domain.Outside(c) && frozen[grid.Index(c.x, c.z)];

        internal List<IntVec3> SetFrozen(bool[] next)
        {
            var changed = new List<IntVec3>();
            for (int i = 0; i < next.Length; i++)
                if (next[i] != (frozen != null && frozen[i])) changed.Add(new IntVec3(i % grid.Width - grid.R, 0, i / grid.Width - grid.R));
            frozen = next;
            return changed;
        }

        internal void Build(Map map, MountainMask mountains)
        {
            Calculator = new OutsideIce(map, domain, grid, mountains, this);
            Calculator.Recalculate();
        }

        internal void Request()
        {
            if (Calculator != null) requested = true;
        }

        string ILiveModule.Name => "ice";
        string ILiveModule.OnEdit(IntVec3 cell, bool surfaceChanged) => null;
        string ILiveModule.RebuildReason(bool rebuildRequested) => null;
        bool ILiveModule.Due() => requested;
        bool ILiveModule.Immediate => true;

        IEnumerable<bool> ILiveModule.UpdateSteps(List<IntVec3> changed)
        {
            requested = false;
            changed.AddRange(Calculator.Recalculate());
            yield break;
        }

        bool ILiveModule.ChangesWater => true;

        void ILiveModule.Announce(CellRect changed, System.Action<string, CellRect> publish)
        {
            publish(Vocabulary.TerrainTop, changed);
            publish(Vocabulary.Frozen, domain.Bounds);
        }

        bool ILiveModule.Busy => false;
    }
}
