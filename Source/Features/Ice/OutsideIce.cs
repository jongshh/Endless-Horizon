using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class OutsideIce
    {
        private sealed class TemperatureState { public float Ratio; }
        private static readonly ConditionalWeakTable<Map, TemperatureState> temperatures = new ConditionalWeakTable<Map, TemperatureState>();
        private sealed class Group
        {
            public readonly List<int> Cells = new List<int>();
            public readonly HashSet<WaterBody> Bodies = new HashSet<WaterBody>(ReferenceEqualityComparer.Instance);
            public readonly HashSet<IntVec3> Contacts = new HashSet<IntVec3>();
        }
        private readonly Map map;
        private readonly LandGrid grid;
        private readonly MountainMask mountains;
        private readonly IceModule state;
        private readonly HorizonDomain domain;
        private readonly int w, h, r, gw, gh;
        private readonly List<Group> groups = new List<Group>();
        private readonly int[] edgeSample;
        private readonly bool[] eligible;
        public static bool Enabled => ModsConfig.OdysseyActive && TerrainDefOf.ThinIce != null;
        public int GroupCount => groups.Count;
        public float TemperatureRatioNow => temperatures.TryGetValue(map, out TemperatureState t) ? t.Ratio : -1f;

        public OutsideIce(Map map, HorizonDomain domain, LandGrid grid, MountainMask mountains, IceModule state)
        {
            this.map = map; this.grid = grid; this.mountains = mountains; this.state = state;
            this.domain = domain; w = domain.W; h = domain.H; r = domain.R; gw = w + 2 * r; gh = h + 2 * r;
            eligible = new bool[gw * gh];
            edgeSample = new int[eligible.Length];
            Array.Fill(edgeSample, -1);
            if (!Enabled) return;
            var dry = new bool[eligible.Length];
            for (int z = -r; z < h + r; z++)
                for (int x = -r; x < w + r; x++)
                {
                    int i = Index(x, z);
                    var c = new IntVec3(x, 0, z);
                    bool inside = Inside(x, z);
                    TerrainDef t = inside ? map.terrainGrid.BaseTerrainAt(c) : domain.InGrid(c) ? grid.Cells[grid.Index(x, z)] : null;
                    bool rock = inside ? c.GetEdifice(map)?.def.mineable == true : mountains?.IsRock(x, z) == true;
                    eligible[i] = !inside && t?.canFreeze == true && !rock;
                    dry[i] = rock || (t != null && !t.IsWater);
                    int d = domain.Steps(x, z);
                    if (eligible[i] && d <= 2)
                    {
                        IntVec3 edge = domain.Nearest(c);
                        if (map.terrainGrid.BaseTerrainAt(edge)?.canFreeze == true)
                            edgeSample[i] = edge.z * w + edge.x;
                    }
                }
            float[] distance = DistanceField.Chamfer(gw, gh, i => dry[i], gw + gh);
            foreach (List<int> component in Components(eligible, gw))
            {
                var group = new Group();
                groups.Add(group);
                group.Cells.AddRange(component);
                foreach (int j in component)
                {
                    int x = j % gw - r, z = j / gw - r;
                    foreach (IntVec3 offset in GenAdj.CardinalDirections)
                    {
                        int xx = x + offset.x, zz = z + offset.z;
                        if (!domain.InGrid(xx, zz)) continue;
                        if (Inside(xx, zz))
                        {
                            var c = new IntVec3(xx, 0, zz);
                            group.Contacts.Add(c);
                            continue;
                        }
                    }
                }
                group.Cells.Sort((a, b) =>
                {
                    int comparison = distance[a].CompareTo(distance[b]);
                    if (comparison != 0) return comparison;
                    comparison = StableOrder(a).CompareTo(StableOrder(b));
                    return comparison != 0 ? comparison : a.CompareTo(b);
                });
            }
        }

        private uint StableOrder(int i)
        {
            unchecked
            {
                uint value = (uint)(i ^ map.Tile.GetHashCode());
                value = (value ^ (value >> 16)) * 0x7feb352dU;
                value = (value ^ (value >> 15)) * 0x846ca68bU;
                return value ^ (value >> 16);
            }
        }
        private int Index(int x, int z) => (z + r) * gw + x + r;
        private bool Inside(int x, int z) => domain.Inside(x, z);

        internal static List<List<int>> Components(bool[] eligible, int width)
        {
            var result = new List<List<int>>();
            var seen = new bool[eligible.Length];
            var stack = new Stack<int>();
            for (int i = 0; i < eligible.Length; i++)
            {
                if (!eligible[i] || seen[i]) continue;
                var cells = new List<int>(); result.Add(cells);
                Visit(i);
                while (stack.Count > 0)
                {
                    int j = stack.Pop(); cells.Add(j);
                    if (j % width > 0) Visit(j - 1);
                    if (j % width < width - 1) Visit(j + 1);
                    if (j >= width) Visit(j - width);
                    if (j < eligible.Length - width) Visit(j + width);
                }
            }
            return result;
            void Visit(int i) { if (eligible[i] && !seen[i]) { seen[i] = true; stack.Push(i); } }
        }

        internal static float BodyRatio(IEnumerable<WaterBody> bodies, out bool any)
        {
            long frozen = 0, total = 0;
            if (bodies != null)
                foreach (WaterBody body in bodies)
                    if (body != null && body.numCellsCanFreeze > 0)
                    {
                        frozen += Math.Clamp(body.numCellsFrozen, 0, body.numCellsCanFreeze);
                        total += body.numCellsCanFreeze;
                    }
            any = total > 0;
            return any ? (float)(frozen / (double)total) : 0f;
        }

        internal static float InitialRatio(float temperature) => temperature < -2.5f ? 1f : 0f;

        internal static float TemperatureRatio(float previous, float temperature) => temperature <= -7f ? 1f : temperature >= 2f ? 0f : previous;

        public List<IntVec3> Recalculate()
        {
            bool[] next = new bool[eligible.Length];
            if (Enabled && groups.Count > 0)
            {
                float global = BodyRatio(map.waterBodyTracker?.Bodies, out bool any);
                if (!any)
                {
                    TemperatureState state = temperatures.GetValue(map, m => new TemperatureState { Ratio = InitialRatio(m.mapTemperature.OutdoorTemp) });
                    global = state.Ratio = TemperatureRatio(state.Ratio, map.mapTemperature.OutdoorTemp);
                }
                foreach (Group group in groups)
                {
                    group.Bodies.Clear();
                    foreach (IntVec3 c in group.Contacts)
                        if (map.terrainGrid.BaseTerrainAt(c)?.canFreeze == true)
                        {
                            WaterBody body = c.GetWaterBody(map);
                            if (body != null) group.Bodies.Add(body);
                        }
                    float ratio = BodyRatio(group.Bodies, out bool connected);
                    if (!connected) ratio = global;
                    var forced = new sbyte[group.Cells.Count];
                    for (int k = 0; k < group.Cells.Count; k++)
                    {
                        int sample = edgeSample[group.Cells[k]];
                        forced[k] = sample < 0 ? (sbyte)-1 : map.terrainGrid.TempTerrainAt(map.cellIndices.IndexToCell(sample)) == TerrainDefOf.ThinIce ? (sbyte)1 : (sbyte)0;
                    }
                    bool[] selected = Allocate(group.Cells.Count, ratio, forced);
                    for (int k = 0; k < selected.Length; k++) next[group.Cells[k]] = selected[k];
                }
            }
            return state.SetFrozen(next);
        }

        internal static bool[] Allocate(int count, float ratio, sbyte[] forced)
        {
            var result = new bool[count];
            int remaining = (int)Math.Round(Math.Clamp(ratio, 0f, 1f) * count, MidpointRounding.ToEven);
            for (int i = 0; i < count; i++) if (forced[i] == 1) { result[i] = true; remaining--; }
            for (int i = 0; i < count && remaining > 0; i++)
                if (forced[i] < 0) { result[i] = true; remaining--; }
            return result;
        }

        public long Signature()
        {
            if (!Enabled) return 0;
            unchecked
            {
                long signature = 17;
                var bodies = map.waterBodyTracker?.Bodies;
                if (bodies != null)
                    foreach (WaterBody body in bodies)
                    {
                        if (body == null) continue;
                        signature = signature * 31 + body.rootCell.GetHashCode();
                        signature = signature * 31 + body.numCellsCanFreeze;
                        signature = signature * 31 + (body.numCellsCanFreeze > 0 ? (int)Math.Floor(Math.Clamp(body.numCellsFrozen / (double)body.numCellsCanFreeze, 0d, 1d) * 20d) : 0);
                        signature = signature * 31 + (body.numCellsFrozen > 0 ? 1 : 0);
                    }
                float temperature = map.mapTemperature.OutdoorTemp;
                signature = signature * 31 + (temperature <= -7f ? 0 : temperature >= 2f ? 2 : 1);
                foreach (int i in domain.Edge) Edge(i % w, i / w);
                return signature;
                void Edge(int x, int z) => signature = signature * 31 + (map.terrainGrid.TempTerrainAt(new IntVec3(x, 0, z)) == TerrainDefOf.ThinIce ? 1 : 0);
            }
        }
    }
}
