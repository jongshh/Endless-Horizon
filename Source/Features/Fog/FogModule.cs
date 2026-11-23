using System;
using System.Collections.Generic;
using Verse;

namespace EndlessHorizon
{
    internal sealed class FogModule : ILiveModule
    {
        private readonly Map map;
        private readonly HorizonDomain domain;
        private readonly LandGrid grid;
        private readonly Func<MountainMask> mountains;
        private readonly Func<int, int, byte> edgeState;
        private bool[] outsideFog;
        private OutsideFogRegions outsideFogRegions;
        private bool enclosureCheck;

        internal FogModule(Map map, HorizonDomain domain, LandGrid grid, Func<MountainMask> mountains, Func<int, int, byte> edgeState)
        {
            this.map = map;
            this.domain = domain;
            this.grid = grid;
            this.mountains = mountains;
            this.edgeState = edgeState;
        }

        internal void Invalidate() => outsideFog = null;

        internal bool FogAt(IntVec3 c)
        {
            if (!domain.Outside(c)) return false;
            if (outsideFog == null)
            {
                outsideFogRegions ??= new OutsideFogRegions(domain, (x, z) => mountains() != null && mountains().IsRock(x, z));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                outsideFog = outsideFogRegions.Compute(edgeState);
                HorizonPerf.Record("outside fog", sw.Elapsed.TotalMilliseconds);
            }
            return outsideFog[grid.Index(c.x, c.z)];
        }

        internal bool Fogged(IntVec3 c)
        {
            if (!FogAt(c)) return false;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = c + GenAdj.AdjacentCells[i];
                if (!domain.Inside(n) || map.fogGrid.IsFogged(n)) continue;
                Building b = n.GetEdifice(map);
                if (b == null || !b.def.mineable) return false;
            }
            return true;
        }

        internal bool EnclosureStale()
        {
            MountainMask m = mountains();
            if (m == null) return false;
            foreach (IntVec3 c in m.ClosedValleys)
                if (!map.fogGrid.IsFogged(c)) return true;
            return OutsideEnclosure.Broken(domain, m.IsRock, edgeState, (x, z) => FogAt(new IntVec3(x, 0, z)));
        }

        internal int FogWithoutMountain()
        {
            MountainMask m = mountains();
            if (m == null) return 0;
            int r = grid.R, count = 0;
            for (int z = -r; z < domain.H + r; z++)
                for (int x = -r; x < domain.W + r; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (domain.Inside(c) || m.IsRock(x, z) || FogAt(c)) continue;
                    for (int i = 0; i < 8; i++)
                    {
                        IntVec3 n = c + GenAdj.AdjacentCells[i];
                        if (domain.Inside(n) ? map.fogGrid.IsFogged(n) : FogAt(n)) { count++; break; }
                    }
                }
            return count;
        }

        string ILiveModule.Name => "fog";

        string ILiveModule.OnEdit(IntVec3 cell, bool surfaceChanged)
        {
            Invalidate();
            enclosureCheck = true;
            return null;
        }

        string ILiveModule.RebuildReason(bool rebuildRequested)
        {
            if (!enclosureCheck) return null;
            enclosureCheck = false;
            return !rebuildRequested && EnclosureStale() ? "outside fog enclosure" : null;
        }

        bool ILiveModule.Due() => false;
        bool ILiveModule.Immediate => false;
        IEnumerable<bool> ILiveModule.UpdateSteps(System.Collections.Generic.List<IntVec3> changed) { yield break; }
        bool ILiveModule.ChangesWater => false;
        void ILiveModule.Announce(CellRect changed, Action<string, CellRect> publish) { }
        bool ILiveModule.Busy => enclosureCheck;
    }
}
