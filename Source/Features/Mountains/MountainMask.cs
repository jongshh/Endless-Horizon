using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class MountainMask
    {
        private readonly Map map;
        private readonly HorizonDomain domain;
        private readonly LandGrid grid;
        private readonly GeneratorModule gen;
        private readonly RoadBuild roads;
        private readonly Func<int, int, byte> edgeState;
        private readonly int r, w, h, gw, gh;
        private readonly bool[] rock;
        private readonly ThingDef[] def;
        public int RockCells;
        public int RockCellsWithoutDef;
        public int EnclosureCells;
        internal readonly List<IntVec3> ClosedValleys = new List<IntVec3>();

        private TerrainDef GridTerrain(IntVec3 c) => domain.InGrid(c) ? grid.Cells[grid.Index(c.x, c.z)] : null;

        private int GridRawSample(IntVec3 c) => domain.InGrid(c) ? grid.RawSamples[grid.Index(c.x, c.z)] : -1;

        public MountainMask(Map map, HorizonDomain domain, LandGrid grid, GeneratorModule gen, RoadBuild roads, Func<int, int, byte> edgeState)
        {
            this.map = map;
            this.domain = domain;
            this.grid = grid;
            this.gen = gen;
            this.roads = roads;
            this.edgeState = edgeState;
            r = domain.R;
            w = domain.W;
            h = domain.H;
            gw = w + 2 * r;
            gh = h + 2 * r;
            rock = new bool[gw * gh];
            def = new ThingDef[gw * gh];
            var raw = new bool[gw * gh];
            var rawDef = new ThingDef[gw * gh];
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    if (domain.Inside(x, z))
                    {
                        continue;
                    }
                    var c = new IntVec3(x, 0, z);
                    if (roads.RoadCleared(c)) continue;
                    int s = GridRawSample(c);
                    TerrainDef t = GridTerrain(c);
                    if (gen.Generator != null)
                    {
                        if (t != null && !t.IsWater && gen.GenRock(c))
                        {
                            raw[Index(x, z)] = true;
                            rawDef[Index(x, z)] = gen.GenRockDef(c);
                        }
                        continue;
                    }
                    if (s < 0 || t == null || t.IsWater)
                    {
                        continue;
                    }
                    Building b = map.edificeGrid.InnerArray[s];
                    if (b != null && b.def.mineable && b.def.graphic is Graphic_Linked)
                    {
                        raw[Index(x, z)] = true;
                        rawDef[Index(x, z)] = b.def;
                    }
                }
            }
            var counts = new Dictionary<ThingDef, int>();
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    if (domain.Inside(x, z))
                    {
                        continue;
                    }
                    int dist = domain.Steps(x, z);
                    ThingDef edgeRock = null;
                    if (dist <= 2)
                    {
                        IntVec3 edge = domain.Nearest(new IntVec3(x, 0, z));
                        Building eb = map.edificeGrid.InnerArray[map.cellIndices.CellToIndex(edge)];
                        if (eb != null && eb.def.mineable && eb.def.graphic is Graphic_Linked)
                        {
                            edgeRock = eb.def;
                        }
                    }
                    var cell = new IntVec3(x, 0, z);
                    TerrainDef t = GridTerrain(cell);
                    if (t == null || t.IsWater || roads.RoadCleared(cell))
                    {
                        continue;
                    }
                    int n = 0;
                    counts.Clear();
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            ThingDef d = RawRockAt(raw, rawDef, x + dx, z + dz);
                            if (d != null)
                            {
                                n++;
                                counts.TryGetValue(d, out int k);
                                counts[d] = k + 1;
                            }
                        }
                    }
                    bool forceEdge = gen.Generator == null && edgeRock != null;
                    bool isRock = gen.Generator != null ? raw[Index(x,z)] : dist <= 2 ? forceEdge || raw[Index(x, z)] : n >= 5;
                    if (isRock)
                    {
                        int i = Index(x, z);
                        rock[i] = true;
                        ThingDef best = gen.Generator != null ? rawDef[i] : edgeRock ?? rawDef[i];
                        if (best == null)
                        {
                            int bestCount = 0;
                            foreach (KeyValuePair<ThingDef, int> kv in counts)
                            {
                                if (kv.Value > bestCount)
                                {
                                    best = kv.Key;
                                    bestCount = kv.Value;
                                }
                            }
                        }
                        def[i] = best;
                        RockCells++;
                        if (best == null) RockCellsWithoutDef++;
                    }
                }
            }
        }

        internal IEnumerable<bool> EncloseSteps()
        {
            var walls = new List<int>();
            foreach (bool step in OutsideEnclosure.WallSteps(domain, IsRock, edgeState, walls)) yield return step;
            yield return false;
            if (walls.Count > 0) Enclose(walls);
        }

        private void Enclose(List<int> walls)
        {
            void Remember(int x, int z)
            {
                if (edgeState(x, z) == 1) ClosedValleys.Add(new IntVec3(x, 0, z));
            }
            foreach (int i in domain.Edge) Remember(i % w, i / w);
            ThingDef local = null;
            foreach (ThingDef d in Find.World.NaturalRockTypesIn(map.Tile))
            {
                if (d.graphic is Graphic_Linked) { local = d; break; }
            }
            foreach (int i in walls)
            {
                int x = i % gw - r, z = i / gw - r;
                ThingDef stone = gen.Generator?.RockAt(x, z);
                if (!(stone?.graphic is Graphic_Linked))
                {
                    stone = map.edificeGrid.InnerArray[map.cellIndices.CellToIndex(domain.Nearest(new IntVec3(x, 0, z)))]?.def;
                    if (stone == null || !stone.mineable || !(stone.graphic is Graphic_Linked)) stone = local;
                }
                rock[i] = true;
                def[i] = stone;
                RockCells++;
                if (stone == null) RockCellsWithoutDef++;
            }
            EnclosureCells = walls.Count;
        }

        private int Index(int x, int z) => (z + r) * gw + (x + r);

        private bool InRing(int x, int z) => domain.InGrid(x, z);

        private ThingDef RawRockAt(bool[] raw, ThingDef[] rawDef, int x, int z)
        {
            if (domain.Inside(x, z))
            {
                Building b = map.edificeGrid.InnerArray[map.cellIndices.CellToIndex(x, z)];
                return b != null && b.def.mineable && b.def.graphic is Graphic_Linked ? b.def : null;
            }
            if (!InRing(x, z))
            {
                return null;
            }
            int i = Index(x, z);
            return raw[i] ? rawDef[i] : null;
        }

        public bool IsRock(int x, int z)
        {
            if (domain.Inside(x, z))
            {
                Building b = map.edificeGrid.InnerArray[map.cellIndices.CellToIndex(x, z)];
                return b != null && b.def.mineable;
            }
            x = Mathf.Clamp(x, -r, w + r - 1);
            z = Mathf.Clamp(z, -r, h + r - 1);
            return rock[Index(x, z)];
        }

        public ThingDef RockDefAt(int x, int z) => InRing(x, z) ? def[Index(x, z)] : null;

    }
}
