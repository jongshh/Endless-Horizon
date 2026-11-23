using System;
using System.Collections.Generic;
using Verse;

namespace EndlessHorizon
{
    internal static class FloodRules
    {
        internal const int Reach = 12;

        internal const byte Unreached = byte.MaxValue;

        internal static byte[] Steps(int width, int height, bool[] seed, bool[] open, out int[] parentOut, out List<int> orderOut)
        {
            int n = width * height;
            var steps = new byte[n];
            var parent = parentOut = new int[n];
            var order = orderOut = new List<int>();
            Array.Fill(steps, Unreached);
            Array.Fill(parent, -1);
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
                if (seed[i])
                {
                    steps[i] = 0;
                    queue.Enqueue(i);
                }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int next = steps[i] + 1;
                if (next > Reach) continue;
                int x = i % width, z = i / width;
                if (x > 0) Visit(i - 1);
                if (x < width - 1) Visit(i + 1);
                if (z > 0) Visit(i - width);
                if (z < height - 1) Visit(i + width);

                void Visit(int j)
                {
                    if (!open[j] || steps[j] != Unreached) return;
                    steps[j] = (byte)next;
                    parent[j] = i;
                    order.Add(j);
                    queue.Enqueue(j);
                }
            }
            return steps;
        }

        internal static TerrainDef FloodFrom(TerrainDef sourceTemp, TerrainDef sourceGround, TerrainDef fallback)
        {
            TerrainDef from = sourceTemp?.floodTerrain != null ? sourceTemp : sourceGround;
            return from?.floodTerrain ?? fallback;
        }

        internal static TerrainDef[] Carry(int n, int[] parent, List<int> order, Func<int, TerrainDef> ground, TerrainDef fallback)
        {
            var result = new TerrainDef[n];
            foreach (int i in order)
            {
                int p = parent[i];
                result[i] = FloodFrom(result[p], ground(p), fallback);
            }
            return result;
        }

        internal static void EdgeCopies(HorizonDomain domain, bool[] open, int rows, out int[] cells, out int[] sources)
        {
            var edge = new List<int>();
            var from = new List<int>();
            for (int z = -domain.R; z < domain.H + domain.R; z++)
                for (int x = -domain.R; x < domain.W + domain.R; x++)
                {
                    if (!domain.Outside(x, z) || domain.Steps(x, z) > rows) continue;
                    int i = domain.Index(x, z);
                    if (!open[i]) continue;
                    IntVec3 near = domain.Nearest(new IntVec3(x, 0, z));
                    if (!open[domain.Index(near.x, near.z)]) continue;
                    edge.Add(i);
                    from.Add(domain.MapIndex(near.x, near.z));
                }
            cells = edge.ToArray();
            sources = from.ToArray();
        }

        internal static int Share(float ratio, int count) =>
            count <= 0 ? 0 : (int)Math.Round(Math.Clamp(ratio, 0f, 1f) * count, MidpointRounding.ToEven);

        internal static uint StableOrder(int i, int salt)
        {
            unchecked
            {
                uint value = (uint)(i ^ salt) * 0x9E3779B1U;
                value = (value ^ (value >> 16)) * 0x7feb352dU;
                value = (value ^ (value >> 15)) * 0x846ca68bU;
                return value ^ (value >> 16);
            }
        }

        internal static void Allocate(List<int>[] byStep, float[] ratio, float overall, Action<int> flood)
        {
            for (int d = 1; d <= Reach; d++)
            {
                List<int> cells = byStep[d];
                if (cells == null || cells.Count == 0) continue;
                int take = Share(ratio[d] < 0f ? overall : ratio[d], cells.Count);
                for (int k = 0; k < take; k++) flood(cells[k]);
            }
        }
    }
}
