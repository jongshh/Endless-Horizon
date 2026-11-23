using System.Collections.Generic;
using Verse;

namespace EndlessHorizon
{
    internal sealed class RoofModule
    {
        private const int MinRoofedCellsPerGroup = 20;
        private readonly Map map;
        private readonly HorizonDomain domain;
        private readonly LandGrid grid;
        private readonly GeneratorModule gen;
        private readonly RoadBuild roads;
        private readonly System.Func<MountainMask> mountains;
        private readonly int r, width, height;
        private bool[] thickRoof;
        private bool[] anyRoof;

        internal RoofModule(Map map, HorizonDomain domain, LandGrid grid, GeneratorModule gen, RoadBuild roads, System.Func<MountainMask> mountains)
        {
            this.map = map;
            this.domain = domain;
            this.grid = grid;
            this.gen = gen;
            this.roads = roads;
            this.mountains = mountains;
            r = grid.R;
            width = grid.Width;
            height = grid.Height;
        }

        private byte[] genRoofLevel => grid.GenRoofLevel;
        private int Index(int x, int z) => grid.Index(x, z);
        private GenReconstruction Generator => gen.Generator;
        private MountainMask Mountains => mountains();
        private byte TunnelRoof(IntVec3 c) => roads.TunnelRoof(c);
        private bool RoadCleared(IntVec3 c) => roads.RoadCleared(c);
        private int RawSampleAt(IntVec3 c) => domain.InGrid(c) ? grid.RawSamples[grid.Index(c.x, c.z)] : -1;

        public bool OutsideThickRoof(IntVec3 c)
        {
            if (!domain.Outside(c)) return false;
            byte tunnelRoof = TunnelRoof(c);
            if (tunnelRoof > 0) return tunnelRoof == 2;
            if (RoadCleared(c)) return false;
            if (Generator?.Snapshot?.OwnsRoof == true && Generator.Snapshot.Covers(c.x,c.z))
                return Generator.Snapshot.Roofs[Generator.Snapshot.Index(c.x,c.z)]?.isThickRoof == true;
            if (Generator == null)
            {
                int s = RawSampleAt(c);
                RoofDef roof = s >= 0 ? map.roofGrid.RoofAt(s) : null;
                return roof != null && roof.isNatural && roof.isThickRoof;
            }
            if (thickRoof == null) ComputeRoofGroups();
            return thickRoof[Index(c.x, c.z)];
        }

        internal byte OutsideRoofAt(IntVec3 c)
        {
            if (!domain.Outside(c)) return 0;
            byte tunnelRoof = TunnelRoof(c);
            if (tunnelRoof > 0) return tunnelRoof;
            if (RoadCleared(c)) return 0;
            if (Generator == null || Mountains?.IsRock(c.x, c.z) != true)
            {
                int s = RawSampleAt(c);
                RoofDef roof = s >= 0 ? map.roofGrid.RoofAt(s) : null;
                return roof?.isNatural != true ? (byte)0 : roof.isThickRoof ? (byte)2 : (byte)1;
            }
            if (OutsideThickRoof(c)) return 2;
            GenerationSnapshot snapshot = Generator.Snapshot;
            if (snapshot?.OwnsRoof == true && snapshot.Covers(c.x, c.z))
                return snapshot.Roofs[snapshot.Index(c.x, c.z)]?.isNatural == true ? (byte)1 : (byte)0;
            return anyRoof[Index(c.x, c.z)] ? (byte)1 : (byte)0;
        }

        private void ComputeRoofGroups()
        {
            int w = domain.W, h = domain.H, total = width * height;
            thickRoof = new bool[total];
            anyRoof = new bool[total];
            if (genRoofLevel == null) return;
            bool Inside(int x, int z) => domain.Inside(x, z);
            bool Natural(int x, int z)
            {
                if (x < -r || z < -r || x >= w + r || z >= h + r) return false;
                if (Inside(x, z))
                {
                    RoofDef roof = map.roofGrid.RoofAt(new IntVec3(x, 0, z));
                    return roof != null && roof.isNatural;
                }
                return genRoofLevel[Index(x, z)] > 0;
            }
            var visited = new bool[total];
            var group = new List<int>();
            var stack = new Stack<int>();
            for (int z0 = -r; z0 < h + r; z0++)
            {
                for (int x0 = -r; x0 < w + r; x0++)
                {
                    if (Inside(x0, z0)) continue;
                    int i0 = Index(x0, z0);
                    if (visited[i0] || genRoofLevel[i0] == 0) continue;
                    group.Clear();
                    int count = 0;
                    visited[i0] = true;
                    stack.Push(i0);
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop();
                        int x = i % width - r, z = i / width - r;
                        count++;
                        if (!Inside(x, z)) group.Add(i);
                        for (int d = 0; d < 4; d++)
                        {
                            IntVec3 o = GenAdj.CardinalDirections[d];
                            int xx = x + o.x, zz = z + o.z;
                            if (!Natural(xx, zz)) continue;
                            int j = Index(xx, zz);
                            if (visited[j]) continue;
                            visited[j] = true;
                            stack.Push(j);
                        }
                    }
                    if (count < MinRoofedCellsPerGroup) continue;
                    foreach (int i in group)
                    {
                        thickRoof[i] = genRoofLevel[i] == 2;
                        anyRoof[i] = genRoofLevel[i] > 0;
                    }
                }
            }
        }
    }
}
