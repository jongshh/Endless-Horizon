using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal sealed class GeneratorModule
    {
        private readonly Map map;
        private readonly HorizonDomain domain;
        private readonly LandGrid grid;
        private readonly PollutionModule pollution;
        private readonly Func<RoadExtension> roadsOf;
        private readonly int r, width, height;
        private Func<IntVec3, int> mirrorOf;

        internal GeneratorModule(Map map, HorizonDomain domain, LandGrid grid, PollutionModule pollution, Func<RoadExtension> roadsOf)
        {
            this.map = map;
            this.domain = domain;
            this.grid = grid;
            this.pollution = pollution;
            this.roadsOf = roadsOf;
            r = grid.R;
            width = grid.Width;
            height = grid.Height;
        }

        private TerrainDef[] cells => grid.Cells;
        private int[] samples => grid.Samples;
        private int[] rawSamples => grid.RawSamples;
        private bool[] genRock { get => grid.GenRock; set => grid.GenRock = value; }
        private ThingDef[] genRockDef { get => grid.GenRockDef; set => grid.GenRockDef = value; }
        private byte[] genRoofLevel { get => grid.GenRoofLevel; set => grid.GenRoofLevel = value; }
        private int Index(int x, int z) => grid.Index(x, z);
        private bool Covers(IntVec3 c) => domain.InGrid(c);
        private RoadExtension Roads => roadsOf();
        internal Func<IntVec3, int> MirrorOf => mirrorOf ??= MirrorIndex;
        private bool GroundPolluted(IntVec3 c, TerrainDef t) => pollution.GroundPolluted(c, t, MirrorOf);

        public string CoastMode = "none";
        public int CoastCells, RiverCells;

        internal void ForgetReflectedRoofs() => reflectedRoof = null;

        public GenReconstruction Generator;
        public string OutsideMode = "mirror";
        public string RiverPaintReport = "none";

        private TerrainDef[] pooled;
        private TerrainDef[] pooledCover;
        private Dictionary<(TerrainDef terrain, BiomeDef biome, bool polluted), List<int>> pool;
        private bool[] pooledPolluted;
        private BiomeDef[] pooledBiome;

        internal void ChooseGenerator()
        {
            if (!HorizonSettings.Current.useGenerator || HorizonDebug.ForceMirror)
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
            if (Generator.IsRock(c, t))
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
            pooledBiome = new BiomeDef[n];
            pool = new Dictionary<(TerrainDef, BiomeDef, bool), List<int>>();
            pooledPolluted = new bool[n];
            void Add(TerrainDef key, int index)
            {
                var identity = (key, pooledBiome[index], pooledPolluted[index]);
                if (!pool.TryGetValue(identity, out List<int> l))
                {
                    pool[identity] = l = new List<int>();
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
                IntVec3 sourceCell = map.cellIndices.IndexToCell(i);
                pooledBiome[i] = map.BiomeAt(sourceCell);
                pooledPolluted[i] = map.pollutionGrid?.IsPolluted(sourceCell) == true;
                TerrainDef t = map.terrainGrid.BaseTerrainAt(sourceCell);
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

        internal int ReplacedSource(IntVec3 c, TerrainDef t)
        {
            try
            {
                return PickSource(c, t);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(HorizonLog.Prefix + "Picking a source for a replaced outside cell failed; that cell is drawn without plants. " + ex, 0x45485753);
                return -1;
            }
        }

        private int PickSource(IntVec3 c, TerrainDef t)
        {
            EnsurePool();
            BiomeDef biome = Generator?.BiomeAt(c.x,c.z) ?? map.BiomeAt(domain.Nearest(c));
            bool dirty = GroundPolluted(c, t);
            if (!pool.TryGetValue((t, biome, dirty), out List<int> list))
            {
                dirty = !dirty;
                if (!pool.TryGetValue((t, biome, dirty), out list)) return -1;
            }
            int edgeDist = domain.Steps(c);
            if (edgeDist <= MirrorSeamCells)
            {
                int m = MirrorIndex(c);
                if (m >= 0 && Pooled(m, t, biome, dirty))
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
                if (!domain.Inside(x, z))
                {
                    continue;
                }
                int k = map.cellIndices.CellToIndex(x, z);
                if (Pooled(k, t, biome, dirty))
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

        private bool Pooled(int index, TerrainDef t, BiomeDef biome, bool dirty) => pooledBiome[index] == biome && pooledPolluted[index] == dirty && (pooled[index] == t || pooledCover[index] == t);

        private static int FloorDiv(int v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);

        internal int MirrorIndex(IntVec3 c)
        {
            float d = Math.Max(0, domain.Steps(c));
            float amp = Mathf.Min(12f, d * 0.4f);
            float macro = Mathf.Min(30f, d * 0.75f);
            float fx = c.x + (float)warpX.GetValue(c.x, 0, c.z) * amp + (float)macroX.GetValue(c.x, 0, c.z) * macro;
            float fz = c.z + (float)warpZ.GetValue(c.x, 0, c.z) * amp + (float)macroZ.GetValue(c.x, 0, c.z) * macro;
            domain.Reflect(Mathf.RoundToInt(fx), Mathf.RoundToInt(fz), out int sx, out int sz);
            return map.cellIndices.CellToIndex(sx, sz);
        }

        private const int RiverReach = 64;
        private const int MaxBandWidth = 40;

        private int[] MapDistance(System.Func<IntVec3, bool> source)
        {
            int w = domain.W, h = domain.H, n = w * h;
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

        internal void ApplyRiverPaint()
        {
            int w = domain.W, h = domain.H;
            int total = width * height;
            var dist = new int[total];
            for (int i = 0; i < total; i++) dist[i] = int.MaxValue;
            var queue = new Queue<int>();
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    bool inside = domain.Inside(x, z);
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
                if (Developed(i) || Roads?.NearRoad(i) == true) continue;
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
                    if (domain.Inside(x, z)) continue;
                    if (Generator.Snapshot?.OwnsVanillaRiverAt(x, z) == true) continue;
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
        private static bool coastWorkerLogged;
        private bool[] nearRiver;
        internal bool MirroredSea(int x, int z, TerrainDef edge)
        {
            float dist = domain.Steps(x, z);
            if (dist >= 14f) return edge.IsOcean;
            domain.Reflect(x, z, out int sx, out int sz);
            return map.terrainGrid.BaseTerrainAt(new IntVec3(sx, 0, sz))?.IsOcean ?? false;
        }
        private IntVec3 Clamp(IntVec3 c) => domain.Nearest(c);
        private Verse.Noise.Perlin warpX, warpZ;
        private readonly Dictionary<int, TerrainDef> dominantCache = new Dictionary<int, TerrainDef>();
        internal static bool Usable(TerrainDef t) => t != null && t.natural && !t.bridge && !t.dontRender;
        private Verse.Noise.Perlin macroX, macroZ;
        private bool[] developed;
        public int WildSubstitutions;
        private bool Developed(int index)
        {
            if (developed == null)
            {
                int n = map.cellIndices.NumGridCells;
                developed = new bool[n];
                Area home = map.areaManager.Home;
                for (int i = 0; i < n; i++)
                {
                    IntVec3 c = map.cellIndices.IndexToCell(i);
                    if (!domain.Inside(c))
                    {
                        developed[i] = true;
                        continue;
                    }
                    bool dev = home != null && home[i];
                    if (!dev)
                    {
                        Building b = map.edificeGrid.InnerArray[i];
                        dev = b != null && !b.def.mineable;
                    }
                    if (!dev)
                    {
                        TerrainDef top = map.terrainGrid.TerrainAt(i);
                        dev = top != map.terrainGrid.BaseTerrainAt(c) || (top != null && !top.natural);
                    }
                    if (!dev)
                    {
                        RoofDef roof = map.roofGrid.RoofAt(i);
                        dev = roof != null && !roof.isNatural;
                    }
                    if (!dev)
                    {
                        dev = map.zoneManager.ZoneAt(c) != null;
                    }
                    if (!dev)
                    {
                        List<Thing> things = map.thingGrid.ThingsListAtFast(i);
                        for (int k = 0; k < things.Count && !dev; k++)
                        {
                            dev = things[k] is Plant p && p.sown;
                        }
                    }
                    developed[i] = dev;
                }
            }
            return developed[index];
        }
        private static readonly IntVec2[] WildOffsets =
        {
            new IntVec2(40, 0), new IntVec2(-40, 0), new IntVec2(0, 40), new IntVec2(0, -40),
            new IntVec2(80, 0), new IntVec2(-80, 0), new IntVec2(0, 80), new IntVec2(0, -80),
            new IntVec2(40, 40), new IntVec2(-40, 40), new IntVec2(40, -40), new IntVec2(-40, -40),
            new IntVec2(120, 0), new IntVec2(-120, 0), new IntVec2(0, 120), new IntVec2(0, -120),
        };
        private bool TryWild(ref int sx, ref int sz)
        {
            foreach (IntVec2 o in WildOffsets)
            {
                int x = sx + o.x, z = sz + o.z;
                if (!domain.Inside(x, z))
                {
                    continue;
                }
                int wild = map.cellIndices.CellToIndex(x, z);
                if (!Developed(wild) && Roads?.Ghost(wild) != true)
                {
                    sx = x;
                    sz = z;
                    return true;
                }
            }
            return false;
        }
        private static readonly int[] BesideSteps = { 4, 6, 8, 12, 16 };
        private bool TryBeside(ref int sx, ref int sz)
        {
            foreach (int step in BesideSteps)
            {
                for (int d = 0; d < 4; d++)
                {
                    int x = sx + GenAdj.CardinalDirections[d].x * step, z = sz + GenAdj.CardinalDirections[d].z * step;
                    if (!domain.Inside(x, z)) continue;
                    int i = map.cellIndices.CellToIndex(x, z);
                    if (Roads.NearRoad(i) || Developed(i)) continue;
                    TerrainDef t = map.terrainGrid.BaseTerrainAt(new IntVec3(x, 0, z));
                    if (!Usable(t) || t.IsWater) continue;
                    sx = x;
                    sz = z;
                    return true;
                }
            }
            return false;
        }
        private TerrainDef OutsideTerrain(IntVec3 c, out int sample, out int rawSample)
        {
            if (warpX == null)
            {
                int seed = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode()) ^ 0x45484F52;
                warpX = new Verse.Noise.Perlin(0.045, 2.0, 0.5, 3, seed, Verse.Noise.QualityMode.Medium);
                warpZ = new Verse.Noise.Perlin(0.045, 2.0, 0.5, 3, seed + 1, Verse.Noise.QualityMode.Medium);
                macroX = new Verse.Noise.Perlin(0.012, 2.0, 0.5, 2, seed + 2, Verse.Noise.QualityMode.Medium);
                macroZ = new Verse.Noise.Perlin(0.012, 2.0, 0.5, 2, seed + 3, Verse.Noise.QualityMode.Medium);
            }
            if (Generator != null)
            {
                TerrainDef g = GeneratorTerrain(c, out sample, out rawSample);
                if (g != null)
                {
                    return g;
                }
            }
            float d = Math.Max(0, domain.Steps(c));
            float amp = Mathf.Min(12f, d * 0.4f);
            float macro = Mathf.Min(30f, d * 0.75f);
            float fx = c.x + (float)warpX.GetValue(c.x, 0, c.z) * amp + (float)macroX.GetValue(c.x, 0, c.z) * macro;
            float fz = c.z + (float)warpZ.GetValue(c.x, 0, c.z) * amp + (float)macroZ.GetValue(c.x, 0, c.z) * macro;
            domain.Reflect(Mathf.RoundToInt(fx), Mathf.RoundToInt(fz), out int sx, out int sz);
            int source = map.cellIndices.CellToIndex(sx, sz);
            if (Developed(source) || Roads?.Ghost(source) == true)
            {
                RememberReflectedRoof(c, source);
                if (!(Roads?.NearRoad(source) == true && TryBeside(ref sx, ref sz)) && !TryWild(ref sx, ref sz))
                {
                    sample = rawSample = -1;
                    return Dominant(Clamp(c));
                }
                WildSubstitutions++;
            }
            TerrainDef t = map.terrainGrid.BaseTerrainAt(new IntVec3(sx, 0, sz));
            sample = map.cellIndices.CellToIndex(sx, sz);
            rawSample = sample;
            if (!Usable(t) || t.IsRiver || NearRiver(sx, sz))
            {
                t = Dominant(Clamp(c));
                sample = -1;
            }
            return t;
        }
        private bool NearRiver(int x, int z)
        {
            if (nearRiver == null)
            {
                int w = domain.W, h = domain.H;
                nearRiver = new bool[w * h];
                int radius = Mathf.Clamp((map.Biome?.riverbankSizeRange.max ?? 4) + 2, 3, 10);
                for (int cz = 0; cz < h; cz++)
                {
                    for (int cx = 0; cx < w; cx++)
                    {
                        TerrainDef t = map.terrainGrid.BaseTerrainAt(new IntVec3(cx, 0, cz));
                        if (t == null || !t.IsRiver)
                        {
                            continue;
                        }
                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            int zz = cz + dz;
                            if (zz < 0 || zz >= h) continue;
                            for (int dx = -radius; dx <= radius; dx++)
                            {
                                int xx = cx + dx;
                                if (xx >= 0 && xx < w) nearRiver[zz * w + xx] = true;
                            }
                        }
                    }
                }
            }
            return nearRiver[domain.MapIndex(x, z)];
        }
        internal TerrainDef Dominant(IntVec3 edgeCell)
        {
            int key = map.cellIndices.CellToIndex(edgeCell);
            if (dominantCache.TryGetValue(key, out TerrainDef cached))
            {
                return cached;
            }
            var counts = new Dictionary<TerrainDef, int>();
            foreach (IntVec3 n in domain.InMapPart(CellRect.CenteredOn(edgeCell, 10)))
            {
                TerrainDef t = map.terrainGrid.BaseTerrainAt(n);
                if (Usable(t) && !t.IsWater && !NearRiver(n.x, n.z) && !Developed(map.cellIndices.CellToIndex(n))
                    && Roads?.Ghost(map.cellIndices.CellToIndex(n)) != true)
                {
                    counts.TryGetValue(t, out int k);
                    counts[t] = k + 1;
                }
            }
            TerrainDef best = null;
            int bestCount = 0;
            foreach (KeyValuePair<TerrainDef, int> kv in counts)
            {
                if (kv.Value > bestCount)
                {
                    best = kv.Key;
                    bestCount = kv.Value;
                }
            }
            best ??= map.Biome?.TerrainForAffordance(TerrainAffordanceDefOf.Light) ?? TerrainDefOf.Soil;
            dominantCache[key] = best;
            return best;
        }
        private void SetGenRoof(int index, float elev)
        {
            genRoofLevel ??= new byte[width * height];
            genRoofLevel[index] = elev > 0.7f * 1.14f ? (byte)2 : elev > 0.7f * 1.04f ? (byte)1 : (byte)0;
        }
        private Dictionary<IntVec3, RoofDef> reflectedRoof;
        internal bool RockBeforeRoads(int x, int z)
        {
            int i = Index(x, z);
            if (Generator != null) return genRock[i];
            int s = rawSamples[i];
            return s >= 0 && map.edificeGrid.InnerArray[s]?.def.mineable == true;
        }
        private void RememberReflectedRoof(IntVec3 c, int source)
        {
            if (Roads?.NearRoad(source) != true || map.edificeGrid.InnerArray[source]?.def.mineable == true) return;
            RoofDef roof = map.roofGrid.RoofAt(source);
            if (roof?.isNatural != true) return;
            reflectedRoof ??= new Dictionary<IntVec3, RoofDef>();
            reflectedRoof[c] = roof;
        }
        internal byte RoofBeforeRoads(IntVec3 c)
        {
            if (!domain.Outside(c)) return 0;
            int i = Index(c.x, c.z);
            RoofDef roof = null;
            if (Generator?.Snapshot?.OwnsRoof == true && Generator.Snapshot.Covers(c.x, c.z))
                roof = Generator.Snapshot.Roofs[Generator.Snapshot.Index(c.x, c.z)];
            else if (Generator != null)
                return genRoofLevel != null ? genRoofLevel[i] : (byte)0;
            else if (reflectedRoof == null || !reflectedRoof.TryGetValue(c, out roof))
                roof = rawSamples[i] >= 0 ? map.roofGrid.RoofAt(rawSamples[i]) : null;
            return roof?.isNatural != true ? (byte)0 : roof.isThickRoof ? (byte)2 : (byte)1;
        }

        internal static bool OwnsRiverAt(GenerationSnapshot snapshot, int x, int z) =>
            snapshot != null && (snapshot.OwnsFlow && snapshot.TerrainReady && snapshot.Covers(x, z) || snapshot.OwnsVanillaRiverAt(x, z));

        internal static bool ContinuesRivers(GenReconstruction generator) =>
            generator?.OwnsTerrain != true && generator?.Snapshot?.RiverOwned == null;

        internal IEnumerable<bool> FillSteps(CoastNoise.Source coast, CoastFallback CoastFallback, RiverExtension River)
        {
            bool workerMode = coast != null && coast.worker.GetType().Assembly != typeof(TileMutatorWorker_Coast).Assembly;
            if (coast != null && !workerMode) CoastMode = "selector";
            for (int z = -r; z < domain.H + r; z++)
            {
                IDisposable scope = coast != null ? new CoastNoise.Scope(coast) : null;
                try
                {
                    for (int x = -r; x < domain.W + r; x++)
                    {
                        var c = new IntVec3(x, 0, z);
                        if (domain.Inside(x, z))
                        {
                            continue;
                        }
                        TerrainDef edge = OutsideTerrain(c, out int sample, out int rawSample);
                        TerrainDef t = edge;
                        if (coast != null && Generator?.OwnsCoast != true)
                        {
                            TerrainDef ct = null;
                            if (workerMode)
                            {
                                try
                                {
                                    ct = CoastNoise.WorkerTerrainAt(coast, c, map);
                                }
                                catch (Exception ex)
                                {
                                    workerMode = false;
                                    CoastMode = "threshold (worker threw: " + ex.GetType().Name + ")";
                                    if (!coastWorkerLogged)
                                    {
                                        coastWorkerLogged = true;
                                        Log.Warning(HorizonLog.Prefix + "Coast worker failed for an outside cell; using the threshold fallback (logged once per session). " + ex);
                                    }
                                }
                            }
                            if (!workerMode)
                            {
                                ct = CoastNoise.TerrainAt(coast, c, Clamp(c), map, edge);
                            }
                            if (ct != null)
                            {
                                t = ct;
                                CoastCells++;
                            }
                            else if (MirroredSea(x, z, edge))
                            {
                                TerrainDef land = Dominant(Clamp(c));
                                t = land.IsIce ? land : MapGenUtility.BeachTerrainAt(Clamp(c), map) ?? TerrainDefOf.Sand;
                            }
                        }
                        else if (CoastFallback != null && Generator?.OwnsCoast != true)
                        {
                            float dist = domain.Steps(x, z);
                            bool mirroredSea = MirroredSea(x, z, edge);
                            float sea = CoastFallback.SeaScore(c, dist, mirroredSea);
                            IntVec3 near = Clamp(c);
                            if (sea > 0.1f) t = MapGenUtility.DeepOceanWaterTerrainAt(near, map);
                            else if (sea > 0f) t = MapGenUtility.ShallowOceanWaterTerrainAt(near, map);
                            else if (sea > -0.08f && !(edge.IsWater ? Dominant(near) : edge).IsIce) t = MapGenUtility.BeachTerrainAt(near, map) ?? TerrainDefOf.Sand;
                            else if (edge.IsOcean) t = Dominant(near);
                            if (t != edge && t.IsWater) CoastCells++;
                        }
                        if (ContinuesRivers(Generator) && River != null && !OwnsRiverAt(Generator?.Snapshot, x, z)
                            && !(t.IsOcean || (coast != null && t.IsWater && !edge.IsWater)))
                        {
                            TerrainDef rt = River.TerrainAt(c, t, Clamp(c));
                            if (rt != null)
                            {
                                t = rt;
                                RiverCells++;
                            }
                        }
                        cells[Index(x, z)] = t;
                        samples[Index(x, z)] = t == edge ? sample : -1;
                        rawSamples[Index(x, z)] = t.IsOcean && !edge.IsOcean ? -1 : rawSample;
                        if (samples[Index(x, z)] < 0 && (t.IsWater || !GenRock(c)))
                        {
                            int source = ReplacedSource(c, t);
                            samples[Index(x, z)] = source;
                            if (t.IsWater && rawSamples[Index(x, z)] < 0) rawSamples[Index(x, z)] = source;
                        }
                    }
                }
                finally { scope?.Dispose(); }
                yield return false;
            }
            if (coast != null && workerMode)
            {
                CoastMode = "worker";
            }
            if (Generator != null && ContinuesRivers(Generator))
            {
                ApplyRiverPaint();
            }
        }
    }
}
