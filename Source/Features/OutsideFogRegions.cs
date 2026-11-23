using System;
using System.Collections.Generic;

namespace EndlessHorizon
{
    internal sealed class OutsideFogRegions
    {
        private readonly int w, h, r, stride;
        private readonly HorizonDomain domain;
        private readonly int[] region;
        private readonly List<List<int>> members = new List<List<int>>();
        private readonly List<bool> exterior = new List<bool>();
        private readonly List<int> edge = new List<int>();
        private readonly List<int> edgeRocks = new List<int>();
        private readonly bool[] hidden;
        private byte[] previous;
        private bool[] result;

        private int Index(int x, int z) => (z + r) * stride + x + r;
        private bool Inside(int x, int z) => domain.Inside(x, z);
        private bool Covers(int x, int z) => domain.InGrid(x, z);

        public OutsideFogRegions(int width, int height, int ring, Func<int, int, bool> rock)
            : this(new HorizonDomain(width, height, ring), rock) { }

        public OutsideFogRegions(HorizonDomain domain, Func<int, int, bool> rock)
        {
            this.domain = domain;
            w = domain.W; h = domain.H; r = domain.R; stride = w + 2 * r;
            region = new int[stride * (h + 2 * r)];
            Array.Fill(region, -1);
            hidden = new bool[region.Length];
            for (int z = -r; z < h + r; z++)
                for (int x = -r; x < w + r; x++)
                {
                    if (Inside(x, z))
                    {
                        if (domain.OnEdge(x, z)) edge.Add(Index(x, z));
                        continue;
                    }
                    hidden[Index(x, z)] = true;
                    if (!rock(x, z)) region[Index(x, z)] = -2;
                    else if (domain.Steps(x, z) <= 2)
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
                domain.Nearest(i % stride - r, i / stride - r, out int x, out int z);
                if (edgeState.TryGetValue(Index(x, z), out byte at) && at == 2) fog[i] = false;
            }
            previous = current;
            return result = fog;
        }
    }

    internal sealed class OutsideEnclosure
    {
        private const byte OpenVisible = 0, OpenFogged = 1;
        private const int PocketMargin = 6;
        private readonly int w, h, r, stride;
        private readonly HorizonDomain domain;
        private readonly List<int> edge = new List<int>();
        private bool[] open;

        private int Index(int x, int z) => (z + r) * stride + x + r;
        private bool Inside(int x, int z) => domain.Inside(x, z);
        private bool Covers(int x, int z) => domain.InGrid(x, z);

        private OutsideEnclosure(HorizonDomain domain)
        {
            this.domain = domain;
            w = domain.W; h = domain.H; r = domain.R; stride = w + 2 * r;
            foreach (int i in domain.Edge) edge.Add(Index(i % w, i / w));
        }

        public static List<int> Walls(int width, int height, int ring, Func<int, int, bool> rock, Func<int, int, byte> state)
        {
            var walls = new List<int>();
            foreach (bool step in WallSteps(width, height, ring, rock, state, walls)) { }
            return walls;
        }

        public static IEnumerable<bool> WallSteps(int width, int height, int ring, Func<int, int, bool> rock, Func<int, int, byte> state, List<int> walls) =>
            WallSteps(new HorizonDomain(width, height, ring), rock, state, walls);

        public static IEnumerable<bool> WallSteps(HorizonDomain domain, Func<int, int, bool> rock, Func<int, int, byte> state, List<int> walls) =>
            new OutsideEnclosure(domain).Find(rock, state, walls);

        private IEnumerable<bool> Find(Func<int, int, bool> rock, Func<int, int, byte> state, List<int> walls)
        {
            var edgeState = new Dictionary<int, byte>(edge.Count);
            bool fogged = false;
            foreach (int i in edge)
            {
                byte s = state(i % stride - r, i / stride - r);
                edgeState.Add(i, s);
                fogged |= (s & 1) != 0;
            }
            if (!fogged) yield break;
            yield return false;
            var regions = new OutsideFogRegions(domain, rock);
            yield return false;
            bool[] hidden = regions.Compute((x, z) => edgeState[Index(x, z)]);
            yield return false;
            open = new bool[stride * (h + 2 * r)];
            for (int z = -r; z < h + r; z++)
                for (int x = -r; x < w + r; x++)
                    if (!Inside(x, z) && !rock(x, z)) open[Index(x, z)] = true;

            var wall = new bool[open.Length];
            void Close(int j)
            {
                if (wall[j] || !open[j] || hidden[j]) return;
                wall[j] = true;
                walls.Add(j);
            }
            yield return false;
            List<int> valleys = Pockets(edgeState);
            yield return false;
            foreach (int j in valleys) Close(j);
            foreach (int i in edge)
            {
                if ((edgeState[i] & 1) == 0) continue;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = i % stride - r + dx, zz = i / stride - r + dz;
                        if (Covers(xx, zz) && !Inside(xx, zz)) Close(Index(xx, zz));
                    }
            }
        }

        public static bool Broken(int w, int h, int r, Func<int, int, bool> rock, Func<int, int, byte> state, Func<int, int, bool> hidden) =>
            Broken(new HorizonDomain(w, h, r), rock, state, hidden);

        public static bool Broken(HorizonDomain domain, Func<int, int, bool> rock, Func<int, int, byte> state, Func<int, int, bool> hidden)
        {
            bool At(int x, int z)
            {
                if ((state(x, z) & 1) == 0) return false;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, zz = z + dz;
                        if (!domain.Outside(xx, zz)) continue;
                        if (!rock(xx, zz) && !hidden(xx, zz)) return true;
                    }
                return false;
            }
            foreach (int i in domain.Edge)
                if (At(i % domain.W, i / domain.W)) return true;
            return false;
        }

        internal static int Wobble(int x, int z) => (int)Math.Round(5 * LatticeNoise(x, z, 11) + 3 * LatticeNoise(x, z, 4));

        private static double LatticeNoise(int x, int z, int cell)
        {
            int gx = (int)Math.Floor(x / (double)cell), gz = (int)Math.Floor(z / (double)cell);
            double fx = x / (double)cell - gx, fz = z / (double)cell - gz;
            fx = fx * fx * (3 - 2 * fx);
            fz = fz * fz * (3 - 2 * fz);
            double At(int px, int pz)
            {
                unchecked
                {
                    uint n = (uint)px * 73856093u ^ (uint)pz * 19349663u ^ (uint)cell * 83492791u;
                    n ^= n >> 16; n *= 0x85ebca6b;
                    n ^= n >> 13; n *= 0xc2b2ae35;
                    n ^= n >> 16;
                    return n / (double)uint.MaxValue * 2 - 1;
                }
            }
            double low = At(gx, gz) + (At(gx + 1, gz) - At(gx, gz)) * fx;
            double high = At(gx, gz + 1) + (At(gx + 1, gz + 1) - At(gx, gz + 1)) * fx;
            return low + (high - low) * fz;
        }

        private List<int> Pockets(Dictionary<int, byte> edgeState)
        {
            var cells = new List<int>();
            if (!edgeState.ContainsValue(OpenFogged)) return cells;
            var reach = new int[open.Length];
            var front = new List<int>();
            int furthest = 0;
            foreach (int start in edge)
            {
                if (edgeState[start] != OpenFogged || reach[start] != 0) continue;
                var run = new List<int> { start };
                reach[start] = 1;
                for (int k = 0; k < run.Count; k++)
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = run[k] % stride - r + dx, zz = run[k] / stride - r + dz;
                            if (!Inside(xx, zz)) continue;
                            int j = Index(xx, zz);
                            if (reach[j] != 0 || !edgeState.TryGetValue(j, out byte s) || s != OpenFogged) continue;
                            reach[j] = 1; run.Add(j);
                        }
                int length = Math.Min(r, run.Count + PocketMargin);
                foreach (int j in run) { reach[j] = length; front.Add(j); }
                furthest = Math.Max(furthest, length + 8);
            }

            var explored = new int[open.Length];
            var walk = new List<int>();
            foreach (int i in edge) if (edgeState[i] == OpenVisible) walk.Add(i);
            for (int step = 1; step <= furthest + 8 && walk.Count > 0; step++)
            {
                var next = new List<int>();
                foreach (int i in walk)
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = i % stride - r + dx, zz = i / stride - r + dz;
                            if (!Covers(xx, zz) || Inside(xx, zz)) continue;
                            int j = Index(xx, zz);
                            if (!open[j] || explored[j] != 0) continue;
                            explored[j] = step; next.Add(j);
                        }
                walk = next;
            }

            for (int step = 1; step <= furthest && front.Count > 0; step++)
            {
                var next = new List<int>();
                foreach (int i in front)
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = i % stride - r + dx, zz = i / stride - r + dz;
                            if (!Covers(xx, zz) || Inside(xx, zz)) continue;
                            int j = Index(xx, zz);
                            if (!open[j]) continue;
                            if (reach[j] == 0) { reach[j] = -reach[i]; next.Add(j); }
                            else if (reach[j] < 0) reach[j] = Math.Min(reach[j], -reach[i]);
                        }
                front = new List<int>();
                foreach (int j in next)
                {
                    int limit = reach[j] = -reach[j];
                    int wobble = Wobble(j % stride - r, j / stride - r);
                    double grown = wobble * Math.Min(1.0, (step - 1) / 6.0);
                    if (step > limit + grown) continue;
                    if (explored[j] != 0 && step + grown >= explored[j]) continue;
                    cells.Add(j);
                    front.Add(j);
                }
            }
            return cells;
        }
    }
}
