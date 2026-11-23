using System;
using System.Collections.Generic;

namespace EndlessHorizon
{
    internal sealed partial class HorizonDomain
    {
        internal readonly int W, H, R, Stride, Rows, Cells;
        private readonly bool[] playable;
        private readonly (float x, float z)[] area;
        private readonly int[] nearest;
        private readonly int[] depth;
        private int[] edge, boundary;

        internal HorizonDomain(int width, int height, int ring, bool[] playable = null, (float x, float z)[] area = null)
        {
            W = width;
            H = height;
            R = ring;
            Stride = width + 2 * ring;
            Rows = height + 2 * ring;
            Cells = Stride * Rows;
            this.area = CounterClockwise(area);
            if (playable == null || Array.IndexOf(playable, false) < 0) return;
            this.playable = playable;
            nearest = NearestPlayableCells();
            depth = CellsFromEdge();
        }

        internal bool Rectangle => playable == null;
        internal bool Limited => area != null;

        internal bool InMap(int x, int z) => x >= 0 && z >= 0 && x < W && z < H;

        internal bool InGrid(int x, int z) => x >= -R && z >= -R && x < W + R && z < H + R;

        internal int Index(int x, int z) => (z + R) * Stride + x + R;

        internal int MapIndex(int x, int z) => z * W + x;

        internal bool Inside(int x, int z) => x >= 0 && z >= 0 && x < W && z < H && (playable == null || playable[z * W + x]);

        internal bool Outside(int x, int z) => InGrid(x, z) && !Inside(x, z);

        internal bool Drawn(int x, int z) => Outside(x, z) && (area == null || InArea(x + 0.5f, z + 0.5f));

        internal bool InArea(float x, float z)
        {
            if (area == null) return true;
            if (area.Length < 3) return false;
            for (int i = 0; i < area.Length; i++)
            {
                (float ax, float az) = area[i];
                (float bx, float bz) = area[(i + 1) % area.Length];
                float ex = bx - ax, ez = bz - az;
                float cross = ex * (z - az) - ez * (x - ax);
                if (cross < 0f) return false;
                if (cross == 0f && !(ez < 0f || ez == 0f && ex > 0f)) return false;
            }
            return true;
        }

        internal void Nearest(int x, int z, out int nx, out int nz)
        {
            if (playable == null || Inside(x, z))
            {
                nx = x < 0 ? 0 : x >= W ? W - 1 : x;
                nz = z < 0 ? 0 : z >= H ? H - 1 : z;
                return;
            }
            int gx = Math.Clamp(x, -R, W + R - 1), gz = Math.Clamp(z, -R, H + R - 1);
            int at = nearest[Index(gx, gz)];
            nx = at % W;
            nz = at / W;
        }

        internal int Steps(int x, int z)
        {
            if (playable == null) return Math.Max(Math.Max(-x, x - W + 1), Math.Max(-z, z - H + 1));
            Nearest(x, z, out int nx, out int nz);
            return Math.Max(Math.Abs(x - nx), Math.Abs(z - nz));
        }

        internal float Distance(int x, int z)
        {
            Nearest(x, z, out int nx, out int nz);
            float dx = x - nx, dz = z - nz;
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        internal float DistanceFromPoint(float x, float z)
        {
            float dx, dz;
            if (playable == null)
            {
                dx = Math.Max(0f, Math.Max(-x, x - W));
                dz = Math.Max(0f, Math.Max(-z, z - H));
            }
            else
            {
                int cx = (int)MathF.Floor(x), cz = (int)MathF.Floor(z);
                if (cx == x || cz == z)
                {
                    for (int oz = cz == z ? -1 : 0; oz <= 0; oz++)
                        for (int ox = cx == x ? -1 : 0; ox <= 0; ox++)
                            if (Inside(cx + ox, cz + oz)) return 0f;
                }
                Nearest(cx, cz, out int nx, out int nz);
                dx = Math.Max(0f, Math.Max(nx - x, x - (nx + 1)));
                dz = Math.Max(0f, Math.Max(nz - z, z - (nz + 1)));
            }
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        internal int FurthestInMap()
        {
            if (playable == null) return 0;
            float far = Math.Max(Math.Max(Distance(0, 0), Distance(W - 1, 0)), Math.Max(Distance(0, H - 1), Distance(W - 1, H - 1)));
            return (int)MathF.Ceiling(far);
        }

        internal int Depth(int x, int z)
        {
            if (!InMap(x, z)) return -1;
            return playable == null ? Math.Min(Math.Min(x, W - 1 - x), Math.Min(z, H - 1 - z)) : depth[z * W + x];
        }

        internal bool InBand(int x, int z, int cells)
        {
            int d = Depth(x, z);
            return d >= 0 && d < cells;
        }

        internal bool OnEdge(int x, int z) => playable == null
            ? x >= 0 && z >= 0 && x < W && z < H && (x == 0 || z == 0 || x == W - 1 || z == H - 1)
            : InMap(x, z) && depth[z * W + x] == 0;

        internal bool NearEdge(int x, int z, int cells)
        {
            if (playable == null) return !(x >= cells && z >= cells && x < W - cells && z < H - cells);
            return !InMap(x, z) || depth[z * W + x] < cells;
        }

        internal int[] Edge => edge ??= EdgeCells(false);

        internal int[] Boundary => boundary ??= EdgeCells(true);

        private int[] EdgeCells(bool cardinal)
        {
            var found = new List<int>();
            for (int z = 0; z < H; z++)
            {
                for (int x = 0; x < W; x++)
                {
                    if (!OnEdge(x, z)) continue;
                    if (cardinal && Inside(x - 1, z) && Inside(x + 1, z) && Inside(x, z - 1) && Inside(x, z + 1)) continue;
                    found.Add(z * W + x);
                }
            }
            return found.ToArray();
        }

        internal bool Interior(int x0, int z0, int x1, int z1)
        {
            if (!(x0 > 0 && z0 > 0 && x1 < W - 1 && z1 < H - 1)) return false;
            if (playable == null) return true;
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    if (depth[z * W + x] <= 0) return false;
            return true;
        }

        internal bool AnyInBand(int x0, int z0, int x1, int z1, int cells)
        {
            if (playable == null) return x0 < cells || z0 < cells || x1 + 1 > W - cells || z1 + 1 > H - cells;
            for (int z = Math.Max(0, z0); z <= Math.Min(H - 1, z1); z++)
                for (int x = Math.Max(0, x0); x <= Math.Min(W - 1, x1); x++)
                    if (InBand(x, z, cells)) return true;
            return false;
        }

        internal static int Mirror(int v, int size)
        {
            if (v < 0) v = -v - 1;
            else if (v >= size) v = 2 * size - v - 1;
            return Math.Clamp(v, 0, size - 1);
        }

        internal void Reflect(int x, int z, out int sx, out int sz)
        {
            if (playable == null)
            {
                sx = Mirror(x, W);
                sz = Mirror(z, H);
                return;
            }
            Nearest(x, z, out int nx, out int nz);
            int dx = nx - x, dz = nz - z;
            dx -= Math.Sign(dx);
            dz -= Math.Sign(dz);
            while (true)
            {
                sx = nx + dx;
                sz = nz + dz;
                if (Inside(sx, sz)) return;
                dx /= 2;
                dz /= 2;
            }
        }

        internal List<(int x0, int z0, int x1, int z1)> Strips(int size)
        {
            var strips = new List<(int x0, int z0, int x1, int z1)>();
            int min = FloorTo(-R, size);
            for (int x = min; x < W + R; x += size)
            {
                for (int z = min; z < H + R; z += size)
                {
                    int x0 = x, z0 = z, x1 = x + size - 1, z1 = z + size - 1;
                    if (playable != null || area != null)
                    {
                        Split(x0, z0, x1, z1, strips);
                        continue;
                    }
                    if (x0 >= 0 && z0 >= 0 && x1 < W && z1 < H) continue;
                    void Add(int a0, int b0, int a1, int b1)
                    {
                        if (a1 >= a0 && b1 >= b0) strips.Add((a0, b0, a1, b1));
                    }
                    Add(x0, z0, Math.Min(x1, -1), z1);
                    Add(Math.Max(x0, W), z0, x1, z1);
                    Add(Math.Max(x0, 0), z0, Math.Min(x1, W - 1), Math.Min(z1, -1));
                    Add(Math.Max(x0, 0), Math.Max(z0, H), Math.Min(x1, W - 1), z1);
                }
            }
            return strips;
        }

        private void Split(int x0, int z0, int x1, int z1, List<(int x0, int z0, int x1, int z1)> strips)
        {
            var open = new List<(int x0, int z0, int x1, int z1)>();
            for (int z = z0; z <= z1; z++)
            {
                var next = new List<(int x0, int z0, int x1, int z1)>();
                int from = x0;
                while (from <= x1)
                {
                    if (!Takes(from, z)) { from++; continue; }
                    int to = from;
                    while (to < x1 && Takes(to + 1, z)) to++;
                    int same = open.FindIndex(o => o.x0 == from && o.x1 == to);
                    if (same >= 0)
                    {
                        next.Add((from, open[same].z0, to, z));
                        open.RemoveAt(same);
                    }
                    else next.Add((from, z, to, z));
                    from = to + 1;
                }
                strips.AddRange(open);
                open = next;
            }
            strips.AddRange(open);
            bool Takes(int x, int z) => !Inside(x, z) && InArea(x + 0.5f, z + 0.5f);
        }

        internal static int FloorTo(int v, int step) => (int)Math.Floor(v / (double)step) * step;

        internal List<(float x, float z)> Clip(List<(float x, float z)> shape)
        {
            if (area == null) return shape;
            if (area.Length < 3) return new List<(float x, float z)>();
            List<(float x, float z)> kept = shape;
            for (int i = 0; i < area.Length && kept.Count > 0; i++)
            {
                (float ax, float az) = area[i];
                (float bx, float bz) = area[(i + 1) % area.Length];
                float Side((float x, float z) p) => (bx - ax) * (p.z - az) - (bz - az) * (p.x - ax);
                var next = new List<(float x, float z)>();
                for (int k = 0; k < kept.Count; k++)
                {
                    (float x, float z) p = kept[k], q = kept[(k + 1) % kept.Count];
                    float sp = Side(p), sq = Side(q);
                    if (sp >= 0f) next.Add(p);
                    if (sp > 0f && sq < 0f || sp < 0f && sq > 0f)
                    {
                        float t = sp / (sp - sq);
                        next.Add((p.x + (q.x - p.x) * t, p.z + (q.z - p.z) * t));
                    }
                }
                kept = next.Count >= 3 ? next : new List<(float x, float z)>();
            }
            return kept;
        }

        private static (float x, float z)[] CounterClockwise((float x, float z)[] polygon)
        {
            if (polygon == null || polygon.Length < 3) return polygon;
            double twice = 0;
            for (int i = 0; i < polygon.Length; i++)
            {
                (float ax, float az) = polygon[i];
                (float bx, float bz) = polygon[(i + 1) % polygon.Length];
                twice += (double)ax * bz - (double)bx * az;
            }
            if (twice == 0) return Array.Empty<(float, float)>();
            var copy = ((float x, float z)[])polygon.Clone();
            if (twice < 0) Array.Reverse(copy);
            return copy;
        }

        internal static bool Convex((float x, float z)[] polygon)
        {
            if (polygon.Length < 3) return false;
            int sign = 0;
            for (int i = 0; i < polygon.Length; i++)
            {
                (float ax, float az) = polygon[i];
                (float bx, float bz) = polygon[(i + 1) % polygon.Length];
                for (int k = 0; k < polygon.Length; k++)
                {
                    double cross = ((double)bx - ax) * ((double)polygon[k].z - az) - ((double)bz - az) * ((double)polygon[k].x - ax);
                    if (Math.Abs(cross) < 1e-3) continue;
                    int at = cross > 0 ? 1 : -1;
                    if (sign == 0) sign = at;
                    else if (sign != at) return false;
                }
            }
            return sign != 0;
        }

        internal bool SameShape(bool[] otherPlayable, (float x, float z)[] otherArea)
        {
            if (otherPlayable != null && Array.IndexOf(otherPlayable, false) < 0) otherPlayable = null;
            otherArea = CounterClockwise(otherArea);
            return (playable == null ? otherPlayable == null : otherPlayable != null && playable.AsSpan().SequenceEqual(otherPlayable))
                && (area == null ? otherArea == null : otherArea != null && area.AsSpan().SequenceEqual(otherArea));
        }

        private int[] NearestPlayableCells()
        {
            var inColumn = new int[W * Rows];
            for (int x = 0; x < W; x++)
            {
                int last = -1;
                for (int z = -R; z < H + R; z++)
                {
                    if (z >= 0 && z < H && playable[z * W + x]) last = z;
                    inColumn[(z + R) * W + x] = last;
                }
                int next = -1;
                for (int z = H + R - 1; z >= -R; z--)
                {
                    if (z >= 0 && z < H && playable[z * W + x]) next = z;
                    int i = (z + R) * W + x, below = inColumn[i];
                    if (next >= 0 && (below < 0 || next - z < z - below)) inColumn[i] = next;
                }
            }
            var found = new int[Cells];
            var site = new int[W];
            var from = new double[W];
            for (int z = -R; z < H + R; z++)
            {
                int row = (z + R) * W, count = 0;
                long Height(int q)
                {
                    long dy = z - inColumn[row + q];
                    return dy * dy + (long)q * q;
                }
                for (int q = 0; q < W; q++)
                {
                    if (inColumn[row + q] < 0) continue;
                    long height = Height(q);
                    double start = double.NegativeInfinity;
                    while (count > 0)
                    {
                        int v = site[count - 1];
                        start = (height - Height(v)) / (2.0 * (q - v));
                        if (start > from[count - 1]) break;
                        count--;
                        start = double.NegativeInfinity;
                    }
                    site[count] = q;
                    from[count++] = start;
                }
                int k = 0;
                for (int x = -R; x < W + R; x++)
                {
                    if (count == 0) throw new ArgumentException("A region needs at least one playable cell.");
                    while (k + 1 < count && from[k + 1] < x) k++;
                    found[Index(x, z)] = inColumn[row + site[k]] * W + site[k];
                }
            }
            return found;
        }

        private int[] CellsFromEdge()
        {
            var found = new int[W * H];
            var queue = new int[W * H];
            int head = 0, tail = 0;
            Array.Fill(found, -1);
            for (int z = 0; z < H; z++)
            {
                for (int x = 0; x < W; x++)
                {
                    if (!playable[z * W + x]) continue;
                    bool open = false;
                    for (int dz = -1; dz <= 1 && !open; dz++)
                        for (int dx = -1; dx <= 1 && !open; dx++)
                            open = !Inside(x + dx, z + dz);
                    if (!open) continue;
                    found[z * W + x] = 0;
                    queue[tail++] = z * W + x;
                }
            }
            while (head < tail)
            {
                int i = queue[head++], x = i % W, z = i / W;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (!Inside(x + dx, z + dz)) continue;
                        int j = (z + dz) * W + x + dx;
                        if (found[j] >= 0) continue;
                        found[j] = found[i] + 1;
                        queue[tail++] = j;
                    }
                }
            }
            return found;
        }
    }
}
