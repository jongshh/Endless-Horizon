using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    public sealed partial class HorizonField
    {
        public GenReconstruction Generator;
        public string OutsideMode = "mirror";
        public string RiverPaintReport = "none";
        public MountainMask Mountains;
        public FogTint FogTint;

        private bool[] genRock;
        private ThingDef[] genRockDef;
        private TerrainDef[] pooled;
        private TerrainDef[] pooledCover;
        private Dictionary<TerrainDef, List<int>> pool;

        private void ChooseGenerator()
        {
            if (!EndlessHorizonMod.Settings.useGenerator || HorizonDebug.ForceMirror)
            {
                OutsideMode = "mirror (setting)";
                return;
            }
            GenReconstruction g = GenReconstruction.For(map);
            if (!g.Usable || g.Fertility == null || g.Predicted == null)
            {
                OutsideMode = "mirror (generator match too low: " + g.Report + ")";
                return;
            }
            Generator = g;
            genRock = new bool[width * height];
            genRockDef = new ThingDef[width * height];
            OutsideMode = "generator";
        }

        public bool GenRock(IntVec3 c) => Generator != null && Covers(c) && genRock[Index(c.x, c.z)];

        public ThingDef GenRockDef(IntVec3 c) => Generator != null && Covers(c) ? genRockDef[Index(c.x, c.z)] : null;

        private TerrainDef GeneratorTerrain(IntVec3 c, out int sample, out int rawSample)
        {
            sample = rawSample = -1;
            TerrainDef t = Generator.TerrainOutside(map, c, out float elev);
            if (t == null)
            {
                return null;
            }
            if (Generator.Elevation != null && elev > 0.7f && !t.IsWater)
            {
                int i = Index(c.x, c.z);
                genRock[i] = true;
                SetGenRoof(i, elev);
                ThingDef rd = Generator.RockAt(c.x, c.z);
                genRockDef[i] = rd?.graphic is Graphic_Linked ? rd : null;
                return t;
            }
            sample = rawSample = PickSource(c, t);
            return t;
        }

        private void EnsurePool()
        {
            if (pool != null)
            {
                return;
            }
            int n = map.cellIndices.NumGridCells;
            pooled = new TerrainDef[n];
            pooledCover = new TerrainDef[n];
            pool = new Dictionary<TerrainDef, List<int>>();
            void Add(TerrainDef key, int index)
            {
                if (!pool.TryGetValue(key, out List<int> l))
                {
                    pool[key] = l = new List<int>();
                }
                l.Add(index);
            }
            Building[] edifices = map.edificeGrid.InnerArray;
            for (int i = 0; i < n; i++)
            {
                if (Developed(i) || edifices[i] != null || map.roofGrid.RoofAt(i) != null)
                {
                    continue;
                }
                TerrainDef t = map.terrainGrid.BaseTerrainAt(map.cellIndices.IndexToCell(i));
                if (TerrainBlend.TryParts(t, out TerrainDef blendBase, out TerrainDef blendCover))
                {
                    if (blendBase != null && !blendBase.IsWater) { pooled[i] = blendBase; Add(blendBase, i); }
                    if (blendCover != null && !blendCover.IsWater && blendCover != blendBase) { pooledCover[i] = blendCover; Add(blendCover, i); }
                    continue;
                }
                if (!Usable(t))
                {
                    continue;
                }
                pooled[i] = t;
                Add(t, i);
            }
        }

        private const int PatchSize = 14;
        private const int MirrorSeamCells = 4;
        private const int PatchTries = 8;

        internal int WaterSource(IntVec3 c, TerrainDef t)
        {
            try
            {
                return PickSource(c, t);
            }
            catch (Exception ex)
            {
                Log.WarningOnce(EndlessHorizonMod.LogPrefix + "Picking a water source cell failed; that water cell is drawn without plants. " + ex, 0x45485753);
                return -1;
            }
        }

        private int PickSource(IntVec3 c, TerrainDef t)
        {
            EnsurePool();
            if (!pool.TryGetValue(t, out List<int> list) || list.Count == 0)
            {
                return -1;
            }
            int w = map.Size.x, h = map.Size.z;
            int edgeDist = Math.Max(Math.Max(-c.x, c.x - w + 1), Math.Max(-c.z, c.z - h + 1));
            if (edgeDist <= MirrorSeamCells)
            {
                int m = MirrorIndex(c);
                if (m >= 0 && Pooled(m, t))
                {
                    return m;
                }
            }
            int jx = Mathf.RoundToInt((float)warpX.GetValue(c.x, 0, c.z) * 5f);
            int jz = Mathf.RoundToInt((float)warpZ.GetValue(c.x, 0, c.z) * 5f);
            int px = FloorDiv(c.x + jx, PatchSize), pz = FloorDiv(c.z + jz, PatchSize);
            int ox = c.x - px * PatchSize - PatchSize / 2, oz = c.z - pz * PatchSize - PatchSize / 2;
            int hash = Gen.HashCombineInt(Gen.HashCombineInt(px, pz), t.shortHash);
            for (int a = 0; a < PatchTries; a++)
            {
                int anchor = list[(int)(Mix((uint)(hash + a * 7919)) % (uint)list.Count)];
                IntVec3 ac = map.cellIndices.IndexToCell(anchor);
                int x = ac.x + ox, z = ac.z + oz;
                if (x < 0 || z < 0 || x >= w || z >= h)
                {
                    continue;
                }
                int k = map.cellIndices.CellToIndex(x, z);
                if (Pooled(k, t))
                {
                    return k;
                }
            }
            return list[(int)(Mix((uint)Gen.HashCombineInt(Gen.HashCombineInt(c.x, c.z), t.shortHash)) % (uint)list.Count)];
        }

        private static uint Mix(uint h)
        {
            h ^= h >> 16;
            h *= 0x85ebca6b;
            h ^= h >> 13;
            h *= 0xc2b2ae35;
            h ^= h >> 16;
            return h;
        }

        private bool Pooled(int index, TerrainDef t) => pooled[index] == t || pooledCover[index] == t;

        private static int FloorDiv(int v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);

        private int MirrorIndex(IntVec3 c)
        {
            int w = map.Size.x, h = map.Size.z;
            int dx = c.x < 0 ? -c.x : (c.x >= w ? c.x - w + 1 : 0);
            int dz = c.z < 0 ? -c.z : (c.z >= h ? c.z - h + 1 : 0);
            float d = Math.Max(dx, dz);
            float amp = Mathf.Min(12f, d * 0.4f);
            float macro = Mathf.Min(30f, d * 0.75f);
            float fx = c.x + (float)warpX.GetValue(c.x, 0, c.z) * amp + (float)macroX.GetValue(c.x, 0, c.z) * macro;
            float fz = c.z + (float)warpZ.GetValue(c.x, 0, c.z) * amp + (float)macroZ.GetValue(c.x, 0, c.z) * macro;
            return map.cellIndices.CellToIndex(Mirror(Mathf.RoundToInt(fx), w), Mirror(Mathf.RoundToInt(fz), h));
        }

        private const int RiverReach = 64;
        private const int MaxBandWidth = 40;

        private int[] MapDistance(System.Func<IntVec3, bool> source)
        {
            int w = map.Size.x, h = map.Size.z, n = w * h;
            var d = new int[n];
            var q = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (source(map.cellIndices.IndexToCell(i)))
                {
                    d[i] = 0;
                    q.Enqueue(i);
                }
                else
                {
                    d[i] = RiverReach + 1;
                }
            }
            if (q.Count == 0) return null;
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                if (d[i] >= RiverReach) continue;
                int x = i % w, z = i / w;
                for (int dz = -1; dz <= 1; dz++)
                {
                    int zz = z + dz;
                    if (zz < 0 || zz >= h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= w) continue;
                        int j = zz * w + xx;
                        if (d[j] > d[i] + 1)
                        {
                            d[j] = d[i] + 1;
                            q.Enqueue(j);
                        }
                    }
                }
            }
            return d;
        }

        private void ApplyRiverPaint()
        {
            int w = map.Size.x, h = map.Size.z;
            int total = width * height;
            var dist = new int[total];
            for (int i = 0; i < total; i++) dist[i] = int.MaxValue;
            var queue = new Queue<int>();
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    bool inside = x >= 0 && z >= 0 && x < w && z < h;
                    TerrainDef t = inside ? map.terrainGrid.BaseTerrainAt(new IntVec3(x, 0, z)) : cells[Index(x, z)];
                    if (t != null && t.IsRiver)
                    {
                        int i = Index(x, z);
                        dist[i] = 0;
                        queue.Enqueue(i);
                    }
                }
            }
            if (queue.Count == 0)
            {
                RiverPaintReport = "no river";
                return;
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int d = dist[i];
                if (d >= RiverReach) continue;
                int x = i % width, z = i / width;
                for (int dz = -1; dz <= 1; dz++)
                {
                    int zz = z + dz;
                    if (zz < 0 || zz >= height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= width) continue;
                        int j = zz * width + xx;
                        if (dist[j] > d + 1)
                        {
                            dist[j] = d + 1;
                            queue.Enqueue(j);
                        }
                    }
                }
            }

            int[] seaDist = MapDistance(c2 => map.terrainGrid.BaseTerrainAt(c2)?.IsOcean ?? false);

            var near = new Dictionary<TerrainDef, List<int>>();
            var far = new Dictionary<TerrainDef, int>();
            TerrainDef[] predicted = Generator.Predicted;
            for (int i = 0; i < predicted.Length; i++)
            {
                if (Developed(i)) continue;
                IntVec3 c = map.cellIndices.IndexToCell(i);
                TerrainDef actual = map.terrainGrid.BaseTerrainAt(c);
                TerrainDef p = predicted[i];
                if (actual == null || actual.IsWater || p == null || TerrainBlend.Matches(actual, p) || !Usable(actual)) continue;
                if (TerrainBlend.TryParts(actual, out _, out _)) continue;
                int d = dist[Index(c.x, c.z)];
                if (seaDist != null && seaDist[i] <= d) continue;
                if (d <= RiverReach)
                {
                    if (!near.TryGetValue(actual, out List<int> l)) near[actual] = l = new List<int>();
                    l.Add(d);
                }
                else
                {
                    far.TryGetValue(actual, out int f);
                    far[actual] = f + 1;
                }
            }
            var bands = new List<(TerrainDef terrain, float width)>();
            foreach (KeyValuePair<TerrainDef, List<int>> kv in near)
            {
                far.TryGetValue(kv.Key, out int f);
                if (kv.Value.Count < 40 || kv.Value.Count < 0.8f * (kv.Value.Count + f)) continue;
                kv.Value.Sort();
                int bandWidth = kv.Value[(int)(kv.Value.Count * 0.9f)];
                if (bandWidth > MaxBandWidth) continue;
                bands.Add((kv.Key, bandWidth));
            }
            bands = bands.OrderBy(b => b.width).Take(3).ToList();
            if (bands.Count == 0)
            {
                RiverPaintReport = "nothing to learn";
                return;
            }
            float maxWidth = bands.Max(b => b.width) * 1.3f;
            var noise = new Perlin(0.04, 2.0, 0.5, 2, Gen.HashCombineInt(map.Tile.GetHashCode(), 0x52505754), QualityMode.Medium);
            int painted = 0;
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    if (x >= 0 && z >= 0 && x < w && z < h) continue;
                    int i = Index(x, z);
                    TerrainDef t = cells[i];
                    int d = dist[i];
                    if (t == null || t.IsWater || genRock[i] || d > maxWidth || d == 0) continue;
                    float n01 = (float)noise.GetValue(x, 0, z) * 0.5f + 0.5f;
                    foreach ((TerrainDef terrain, float bw) in bands)
                    {
                        if (d <= bw * (0.75f + 0.5f * n01))
                        {
                            if (t != terrain || samples[i] < 0)
                            {
                                cells[i] = terrain;
                                samples[i] = rawSamples[i] = PickSource(new IntVec3(x, 0, z), terrain);
                                painted++;
                            }
                            break;
                        }
                    }
                }
            }
            RiverPaintReport = string.Join(", ", bands.Select(b => $"{b.terrain.defName} {b.width:0}")) + $" ({painted} cells)";
        }
    }
}
