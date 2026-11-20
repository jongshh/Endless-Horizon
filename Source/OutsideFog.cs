using System.Collections.Generic;
using Verse;

namespace EndlessHorizon
{
    public sealed partial class HorizonField
    {
        private bool[] outsideFog;

        public void InvalidateOutsideFog() => outsideFog = null;

        public bool OutsideFogAt(IntVec3 c)
        {
            if (!Covers(c) || c.InBounds(map)) return false;
            if (outsideFog == null) ComputeOutsideFog();
            return outsideFog[Index(c.x, c.z)];
        }

        private void ComputeOutsideFog()
        {
            int w = map.Size.x, h = map.Size.z;
            int total = width * height;
            var fog = new bool[total];
            var visible = new bool[total];
            MountainMask m = Mountains;
            bool Inside(int x, int z) => x >= 0 && z >= 0 && x < w && z < h;
            bool Rock(int x, int z) => m != null && m.IsRock(x, z);
            bool OpenMapCell(int x, int z)
            {
                var c = new IntVec3(x, 0, z);
                if (map.fogGrid.IsFogged(c)) return false;
                Building b = c.GetEdifice(map);
                return b == null || !b.def.mineable;
            }
            var queue = new Queue<int>();
            void Seed(int x, int z)
            {
                int i = Index(x, z);
                if (visible[i]) return;
                visible[i] = true;
                queue.Enqueue(i);
            }
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    if (Inside(x, z) || Rock(x, z)) continue;
                    bool border = x == -r || z == -r || x == w + r - 1 || z == h + r - 1;
                    bool nextToMap = false;
                    if (!border)
                    {
                        for (int d = 0; d < 8 && !nextToMap; d++)
                        {
                            IntVec3 n = new IntVec3(x, 0, z) + GenAdj.AdjacentCells[d];
                            nextToMap = Inside(n.x, n.z) && OpenMapCell(n.x, n.z);
                        }
                    }
                    if (border || nextToMap) Seed(x, z);
                }
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % width - r, z = i / width - r;
                for (int d = 0; d < 8; d++)
                {
                    int xx = x + GenAdj.AdjacentCells[d].x, zz = z + GenAdj.AdjacentCells[d].z;
                    if (xx < -r || zz < -r || xx >= w + r || zz >= h + r || Inside(xx, zz)) continue;
                    if (Rock(xx, zz)) continue;
                    Seed(xx, zz);
                }
            }
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    if (Inside(x, z)) continue;
                    int i = Index(x, z);
                    if (visible[i]) continue;
                    bool seen = false;
                    if (Rock(x, z))
                    {
                        for (int d = 0; d < 8 && !seen; d++)
                        {
                            int xx = x + GenAdj.AdjacentCells[d].x, zz = z + GenAdj.AdjacentCells[d].z;
                            if (Inside(xx, zz)) seen = OpenMapCell(xx, zz);
                            else if (xx >= -r && zz >= -r && xx < w + r && zz < h + r) seen = !Rock(xx, zz) && visible[Index(xx, zz)];
                        }
                    }
                    fog[i] = !seen;
                    int dist = System.Math.Max(System.Math.Max(-x, x - w + 1), System.Math.Max(-z, z - h + 1));
                    if (dist <= 2 && Rock(x, z))
                    {
                        var edge = new IntVec3(UnityEngine.Mathf.Clamp(x, 0, w - 1), 0, UnityEngine.Mathf.Clamp(z, 0, h - 1));
                        Building eb = edge.GetEdifice(map);
                        if (eb != null && eb.def.mineable)
                        {
                            fog[i] = map.fogGrid.IsFogged(edge);
                        }
                    }
                }
            }
            outsideFog = fog;
        }
    }
}
