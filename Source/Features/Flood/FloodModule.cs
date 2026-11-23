using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using RimWorld;
using Verse;

namespace EndlessHorizon
{
    internal sealed class FloodModule : ILiveModule
    {
        internal const int Seasonal = 0, Torrential = 1;
        private const int CheckInterval = 120;
        private const int EdgeRows = 2;

        private sealed class Memory { internal readonly bool[] Kinds = new bool[2]; }
        private static readonly ConditionalWeakTable<Map, Memory> memories = new ConditionalWeakTable<Map, Memory>();

        private readonly HorizonDomain domain;
        private readonly LandGrid grid;
        private Map map;
        private bool failed;
        private TerrainDef[] flood;
        private List<int>[][] inside;
        private List<int>[][] outside;
        private TerrainDef[][] kindDef;
        private int[] edgeCells, edgeSources;
        private bool requested, wet;
        private int nextCheck;

        internal int FloodedCount { get; private set; }
        internal int Updates { get; private set; }
        internal double LastMs { get; private set; }
        internal double BuildMs { get; private set; }
        internal bool[] LastKinds { get; } = new bool[2];

        internal FloodModule(HorizonDomain domain, LandGrid grid)
        {
            this.domain = domain;
            this.grid = grid;
        }

        internal static bool Enabled => ModsConfig.OdysseyActive && TerrainDefOf.ShallowFloodwater != null;

        internal bool Ready => flood != null && !failed;

        internal TerrainDef FloodAt(IntVec3 c) => flood != null && domain.Outside(c) ? flood[grid.Index(c.x, c.z)] : null;

        internal void Request()
        {
            if (Ready) requested = true;
        }

        internal void Build(Map map, MountainMask mountains, RoadBuild roads)
        {
            this.map = map;
            if (!Enabled) return;
            var watch = Stopwatch.StartNew();
            try
            {
                Prepare(mountains, roads);
                Recalculate();
            }
            catch (Exception ex)
            {
                failed = true;
                flood = null;
                Log.Error(HorizonLog.Prefix + "Continuing floods outside failed; the outside shows none. " + ex);
            }
            BuildMs = watch.Elapsed.TotalMilliseconds;
        }

        private void Prepare(MountainMask mountains, RoadBuild roads)
        {
            int n = domain.Cells, width = domain.Stride, height = domain.Rows, r = domain.R;
            var open = new bool[n];
            var openInside = new bool[n];
            var water = new bool[n];
            var river = new bool[n];
            var fresh = new bool[n];
            var ground = new TerrainDef[n];
            var mapInside = new bool[n];
            var unroofed = new Dictionary<WaterBody, bool>(ReferenceEqualityComparer.Instance);
            TerrainGrid terrain = map.terrainGrid;
            for (int z = -r; z < domain.H + r; z++)
                for (int x = -r; x < domain.W + r; x++)
                {
                    int i = domain.Index(x, z);
                    var c = new IntVec3(x, 0, z);
                    if (domain.Inside(x, z))
                    {
                        mapInside[i] = true;
                        TerrainDef t = terrain.TerrainAt(c);
                        if (t == null || t.IsFlood) t = terrain.BaseTerrainAt(c);
                        ground[i] = terrain.BaseTerrainAt(c);
                        water[i] = t?.IsWater == true;
                        river[i] = t?.IsRiver == true;
                        open[i] = t != null && !t.IsWater && terrain.FoundationAt(c) == null && c.GetEdifice(map) == null;
                        openInside[i] = open[i];
                        if (water[i])
                        {
                            WaterBody body = c.GetWaterBody(map);
                            fresh[i] = body != null && body.waterBodyType != WaterBodyType.Saltwater && Unroofed(body);
                        }
                        continue;
                    }
                    TerrainDef o = grid.Cells[grid.Index(x, z)];
                    ground[i] = o;
                    if (o == null || mountains?.IsRock(x, z) == true) continue;
                    water[i] = o.IsWater;
                    river[i] = o.IsRiver;
                    fresh[i] = o.IsWater && !o.IsOcean && !roads.InTunnel(c);
                    open[i] = !o.IsWater && roads.RoadTopAt(c)?.bridge != true && !roads.InTunnel(c) && roads.BuildingAt(c) == null;
                }

            bool Unroofed(WaterBody body)
            {
                if (unroofed.TryGetValue(body, out bool known)) return known;
                bool any = false;
                if (body.cells != null)
                    foreach (IntVec3 cell in body.cells)
                        if (!cell.Roofed(map)) { any = true; break; }
                unroofed[body] = any;
                return any;
            }

            int salt = map.Tile.GetHashCode();
            inside = new List<int>[2][];
            outside = new List<int>[2][];
            kindDef = new TerrainDef[2][];
            FloodRules.EdgeCopies(domain, open, EdgeRows, out edgeCells, out edgeSources);
            var copies = new bool[n];
            foreach (int i in edgeCells) copies[i] = true;

            for (int kind = 0; kind < 2; kind++)
            {
                bool[] seedOf = kind == Seasonal ? river : fresh;
                bool[] notSame = kind == Seasonal ? river : water;
                var seeds = new bool[n];
                var seedsInside = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    if (!seedOf[i]) continue;
                    int x = i % width, z = i / width;
                    bool any = false, anyInside = false;
                    Look(x - 1, z); Look(x + 1, z); Look(x, z - 1); Look(x, z + 1);
                    seeds[i] = any;
                    seedsInside[i] = anyInside && mapInside[i];

                    void Look(int xx, int zz)
                    {
                        if (xx < 0 || zz < 0 || xx >= width || zz >= height) return;
                        int j = zz * width + xx;
                        if (notSame[j]) return;
                        any = true;
                        if (mapInside[j]) anyInside = true;
                    }
                }
                byte[] vanilla = FloodRules.Steps(width, height, seedsInside, openInside, out _, out _);
                byte[] joint = FloodRules.Steps(width, height, seeds, open, out int[] parent, out List<int> order);
                inside[kind] = NewSteps();
                outside[kind] = NewSteps();
                for (int i = 0; i < n; i++)
                {
                    if (mapInside[i])
                    {
                        byte d = vanilla[i];
                        if (d >= 1 && d <= FloodRules.Reach) inside[kind][d].Add(domain.MapIndex(i % width - r, i / width - r));
                    }
                    else if (!copies[i])
                    {
                        byte d = joint[i];
                        if (d >= 1 && d <= FloodRules.Reach) outside[kind][d].Add(i);
                    }
                }
                foreach (List<int> cells in outside[kind])
                    cells.Sort((a, b) =>
                    {
                        int k = FloodRules.StableOrder(a, salt).CompareTo(FloodRules.StableOrder(b, salt));
                        return k != 0 ? k : a.CompareTo(b);
                    });
                kindDef[kind] = kind == Torrential
                    ? FloodRules.Carry(n, parent, order, i => ground[i], TerrainDefOf.ShallowFloodwater)
                    : null;
            }
            flood = new TerrainDef[n];
        }

        private static List<int>[] NewSteps()
        {
            var steps = new List<int>[FloodRules.Reach + 1];
            for (int d = 0; d < steps.Length; d++) steps[d] = new List<int>();
            return steps;
        }

        private static bool Has(Map map, ThingDef def) => def != null && map.listerThings.ThingsOfDef(def).Count > 0;

        private bool[] Kinds(out bool present)
        {
            Memory memory = memories.GetValue(map, _ => new Memory());
            bool seasonal = Has(map, ThingDefOf.SeasonalFlood), torrential = Has(map, ThingDefOf.TorrentialRainFlood);
            present = seasonal || torrential;
            if (present)
            {
                memory.Kinds[Seasonal] = seasonal;
                memory.Kinds[Torrential] = torrential;
            }
            if (!memory.Kinds[Seasonal] && !memory.Kinds[Torrential]) return new[] { true, true };
            return (bool[])memory.Kinds.Clone();
        }

        internal List<IntVec3> Recalculate()
        {
            var changed = new List<IntVec3>();
            if (!Ready) return changed;
            var watch = Stopwatch.StartNew();
            var next = new TerrainDef[flood.Length];
            TerrainGrid terrain = map.terrainGrid;
            bool[] kinds = Kinds(out _);
            int insideWet = 0;
            var ratio = new float[FloodRules.Reach + 1];
            for (int kind = 0; kind < 2; kind++)
            {
                LastKinds[kind] = kinds[kind];
                if (!kinds[kind]) continue;
                int total = 0, wetTotal = 0;
                for (int d = 1; d <= FloodRules.Reach; d++)
                {
                    List<int> cells = inside[kind][d];
                    int w = 0;
                    foreach (int s in cells)
                        if (terrain.TempTerrainAt(s)?.IsFlood == true) w++;
                    ratio[d] = cells.Count > 0 ? w / (float)cells.Count : -1f;
                    total += cells.Count;
                    wetTotal += w;
                }
                insideWet += wetTotal;
                if (wetTotal == 0) continue;
                float overall = total > 0 ? wetTotal / (float)total : 0f;
                TerrainDef[] defs = kindDef[kind];
                FloodRules.Allocate(outside[kind], ratio, overall, i => next[i] ??= defs?[i] ?? TerrainDefOf.ShallowFloodwater);
            }
            for (int e = 0; e < edgeCells.Length; e++)
            {
                TerrainDef t = terrain.TempTerrainAt(edgeSources[e]);
                next[edgeCells[e]] = t != null && t.IsFlood ? t : null;
            }
            int count = 0;
            for (int i = 0; i < next.Length; i++)
            {
                if (next[i] != null) count++;
                if (next[i] != flood[i]) changed.Add(new IntVec3(i % grid.Width - grid.R, 0, i / grid.Width - grid.R));
            }
            flood = next;
            FloodedCount = count;
            wet = insideWet > 0 || count > 0;
            Updates++;
            LastMs = watch.Elapsed.TotalMilliseconds;
            return changed;
        }

        string ILiveModule.Name => "flood";
        string ILiveModule.OnEdit(IntVec3 cell, bool surfaceChanged) => null;
        string ILiveModule.RebuildReason(bool rebuildRequested) => null;

        bool ILiveModule.Due()
        {
            if (!Ready) return false;
            if (requested) return true;
            int now = Find.TickManager.TicksGame;
            if (now < nextCheck) return false;
            nextCheck = now + CheckInterval;
            if (!wet && !Has(map, ThingDefOf.SeasonalFlood) && !Has(map, ThingDefOf.TorrentialRainFlood)) return false;
            requested = true;
            return true;
        }

        bool ILiveModule.Immediate => false;

        IEnumerable<bool> ILiveModule.UpdateSteps(List<IntVec3> changed)
        {
            requested = false;
            try { changed.AddRange(Recalculate()); }
            catch (Exception ex)
            {
                Log.ErrorOnce(HorizonLog.Prefix + "Following the flood outside failed; the outside flood is cleared for this ring. " + ex, 0x45484644);
                if (flood != null)
                    for (int i = 0; i < flood.Length; i++)
                        if (flood[i] != null) changed.Add(new IntVec3(i % grid.Width - grid.R, 0, i / grid.Width - grid.R));
                failed = true;
                flood = null;
                FloodedCount = 0;
            }
            yield break;
        }

        bool ILiveModule.ChangesWater => false;

        void ILiveModule.Announce(CellRect changed, Action<string, CellRect> publish) => publish(Vocabulary.TerrainTop, changed);

        bool ILiveModule.Busy => false;

        internal string Profile()
        {
            if (!Ready) return failed ? "failed" : Enabled ? "not built" : "off (no Odyssey)";
            TerrainGrid terrain = map.terrainGrid;
            var b = new StringBuilder();
            b.Append($"flooded outside={FloodedCount} edgeCopies={edgeCells.Length} updates={Updates} lastMs={LastMs:F2} buildMs={BuildMs:F1}");
            b.Append($" kinds=seasonal:{LastKinds[Seasonal]},torrential:{LastKinds[Torrential]}");
            for (int kind = 0; kind < 2; kind++)
            {
                b.Append(kind == Seasonal ? " | seasonal d:map/out" : " | torrential d:map/out");
                for (int d = 1; d <= FloodRules.Reach; d++)
                {
                    int w = 0, o = 0;
                    foreach (int s in inside[kind][d]) if (terrain.TempTerrainAt(s)?.IsFlood == true) w++;
                    foreach (int i in outside[kind][d]) if (flood[i] != null) o++;
                    b.Append($" {d}:{w}/{inside[kind][d].Count},{o}/{outside[kind][d].Count}");
                }
            }
            return b.ToString();
        }
    }
}
