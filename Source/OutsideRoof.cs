using System.Collections.Generic;
using Verse;

namespace EndlessHorizon
{
    public sealed partial class HorizonField
    {
        private const int MinRoofedCellsPerGroup = 20;
        private byte[] genRoofLevel;
        private bool[] thickRoof;

        private void SetGenRoof(int index, float elev)
        {
            genRoofLevel ??= new byte[width * height];
            genRoofLevel[index] = elev > 0.7f * 1.14f ? (byte)2 : elev > 0.7f * 1.04f ? (byte)1 : (byte)0;
        }

        public bool OutsideThickRoof(IntVec3 c)
        {
            if (!Covers(c) || c.InBounds(map)) return false;
            if (Generator == null)
            {
                int s = RawSampleAt(c);
                RoofDef roof = s >= 0 ? map.roofGrid.RoofAt(s) : null;
                return roof != null && roof.isNatural && roof.isThickRoof;
            }
            if (thickRoof == null) ComputeRoofGroups();
            return thickRoof[Index(c.x, c.z)];
        }

        private void ComputeRoofGroups()
        {
            int w = map.Size.x, h = map.Size.z, total = width * height;
            thickRoof = new bool[total];
            if (genRoofLevel == null) return;
            bool Inside(int x, int z) => x >= 0 && z >= 0 && x < w && z < h;
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
                    }
                }
            }
        }
    }
}
