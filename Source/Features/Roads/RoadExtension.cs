using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.BaseGen;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal sealed class RoadCrossing
    {
        internal Rot4 Side;
        internal int Width;
        internal Vector2 Start;
        internal Vector2 Normal;
        internal Vector2 OutDir;
        internal Vector2 TargetDir;
        internal int TraceRows;
        internal readonly HashSet<TerrainDef> Seen = new();
        internal RoadDef Road;
        internal float Heading;
        internal float Turned;
        internal float Bend;
        internal float VisibleLength;
        internal string End = "not drawn";
        internal List<int> Run;
        internal EdgeRun Edge;
        internal List<Vector2> Lead;
        internal readonly List<IntVec3> RoofedExit = new();
        internal readonly List<IntVec3> CoveredAhead = new();

        public override string ToString() => Road == null
            ? $"{Side.ToStringHuman()} at ({Start.x:F0},{Start.y:F0}) width={Width}: unassigned (more crossings than world road links), not continued"
            : $"{Side.ToStringHuman()} at ({Start.x:F0},{Start.y:F0}) width={Width} def={Road.defName} trace={TraceRows} out=({OutDir.x:F2},{OutDir.y:F2}) "
              + $"world={Heading:F0}{(Lead != null ? " alongEdge" : "")} target=({TargetDir.x:F2},{TargetDir.y:F2}) turned={Turned:F0} bend={Bend:F0} length={VisibleLength:F0} end={End}";
    }

    internal sealed class RoadExtension
    {
        private const int MergeGap = 4;
        private const int TraceDepth = 12;
        private const int ShoulderCells = 3;
        private const float SamplesPerCell = 4f;
        private const float LeadIn = 12f;
        private static readonly float[] Bends = { 30f, 9f };
        private const float SharpBendCost = 6f;
        private const float OpenSkyMargin = 1.2f;
        private const float CornerCells = 24f;
        private const int SeamRows = 3;
        private const float SeamMiddle = 0.75f;
        private const float MinOutward = 0.35f;
        private const float MaxBridgeSpan = 30f;
        private static readonly float[] Turns = { 0f, 15f, -15f, 30f, -30f, 50f, -50f, 75f, -75f };

        private readonly Map map;
        private readonly HorizonDomain domain;
        private readonly int w, h, r, seed;
        internal readonly List<RoadCrossing> Crossings = new();
        internal readonly List<string> Notes = new();
        private readonly HashSet<TerrainDef> naturalPlaced = new();
        internal readonly HashSet<IntVec3> OpenSky = new();
        private bool[] nearRoad;

        private sealed class Link
        {
            internal RoadDef Road;
            internal float Heading;
            internal float ExitAngle;
        }

        internal RoadExtension(Map map, HorizonDomain domain)
        {
            this.map = map;
            this.domain = domain;
            w = domain.W; h = domain.H; r = domain.R;
            seed = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode()) ^ 0x524F4144;
        }

        internal bool Any => Crossings.Any(c => c.Road != null);

        internal void Locate()
        {
            List<IntVec3> saved = map.roadInfo?.roadEdgeTiles;
            List<Link> links = WorldLinks();
            if (!domain.Rectangle)
            {
                if (links.Count == 0) Notes.Add("no world road link");
                else
                    foreach (EdgeRun run in EdgeRun.Find(domain, IsPaved, MergeGap)) AddRegionCrossing(run);
                if (links.Count > 0 && Crossings.Count == 0) Notes.Add($"{links.Count} world road link(s) but no road on the region's boundary: not continued");
                Continue(links);
                return;
            }
            if (saved == null || saved.Count == 0)
            {
                Notes.Add(links.Count > 0 ? $"{links.Count} world road link(s) but no saved crossing: not continued" : "no road");
                return;
            }
            foreach (List<int> run in Runs(saved.Where(c => domain.OnEdge(c)).Select(c => Perimeter(c.x, c.z, w, h)), 2 * w + 2 * h - 4))
                AddCrossing(run);
            Continue(links);
        }

        private bool IsPaved(IntVec3 c) => IsRoadSurface(map.terrainGrid.TerrainAt(c), map.terrainGrid.FoundationAt(c));

        private void AddRegionCrossing(EdgeRun run)
        {
            var c = new RoadCrossing { Side = run.Side, Width = run.Width, Edge = run, Normal = run.Normal };
            float prev = 0f, half = run.Width / 2f + 2f;
            int gap = 0;
            var rows = new List<Vector2>();
            for (int d = 0; d < TraceDepth && gap <= 3; d++)
            {
                float sum = 0f;
                int n = 0;
                for (int t = Mathf.FloorToInt(prev - half - 2f); t <= Mathf.CeilToInt(prev + half + 2f); t++)
                {
                    IntVec3 cell = run.Cell(t, d);
                    if (!domain.Inside(cell) || !IsPaved(cell)) continue;
                    sum += t; n++;
                    TerrainDef top = map.terrainGrid.TerrainAt(cell);
                    if (top?.HasTag("Road") == true) c.Seen.Add(top);
                }
                if (n == 0) { gap++; continue; }
                gap = 0;
                prev = sum / n;
                rows.Add(new Vector2(d, prev));
            }
            c.TraceRows = rows.Count;
            float slope = 0f;
            if (rows.Count >= 4)
            {
                float md = rows.Average(p => p.x), mt = rows.Average(p => p.y), num = 0f, den = 0f;
                foreach (Vector2 p in rows)
                {
                    num += (p.x - md) * (p.y - mt);
                    den += (p.x - md) * (p.x - md);
                }
                slope = den > 0f ? Mathf.Clamp(num / den, -2f, 2f) : 0f;
            }
            c.Start = run.Centre;
            c.OutDir = (run.Normal - run.Tangent * slope).normalized;
            c.TargetDir = c.OutDir;
            Crossings.Add(c);
        }

        private void Continue(List<Link> links)
        {
            Assign(links);
            foreach (RoadCrossing c in Crossings)
            {
                if (c.Road?.roadGenSteps == null) continue;
                if (c.Run != null) FollowEdge(c);
                foreach (RoadDefGenStep step in c.Road.roadGenSteps)
                {
                    if (step is RoadDefGenStep_Place p && p.place is TerrainDef t && t.natural) naturalPlaced.Add(t);
                    if (!RoadRules.Supported(step)) Notes.Add($"{c.Road.defName}: step {step.GetType().FullName} skipped");
                }
            }
            if (Any) MarkNearRoad();
        }

        private List<Link> WorldLinks()
        {
            var result = new List<Link>();
            if (!map.Tile.Valid || !(map.TileInfo is SurfaceTile surface) || surface.Roads == null) return result;
            foreach (SurfaceTile.RoadLink link in surface.Roads)
            {
                if (link.road == null) continue;
                float heading = Find.WorldGrid.GetHeadingFromTo(map.Tile, link.neighbor);
                result.Add(new Link { Road = link.road, Heading = heading, ExitAngle = heading });
            }
            if (result.Count > 1)
            {
                float[] angles = ExitAngles(result.Select(l => l.Heading).ToArray());
                for (int i = 0; i < angles.Length; i++) result[i].ExitAngle = angles[i];
            }
            return result;
        }

        internal static float[] ExitAngles(float[] headings)
        {
            if (headings.Length < 2) return headings;
            Vector2 sum = Vector2.zero;
            foreach (float a in headings) sum += FromAngle(a);
            Vector2 shift = sum / -headings.Length;
            return headings.Select(a => ToAngle(FromAngle(a) + shift)).ToArray();
        }

        internal static Vector2 FromAngle(float degrees) => new Vector2(Mathf.Sin(degrees * Mathf.Deg2Rad), Mathf.Cos(degrees * Mathf.Deg2Rad));

        internal static float ToAngle(Vector2 v)
        {
            float a = Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg;
            return a < 0f ? a + 360f : a;
        }

        private static float AngleBetween(float a, float b)
        {
            float d = Mathf.Abs(a - b) % 360f;
            return d > 180f ? 360f - d : d;
        }

        internal static int Perimeter(int x, int z, int w, int h)
        {
            if (z == 0) return x;
            if (x == w - 1) return w - 1 + z;
            if (z == h - 1) return w - 1 + h - 1 + (w - 1 - x);
            return 2 * (w - 1) + h - 1 + (h - 1 - z);
        }

        internal static IntVec3 PerimeterCell(int p, int w, int h)
        {
            if (p < w) return new IntVec3(p, 0, 0);
            p -= w - 1;
            if (p < h) return new IntVec3(w - 1, 0, p);
            p -= h - 1;
            if (p < w) return new IntVec3(w - 1 - p, 0, h - 1);
            p -= w - 1;
            return new IntVec3(0, 0, h - 1 - p);
        }

        internal static List<List<int>> Runs(IEnumerable<int> positions, int length)
        {
            List<int> sorted = positions.Distinct().OrderBy(p => p).ToList();
            var runs = new List<List<int>>();
            foreach (int p in sorted)
            {
                if (runs.Count == 0 || p - runs[runs.Count - 1][runs[runs.Count - 1].Count - 1] > MergeGap + 1) runs.Add(new List<int>());
                runs[runs.Count - 1].Add(p);
            }
            if (runs.Count > 1)
            {
                List<int> first = runs[0], last = runs[runs.Count - 1];
                if (first[0] + length - last[last.Count - 1] <= MergeGap + 1)
                {
                    last.AddRange(first.Select(p => p + length));
                    runs.RemoveAt(0);
                }
            }
            return runs;
        }

        private IntVec3 Cell(Rot4 side, int t, int d)
        {
            if (side == Rot4.South) return new IntVec3(t, 0, d);
            if (side == Rot4.North) return new IntVec3(t, 0, h - 1 - d);
            if (side == Rot4.West) return new IntVec3(d, 0, t);
            return new IntVec3(w - 1 - d, 0, t);
        }

        private Vector2 Pos(Rot4 side, float t, float d)
        {
            if (side == Rot4.South) return new Vector2(t + 0.5f, d + 0.5f);
            if (side == Rot4.North) return new Vector2(t + 0.5f, h - 0.5f - d);
            if (side == Rot4.West) return new Vector2(d + 0.5f, t + 0.5f);
            return new Vector2(w - 0.5f - d, t + 0.5f);
        }

        private static bool IsRoadSurface(TerrainDef top, TerrainDef foundation) => top?.HasTag("Road") == true || foundation?.bridge == true;

        private void AddCrossing(List<int> run)
        {
            int length = 2 * w + 2 * h - 4;
            IntVec3 middle = PerimeterCell((run[0] + run[run.Count - 1]) / 2 % length, w, h);
            Rot4 side = middle.z == 0 ? Rot4.South : middle.z == h - 1 ? Rot4.North : middle.x == 0 ? Rot4.West : Rot4.East;
            bool alongX = side == Rot4.South || side == Rot4.North;
            var along = new List<float>();
            foreach (int p in run)
            {
                IntVec3 cell = PerimeterCell(p % length, w, h);
                along.Add(alongX ? cell.x : cell.z);
            }
            float centre = (along.Min() + along.Max()) / 2f;
            var c = new RoadCrossing { Side = side, Width = run[run.Count - 1] - run[0] + 1, Run = run };
            c.Normal = (Pos(side, 0f, -1f) - Pos(side, 0f, 0f)).normalized;

            int edge = alongX ? w : h, gap = 0;
            float prev = centre, half = c.Width / 2f + 2f;
            var rows = new List<Vector2>();
            for (int d = 0; d < TraceDepth && gap <= 3; d++)
            {
                int lo = Mathf.Max(0, Mathf.FloorToInt(prev - half - 2f)), hi = Mathf.Min(edge - 1, Mathf.CeilToInt(prev + half + 2f));
                float sum = 0f;
                int n = 0;
                for (int t = lo; t <= hi; t++)
                {
                    IntVec3 cell = Cell(side, t, d);
                    TerrainDef top = map.terrainGrid.TerrainAt(cell);
                    if (!IsRoadSurface(top, map.terrainGrid.FoundationAt(cell))) continue;
                    sum += t; n++;
                    if (top?.HasTag("Road") == true) c.Seen.Add(top);
                }
                if (n == 0) { gap++; continue; }
                gap = 0;
                prev = sum / n;
                rows.Add(new Vector2(d, prev));
            }
            c.TraceRows = rows.Count;
            float slope = 0f;
            if (rows.Count >= 4)
            {
                float md = rows.Average(p => p.x), mt = rows.Average(p => p.y), num = 0f, den = 0f;
                foreach (Vector2 p in rows)
                {
                    num += (p.x - md) * (p.y - mt);
                    den += (p.x - md) * (p.x - md);
                }
                slope = den > 0f ? Mathf.Clamp(num / den, -2f, 2f) : 0f;
                float traced = mt - slope * md;
                if (Mathf.Abs(traced - centre) <= c.Width / 2f + 1f) centre = traced;
            }
            c.Start = Pos(side, centre, 0f);
            c.OutDir = (Pos(side, -slope, -1f) - Pos(side, 0f, 0f)).normalized;
            c.TargetDir = c.OutDir;
            Crossings.Add(c);
        }

        private void FollowEdge(RoadCrossing c)
        {
            float core = CoreRadius(c.Road);
            if (c.Width <= AlongEdgeWidth(core)) return;
            int first = c.Run[0], last = c.Run[c.Run.Count - 1], inset = Mathf.CeilToInt(core);
            bool arrivesAtFirst = RoadInside(first, 2 * inset + 1) >= RoadInside(last, 2 * inset + 1);
            int from = arrivesAtFirst ? first : last, to = arrivesAtFirst ? last - inset : first + inset;
            c.Lead = EdgeLead(from, to, w, h, out Vector2 exit, out Vector2 outDir, out Vector2 normal);
            c.Start = exit; c.OutDir = outDir; c.Normal = normal;
        }

        internal static int AlongEdgeWidth(float core) => Math.Max(9, Mathf.RoundToInt(2f * core * 2.2f) + 2);

        private int RoadInside(int position, int fromDepth)
        {
            const int reach = 14;
            IntVec3 at = PerimeterCell(position % (2 * w + 2 * h - 4), w, h);
            int count = 0;
            for (int z = Math.Max(0, at.z - reach); z <= Math.Min(h - 1, at.z + reach); z++)
                for (int x = Math.Max(0, at.x - reach); x <= Math.Min(w - 1, at.x + reach); x++)
                {
                    if (Math.Min(Math.Min(x, w - 1 - x), Math.Min(z, h - 1 - z)) < fromDepth) continue;
                    var c = new IntVec3(x, 0, z);
                    if (IsRoadSurface(map.terrainGrid.TerrainAt(c), map.terrainGrid.FoundationAt(c))) count++;
                }
            return count;
        }

        internal static List<Vector2> EdgeLead(int from, int to, int w, int h, out Vector2 exit, out Vector2 outDir, out Vector2 normal)
        {
            int length = 2 * w + 2 * h - 4, sign = to >= from ? 1 : -1;
            Vector2 Centre(int p)
            {
                IntVec3 c = PerimeterCell((p % length + length) % length, w, h);
                return new Vector2(c.x + 0.5f, c.z + 0.5f);
            }
            var lead = new List<Vector2>();
            for (int p = from; p != to; p += sign)
                for (int k = 0; k < SamplesPerCell; k++) lead.Add(Vector2.Lerp(Centre(p), Centre(p + sign), k / SamplesPerCell));
            exit = Centre(to);
            Vector2 along = (Centre(to) - Centre(to - sign)).normalized;
            normal = new Vector2(along.y, -along.x) * sign;
            outDir = (along + normal).normalized;
            return lead;
        }

        private void Assign(List<Link> links)
        {
            if (Crossings.Count == 0 || links.Count == 0)
            {
                if (Crossings.Count > 0) Notes.Add("saved crossings but no world road link: not continued");
                return;
            }
            var cost = new float[links.Count, Crossings.Count];
            Vector2 middle = Middle();
            for (int c = 0; c < Crossings.Count; c++)
            {
                RoadCrossing crossing = Crossings[c];
                float at = ToAngle(crossing.Start - middle);
                for (int l = 0; l < links.Count; l++)
                    cost[l, c] = AngleBetween(links[l].ExitAngle, at) + (SurfaceFits(links[l].Road, crossing.Seen) ? 0f : 90f);
            }
            int[] chosen = Match(cost);
            for (int l = 0; l < chosen.Length; l++)
            {
                if (chosen[l] < 0)
                {
                    Notes.Add($"world link {links[l].Road.defName}@{links[l].Heading:F0} has no saved crossing: not continued");
                    continue;
                }
                Crossings[chosen[l]].Road = links[l].Road;
                Crossings[chosen[l]].Heading = links[l].Heading;
            }
        }

        private Vector2 Middle()
        {
            if (domain.Rectangle) return new Vector2(w / 2f, h / 2f);
            Vector2 sum = Vector2.zero;
            foreach (int i in domain.Edge) sum += new Vector2(i % w + 0.5f, i / w + 0.5f);
            return sum / Mathf.Max(1, domain.Edge.Length);
        }

        private static bool SurfaceFits(RoadDef road, HashSet<TerrainDef> seen)
        {
            if (seen.Count == 0 || road.roadGenSteps == null) return true;
            foreach (RoadDefGenStep step in road.roadGenSteps)
            {
                if (!(step is RoadDefGenStep_Place p) || !(p.place is TerrainDef t) || !t.HasTag("Road")) continue;
                if (seen.Contains(t)) return true;
                if (t == TerrainDefOf.FlagstoneSandstone && seen.Any(s => s.layerable && s.CostList?.Count > 0)) return true;
            }
            return false;
        }

        internal static int[] Match(float[,] cost)
        {
            int rows = cost.GetLength(0), columns = cost.GetLength(1);
            var current = new int[rows];
            var best = new int[rows];
            for (int i = 0; i < rows; i++) best[i] = -1;
            var used = new bool[columns];
            float bestCost = float.MaxValue;
            int wanted = Math.Min(rows, columns), budget = 200000;
            void Search(int row, int paired, float total)
            {
                if (total >= bestCost || --budget < 0 || paired + (rows - row) < wanted) return;
                if (row == rows)
                {
                    bestCost = total;
                    Array.Copy(current, best, rows);
                    return;
                }
                for (int c = 0; c < columns; c++)
                {
                    if (used[c]) continue;
                    used[c] = true; current[row] = c;
                    Search(row + 1, paired + 1, total + cost[row, c]);
                    used[c] = false;
                }
                current[row] = -1;
                Search(row + 1, paired, total);
            }
            Search(0, 0, 0f);
            return best;
        }

        internal (int inside, int outside, int blocked) Seam(RoadCrossing c, Func<IntVec3, TerrainDef> topAt, Func<IntVec3, TerrainDef> groundAt)
        {
            if (c.Edge != null) return RegionSeam(c, topAt, groundAt);
            bool alongX = c.Side == Rot4.South || c.Side == Rot4.North;
            IntVec3 middle = PerimeterCell((c.Run[0] + c.Run[c.Run.Count - 1]) / 2 % (2 * w + 2 * h - 4), w, h);
            int half = c.Width / 2 + 2;
            if (c.Lead != null)
            {
                middle = new IntVec3(Mathf.Clamp(Mathf.FloorToInt(c.Start.x), 0, w - 1), 0, Mathf.Clamp(Mathf.FloorToInt(c.Start.y), 0, h - 1));
                alongX = middle.z == 0 || middle.z == h - 1;
                half = Mathf.CeilToInt(CoreRadius(c.Road)) + 3;
            }
            int centre = c.Lead != null ? (alongX ? middle.x : middle.z) : Mathf.FloorToInt(alongX ? c.Start.x : c.Start.y);
            int edge = alongX ? w : h;
            int inside = 0, outside = 0, blocked = 0;
            for (int t = Math.Max(0, centre - half); t <= Math.Min(edge - 1, centre + half); t++)
            {
                Rot4 at = c.Lead == null ? c.Side : middle.z == 0 ? Rot4.South : middle.z == h - 1 ? Rot4.North : middle.x == 0 ? Rot4.West : Rot4.East;
                IntVec3 on = Cell(at, t, 0), off = Cell(at, t, -1);
                bool paved = IsRoadSurface(map.terrainGrid.TerrainAt(on), map.terrainGrid.FoundationAt(on));
                if (paved) inside++;
                TerrainDef top = topAt(off);
                if (IsRoadSurface(top, top?.bridge == true ? top : null)) outside++;
                else if (paved && RefusesPaving(c.Road, groundAt(off))) blocked++;
            }
            return (inside, outside, blocked);
        }

        private (int inside, int outside, int blocked) RegionSeam(RoadCrossing c, Func<IntVec3, TerrainDef> topAt, Func<IntVec3, TerrainDef> groundAt)
        {
            int inside = 0, outside = 0, blocked = 0;
            foreach (IntVec3 on in c.Edge.Cells)
            {
                bool paved = IsPaved(on);
                if (paved) inside++;
                var off = new IntVec3(on.x + Mathf.RoundToInt(c.Edge.Normal.x), 0, on.z + Mathf.RoundToInt(c.Edge.Normal.y));
                if (!domain.Outside(off)) continue;
                TerrainDef top = topAt(off);
                if (IsRoadSurface(top, top?.bridge == true ? top : null)) outside++;
                else if (paved && RefusesPaving(c.Road, groundAt(off))) blocked++;
            }
            return (inside, outside, blocked);
        }

        internal static bool RefusesPaving(RoadDef road, TerrainDef ground)
        {
            if (road?.roadGenSteps == null || ground == null) return false;
            bool any = false;
            foreach (RoadDefGenStep step in road.roadGenSteps)
            {
                if (!(step is RoadDefGenStep_Place place) || !(place.place is TerrainDef paving)) continue;
                any = true;
                if (!ground.changeable) continue;
                TerrainAffordanceDef need = paving.terrainAffordanceNeeded;
                if (need == null || ground.affordances?.Contains(need) == true) return false;
            }
            return any;
        }

        private void MarkNearRoad()
        {
            nearRoad = new bool[w * h];
            var seeds = new List<int>();
            for (int i = 0; i < nearRoad.Length; i++)
                if (IsRoadSurface(map.terrainGrid.TerrainAt(i), map.terrainGrid.FoundationAt(i))) seeds.Add(i);
            GenerationExternalCells.Spread(nearRoad, seeds, w, h, ShoulderCells);
        }

        internal bool NearRoad(int index) => nearRoad != null && nearRoad[index];

        internal bool Ghost(int index)
        {
            if (!NearRoad(index)) return false;
            IntVec3 c = map.cellIndices.IndexToCell(index);
            if (naturalPlaced.Contains(map.terrainGrid.BaseTerrainAt(c))) return true;
            return map.edificeGrid.InnerArray[index] == null && map.roofGrid.RoofAt(index)?.isNatural == true;
        }

        private bool Inside(int x, int z) => domain.Inside(x, z);

        private void ReadRoofedExits()
        {
            int length = 2 * w + 2 * h - 4;
            foreach (RoadCrossing c in Crossings)
            {
                if (c.Road == null) continue;
                IEnumerable<IntVec3> exits = c.Edge?.Cells ?? c.Run.Select(p => PerimeterCell(p % length, w, h));
                foreach (IntVec3 cell in exits)
                {
                    int i = map.cellIndices.CellToIndex(cell);
                    if (map.roofGrid.RoofAt(i)?.isNatural == true && map.edificeGrid.InnerArray[i]?.def.mineable != true) c.RoofedExit.Add(cell);
                }
            }
        }

        internal static Dictionary<IntVec3, byte> Tunnel(IEnumerable<IntVec3> exits, HashSet<IntVec3> band, Func<IntVec3, byte> roofBefore)
        {
            var tunnel = new Dictionary<IntVec3, byte>();
            var open = new Stack<IntVec3>(exits);
            while (open.Count > 0)
            {
                IntVec3 at = open.Pop();
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 next = at + GenAdj.CardinalDirections[d];
                    if (!band.Contains(next) || tunnel.ContainsKey(next)) continue;
                    byte roof = roofBefore(next);
                    if (roof == 0) continue;
                    tunnel.Add(next, roof);
                    open.Push(next);
                }
            }
            return tunnel;
        }

        private bool InGrid(int x, int z) => domain.InGrid(x, z);

        internal IEnumerable<bool> BuildSteps(Func<int, int, TerrainDef> terrain, Func<int, int, bool> rock, List<RoadSurface> built)
        {
            var rules = new RoadRules();
            var surface = new RoadSurface(-r, -r, w + 2 * r, h + 2 * r, map.generatorDef?.defaultUnderGridTerrain ?? TerrainDefOf.Sand,
                (x, z) => Inside(x, z) ? map.terrainGrid.TerrainAt(new IntVec3(x, 0, z)) : terrain(x, z),
                (x, z) => Inside(x, z) ? map.terrainGrid.FoundationAt(new IntVec3(x, 0, z)) : null);
            foreach (ThingDef def in Crossings.Where(c => c.Road?.roadGenSteps != null).SelectMany(c => c.Road.roadGenSteps)
                         .OfType<RoadDefGenStep_Place>().Select(p => p.place).OfType<ThingDef>().Distinct())
                foreach (Thing standing in map.listerThings.ThingsOfDef(def))
                    surface.Standing(def, standing.Position.x, standing.Position.z);
            foreach (RoadCrossing c in Crossings.Where(c => c.Road != null).OrderBy(c => c.Road.priority).ToList())
            {
                if (c.Road.roadGenSteps.NullOrEmpty())
                {
                    c.End = "RoadDef has no gen steps";
                    continue;
                }
                int roadSeed = Gen.HashCombineInt(seed, Gen.HashCombineInt(Mathf.RoundToInt(c.Start.x), Mathf.RoundToInt(c.Start.y)));
                List<Vector2> path = null;
                float best = float.MaxValue;
                float world = ToAngle(FromAngle(c.Heading));
                Vector2 normal = c.Edge != null ? c.Normal : ExitNormal(c.Start, c.Normal, w, h);
                foreach (float bendCells in Bends)
                    foreach (float turn in Turns)
                    {
                        Vector2 target = Outward(FromAngle(world + turn), normal);
                        List<Vector2> candidate = Trace(c, target, bendCells, roadSeed, terrain, rock, out float cost, out string end, out float length);
                        cost += Mathf.Abs(turn) * 0.6f + (bendCells < Bends[0] ? SharpBendCost : 0f);
                        if (cost < best)
                        {
                            best = cost; path = candidate;
                            c.TargetDir = target; c.Turned = turn; c.Bend = bendCells; c.End = end; c.VisibleLength = length;
                        }
                        yield return false;
                    }
                if (path == null || path.Count == 0) continue;

                int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
                foreach (Vector2 p in path)
                {
                    x0 = Math.Min(x0, Mathf.FloorToInt(p.x)); x1 = Math.Max(x1, Mathf.FloorToInt(p.x));
                    z0 = Math.Min(z0, Mathf.FloorToInt(p.y)); z1 = Math.Max(z1, Mathf.FloorToInt(p.y));
                }
                int reach = Mathf.CeilToInt(RoadDistanceField.Radius) + 1;
                x0 = Math.Max(-r, x0 - reach); z0 = Math.Max(-r, z0 - reach);
                x1 = Math.Min(w + r - 1, x1 + reach); z1 = Math.Min(h + r - 1, z1 + reach);
                if (x1 < x0 || z1 < z0) continue;
                var field = new RoadDistanceField(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
                for (int i = 0; i < path.Count; i++)
                {
                    field.Fill(path[i].x, path[i].y, i / SamplesPerCell);
                    if (i % 16 == 15) yield return false;
                }
                float core = CoreRadius(c.Road);
                float Draw(int x, int z, int n)
                {
                    int out_ = domain.Steps(x, z);
                    bool steady = out_ <= SeamRows && field.TryGet(x, z, out float from, out _, out _, out _)
                                  && SeamSteady(out_, from, core, out_ == 1 && from <= core && PavedInside(x, z));
                    return SeamDraw(Random01(roadSeed, x, z, n), n, steady);
                }
                foreach (bool step in rules.Apply(c.Road, field, surface, RockSurface(c), Draw, (x, z) => !Inside(x, z))) yield return step;
                for (int x = field.MinX; x < field.MinX + field.Width; x++)
                {
                    for (int z = field.MinZ; z < field.MinZ + field.Height; z++)
                    {
                        if (!field.TryGet(x, z, out float from, out _, out _, out _) || from > core + OpenSkyMargin || Inside(x, z) || !InGrid(x, z)) continue;
                        OpenSky.Add(new IntVec3(x, 0, z));
                        if (from <= core) surface.Clear(x, z);
                    }
                    if ((x & 15) == 15) yield return false;
                }
            }
            ReadRoofedExits();
            built.Add(surface);
        }

        internal static Vector2 ExitNormal(Vector2 start, Vector2 normal, int w, int h)
        {
            Vector2 other;
            if (Mathf.Abs(normal.x) > 0.5f)
            {
                if (start.y < CornerCells) other = new Vector2(0f, -1f);
                else if (start.y > h - CornerCells) other = new Vector2(0f, 1f);
                else return normal;
            }
            else
            {
                if (start.x < CornerCells) other = new Vector2(-1f, 0f);
                else if (start.x > w - CornerCells) other = new Vector2(1f, 0f);
                else return normal;
            }
            return (normal + other).normalized;
        }

        internal static bool SeamSteady(int out_, float from, float core, bool pavedInside) =>
            out_ <= SeamRows && (from <= SeamMiddle || out_ == 1 && from <= core && pavedInside);

        private bool PavedInside(int x, int z)
        {
            IntVec3 c = domain.Nearest(new IntVec3(x, 0, z));
            return IsRoadSurface(map.terrainGrid.TerrainAt(c), map.terrainGrid.FoundationAt(c));
        }

        internal static float SeamDraw(float value, int draw, bool middle) => !middle ? value : draw == 0 ? 0.5f : 0.1f + 0.3f * value;

        internal static float CoreRadius(RoadDef road)
        {
            float core = 0.9f;
            foreach (RoadDefGenStep step in road.roadGenSteps)
            {
                if (!(step is RoadDefGenStep_Place p) || !(p.place is TerrainDef t) || t.bridge || p.chancePerPositionCurve == null) continue;
                for (float x = 0f; x <= RoadDistanceField.Radius; x += 0.1f)
                    if (p.chancePerPositionCurve.Evaluate(x) >= 0.5f) core = Mathf.Max(core, x);
            }
            return core;
        }

        internal static Vector2 Outward(Vector2 wanted, Vector2 normal)
        {
            wanted = wanted.normalized;
            float out_ = Vector2.Dot(wanted, normal);
            if (out_ >= MinOutward) return wanted;
            var tangent = new Vector2(-normal.y, normal.x);
            float side = Vector2.Dot(wanted, tangent) < 0f ? -1f : 1f;
            return normal * MinOutward + tangent * (side * Mathf.Sqrt(1f - MinOutward * MinOutward));
        }

        private List<Vector2> Trace(RoadCrossing c, Vector2 target, float bendCells, int roadSeed, Func<int, int, TerrainDef> terrain,
            Func<int, int, bool> rock, out float cost, out string end, out float visible)
        {
            const float step = 1f / SamplesPerCell;
            float wander = Mathf.Min(2f, 45f / Mathf.Max(1, c.Road.tilesPerSegment));
            float rockCost = c.Road.pathingMode == RoadPathingDefOf.Avoid ? 0.5f : 0.05f;
            var bend = new Perlin(0.04, 2.0, 0.5, 2, roadSeed, QualityMode.Medium);
            TerrainAffordanceDef bridgeable = TerrainDefOf.Bridge?.terrainAffordanceNeeded;
            var path = new List<Vector2>();
            if (c.Lead != null) path.AddRange(c.Lead);
            else for (float s = -LeadIn; s < 0f; s += step) path.Add(c.Start + c.OutDir * s);

            Vector2 at = c.Start;
            float limit = (r + RoadDistanceField.Radius) / MinOutward + bendCells;
            float water = 0f, rocky = 0f, span = 0f;
            int spanStart = -1;
            end = "ring edge";
            visible = 0f;
            for (float s = 0f; s < limit; s += step)
            {
                float k = Mathf.Clamp01(s / bendCells);
                Vector2 dir = Vector2.Lerp(c.OutDir, target, k * k * (3f - 2f * k)).normalized;
                var side = new Vector2(-dir.y, dir.x);
                Vector2 p = at + side * ((float)bend.GetValue(s, 0, 0) * wander * Mathf.Clamp01(s / 20f));
                at += dir * step;
                int x = Mathf.FloorToInt(p.x), z = Mathf.FloorToInt(p.y);
                if (x < -r - RoadDistanceField.Radius || z < -r - RoadDistanceField.Radius
                    || x >= w + r + RoadDistanceField.Radius || z >= h + r + RoadDistanceField.Radius) break;
                if (s > 3f && Inside(x, z))
                {
                    end = "back into the map";
                    break;
                }
                if (InGrid(x, z) && !Inside(x, z))
                {
                    TerrainDef t = terrain(x, z);
                    if (t != null && (t.IsWater || t.passability == Traversability.Impassable))
                    {
                        bool bridge = !t.IsOcean && bridgeable != null && t.affordances?.Contains(bridgeable) == true;
                        if (spanStart < 0) spanStart = path.Count;
                        span += step; water += step;
                        if (!bridge || span > MaxBridgeSpan)
                        {
                            path.RemoveRange(spanStart, path.Count - spanStart);
                            visible -= span - step;
                            end = bridge ? "water too wide" : t.defName;
                            break;
                        }
                    }
                    else
                    {
                        span = 0f; spanStart = -1;
                        if (rock(x, z)) rocky += step;
                    }
                    visible += step;
                }
                path.Add(p);
            }
            cost = water + rocky * rockCost;
            if (end != "ring edge") cost += 40f + Mathf.Max(0f, r - visible) * 4f;
            return path;
        }

        private TerrainDef RockSurface(RoadCrossing c)
        {
            foreach (TerrainDef t in c.Seen)
                if (t.layerable && t.CostList?.Count > 0) return t;
            ThingDef stone = Find.World.NaturalRockTypesIn(map.Tile).FirstOrDefault()?.building?.mineableThing;
            ThingDef block = stone?.butcherProducts?.Count > 0 ? stone.butcherProducts[0].thingDef : null;
            return block == null ? null : BaseGenUtility.CorrespondingTerrainDef(block, false);
        }

        internal static float Random01(int seed, int x, int z, int draw)
        {
            unchecked
            {
                uint v = (uint)seed;
                foreach (int part in new[] { x, z, draw })
                {
                    uint k = (uint)part * 0xcc9e2d51u;
                    k = (k << 15) | (k >> 17);
                    v ^= k * 0x1b873593u;
                    v = ((v << 13) | (v >> 19)) * 5u + 0xe6546b64u;
                }
                v ^= v >> 16; v *= 0x85ebca6bu;
                v ^= v >> 13; v *= 0xc2b2ae35u;
                v ^= v >> 16;
                return (v >> 8) / 16777216f;
            }
        }
    }
}
