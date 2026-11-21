using System;
using System.Collections.Generic;

namespace EndlessHorizon
{
    internal sealed class OutsideFogRegions
    {
        private readonly int w, h, r, stride;
        private readonly int[] region;
        private readonly List<List<int>> members = new List<List<int>>();
        private readonly List<bool> exterior = new List<bool>();
        private readonly List<int> edge = new List<int>();
        private readonly List<int> edgeRocks = new List<int>();
        private readonly bool[] hidden;
        private byte[] previous;
        private bool[] result;

        private int Index(int x, int z) => (z + r) * stride + x + r;
        private bool Inside(int x, int z) => x >= 0 && z >= 0 && x < w && z < h;
        private bool Covers(int x, int z) => x >= -r && z >= -r && x < w + r && z < h + r;

        public OutsideFogRegions(int width, int height, int ring, Func<int, int, bool> rock)
        {
            w = width; h = height; r = ring; stride = w + 2 * r;
            region = new int[stride * (h + 2 * r)];
            Array.Fill(region, -1);
            hidden = new bool[region.Length];
            for (int z = -r; z < h + r; z++)
                for (int x = -r; x < w + r; x++)
                {
                    if (Inside(x, z))
                    {
                        if (x == 0 || z == 0 || x == w - 1 || z == h - 1) edge.Add(Index(x, z));
                        continue;
                    }
                    hidden[Index(x, z)] = true;
                    if (!rock(x, z)) region[Index(x, z)] = -2;
                    else if (Math.Max(Math.Max(-x, x - w + 1), Math.Max(-z, z - h + 1)) <= 2)
                        edgeRocks.Add(Index(x, z));
                }
            var queue = new Queue<int>();
            for (int i = 0; i < region.Length; i++)
            {
                if (region[i] != -2) continue;
                int id = members.Count;
                var cells = new List<int>();
                bool outer = false;
                region[i] = id; queue.Enqueue(i);
                while (queue.Count > 0)
                {
                    int c = queue.Dequeue(), x = c % stride - r, z = c / stride - r;
                    cells.Add(c);
                    outer |= x == -r || z == -r || x == w + r - 1 || z == h + r - 1;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (!Covers(x + dx, z + dz)) continue;
                            int n = Index(x + dx, z + dz);
                            if (region[n] != -2) continue;
                            region[n] = id; queue.Enqueue(n);
                        }
                }
                members.Add(cells); exterior.Add(outer);
            }
            for (int z = -r; z < h + r; z++)
                for (int x = -r; x < w + r; x++)
                {
                    int i = Index(x, z);
                    if (!hidden[i] || region[i] >= 0) continue;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                            if (Covers(x + dx, z + dz))
                            {
                                int id = region[Index(x + dx, z + dz)];
                                if (id >= 0) members[id].Add(i);
                            }
                }
        }

        public bool[] Compute(Func<int, int, byte> state)
        {
            var current = new byte[edge.Count];
            bool changed = previous == null;
            for (int n = 0; n < edge.Count; n++)
            {
                int i = edge[n];
                current[n] = state(i % stride - r, i / stride - r);
                changed |= previous == null || current[n] != previous[n];
            }
            if (!changed) return result;
            var fog = (bool[])hidden.Clone();
            bool[] visible = exterior.ToArray();
            var edgeState = new Dictionary<int, byte>(edge.Count);
            for (int n = 0; n < edge.Count; n++)
            {
                int i = edge[n], x = i % stride - r, z = i / stride - r;
                edgeState.Add(i, current[n]);
                if (current[n] != 0) continue;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, zz = z + dz;
                        if (!Covers(xx, zz) || Inside(xx, zz)) continue;
                        int j = Index(xx, zz), id = region[j];
                        if (id >= 0) visible[id] = true;
                        else fog[j] = false;
                    }
            }
            for (int id = 0; id < members.Count; id++)
                if (visible[id]) foreach (int i in members[id]) fog[i] = false;
            foreach (int i in edgeRocks)
            {
                int x = Math.Clamp(i % stride - r, 0, w - 1), z = Math.Clamp(i / stride - r, 0, h - 1);
                byte s = edgeState[Index(x, z)];
                if ((s & 2) != 0) fog[i] = (s & 1) != 0;
            }
            previous = current;
            return result = fog;
        }
    }
}
