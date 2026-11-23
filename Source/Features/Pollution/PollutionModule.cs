using System;
using System.Collections.Generic;
using Verse;

namespace EndlessHorizon
{
    internal sealed class PollutionModule : ILiveModule
    {
        private const int PollutionCheckTicks = 600;
        private readonly Map map;
        private readonly HorizonDomain domain;
        private readonly LandGrid grid;
        private bool[] pollutionBase;
        private bool pollutionRead;
        private int[] pollutionSource;
        private bool[] polluted;
        private int pollutionTotal, nextPollutionCheck;
        private Func<IntVec3, int> mirror;

        internal PollutionModule(Map map, HorizonDomain domain, LandGrid grid)
        {
            this.map = map;
            this.domain = domain;
            this.grid = grid;
        }

        internal IEnumerable<bool> ReadSteps()
        {
            if (!ModsConfig.BiotechActive || map.pollutionGrid == null)
            {
                pollutionRead = true;
                yield break;
            }
            int total = map.pollutionGrid.TotalPollution;
            if (total == 0)
            {
                pollutionBase = null;
                pollutionTotal = 0;
                pollutionRead = true;
                yield break;
            }
            int w = domain.W, h = domain.H;
            var value = new bool[w * h];
            var seen = new bool[value.Length];
            var queue = new Queue<int>();
            for (int i = 0; i < value.Length; i++)
            {
                if ((i & 2047) == 2047) yield return false;
                IntVec3 c = map.cellIndices.IndexToCell(i);
                if (!domain.Inside(c)) continue;
                bool dirty = map.pollutionGrid.IsPolluted(c);
                if (!dirty && !map.pollutionGrid.EverPollutable(c)) continue;
                value[i] = dirty;
                seen[i] = true;
                queue.Enqueue(i);
            }
            int walked = 0;
            while (queue.Count > 0)
            {
                if ((++walked & 2047) == 0) yield return false;
                int i = queue.Dequeue(), x = i % w, z = i / w;
                void Pass(int xx, int zz)
                {
                    if (xx < 0 || zz < 0 || xx >= w || zz >= h) return;
                    int j = zz * w + xx;
                    if (seen[j]) return;
                    seen[j] = true;
                    value[j] = value[i];
                    queue.Enqueue(j);
                }
                Pass(x - 1, z); Pass(x + 1, z); Pass(x, z - 1); Pass(x, z + 1);
            }
            pollutionBase = value;
            pollutionTotal = total;
            pollutionRead = true;
        }

        internal bool GroundPolluted(IntVec3 c, TerrainDef t, Func<IntVec3, int> mirrorIndex)
        {
            if (!pollutionRead)
            {
                foreach (bool step in ReadSteps()) { }
            }
            if (pollutionBase == null || t == null || !t.canBePolluted) return false;
            mirror ??= mirrorIndex;
            pollutionSource ??= new int[grid.Width * grid.Height];
            int i = grid.Index(c.x, c.z);
            if (pollutionSource[i] == 0) pollutionSource[i] = mirrorIndex(c) + 1;
            return pollutionBase[pollutionSource[i] - 1];
        }

        internal bool GroundPollutedAt(IntVec3 c) => polluted != null && domain.Outside(c) && polluted[grid.Index(c.x, c.z)];

        internal IEnumerable<bool> BuildSteps(Func<IntVec3, int> mirrorIndex)
        {
            mirror = mirrorIndex;
            if (!pollutionRead)
            {
                foreach (bool step in ReadSteps()) yield return step;
            }
            nextPollutionCheck = (Find.TickManager?.TicksGame ?? 0) + PollutionCheckTicks;
            if (pollutionBase == null) yield break;
            int r = grid.R;
            var fresh = new bool[grid.Width * grid.Height];
            for (int z = -r; z < domain.H + r; z++)
            {
                for (int x = -r; x < domain.W + r; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (!domain.Inside(c)) fresh[grid.Index(x, z)] = GroundPolluted(c, domain.InGrid(c) ? grid.Cells[grid.Index(x, z)] : null, mirrorIndex);
                }
                yield return false;
            }
            polluted = fresh;
        }

        internal bool Due()
        {
            if (!pollutionRead || !ModsConfig.BiotechActive || map.pollutionGrid == null) return false;
            int now = Find.TickManager.TicksGame;
            if (now < nextPollutionCheck) return false;
            nextPollutionCheck = now + PollutionCheckTicks;
            return map.pollutionGrid.TotalPollution != pollutionTotal;
        }

        internal IEnumerable<bool> UpdateSteps(List<IntVec3> changed, Func<IntVec3, int> mirrorIndex)
        {
            foreach (bool step in ReadSteps()) yield return step;
            int r = grid.R;
            var fresh = new bool[grid.Width * grid.Height];
            for (int z = -r; z < domain.H + r; z++)
            {
                for (int x = -r; x < domain.W + r; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (domain.Inside(c)) continue;
                    int i = grid.Index(x, z);
                    fresh[i] = GroundPolluted(c, grid.Cells[i], mirrorIndex);
                    if (fresh[i] != (polluted != null && polluted[i])) changed.Add(c);
                }
                yield return false;
            }
            polluted = fresh;
        }

        string ILiveModule.Name => "pollution";
        string ILiveModule.OnEdit(IntVec3 cell, bool surfaceChanged) => null;
        string ILiveModule.RebuildReason(bool rebuildRequested) => null;
        bool ILiveModule.Due() => Due();
        bool ILiveModule.Immediate => false;
        IEnumerable<bool> ILiveModule.UpdateSteps(List<IntVec3> changed) => UpdateSteps(changed, mirror);
        bool ILiveModule.ChangesWater => false;
        void ILiveModule.Announce(CellRect changed, Action<string, CellRect> publish) => publish(Vocabulary.Polluted, changed);
        bool ILiveModule.Busy => false;
    }
}
