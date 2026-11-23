using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal class GenerationRiverTerrain
    {
        internal const int Pad = 25;
        private const float RemoveRoofWidthThreshold = 20f, RiverCaveBankFactor = 0.3f, ShallowFactor = 0.2f;

        internal sealed class Node
        {
            internal Vector3 Start, End;
            internal float Width;
            internal int Seed;
            internal readonly List<Node> Children = new();
        }

        internal sealed class Terrains
        {
            internal Func<IntVec3, TerrainDef> DeepAt, ShallowAt, BankAt;
            internal Func<IntVec3, TerrainDef> DeepFreshAt, ShallowFreshAt, LakeshoreAt;
            internal Func<TerrainDef, bool> BankAllowed;
            internal IntRange BankSize;

            internal static Terrains Vanilla(BiomeDef primary, IEnumerable<TileMutatorDef> mutators, Func<int, int, BiomeDef> biomeAt)
            {
                TerrainDef bankOverride = mutators?.FirstOrDefault(m => m.overrideRiverbankTerrain != null)?.overrideRiverbankTerrain;
                TerrainDef shoreOverride = mutators?.FirstOrDefault(m => m.overrideLakeBeachTerrain != null)?.overrideLakeBeachTerrain;
                var fertile = new HashSet<TerrainDef>(primary.terrainsByFertility?.Select(t => t.terrain) ?? Enumerable.Empty<TerrainDef>());
                return new Terrains
                {
                    DeepAt = c => biomeAt(c.x, c.z).waterMovingChestDeepTerrain ?? TerrainDefOf.WaterMovingChestDeep,
                    ShallowAt = c => biomeAt(c.x, c.z).waterMovingShallowTerrain ?? TerrainDefOf.WaterMovingShallow,
                    BankAt = c => bankOverride ?? biomeAt(c.x, c.z).riverbankTerrain ?? TerrainDefOf.Riverbank,
                    DeepFreshAt = c => biomeAt(c.x, c.z).waterDeepTerrain ?? TerrainDefOf.WaterDeep,
                    ShallowFreshAt = c => biomeAt(c.x, c.z).waterShallowTerrain ?? TerrainDefOf.WaterShallow,
                    LakeshoreAt = c => shoreOverride ?? biomeAt(c.x, c.z).lakeBeachTerrain ?? TerrainDefOf.Sand,
                    BankAllowed = fertile.Contains,
                    BankSize = primary.riverbankSizeRange,
                };
            }
        }

        internal struct Cell
        {
            internal float Depth, RiverWidth;
            internal TerrainDef Terrain;
            internal bool River;
            internal bool ClearsEdifice;
            internal bool RemovesRoof;
            internal bool CollapseCandidate;
            internal bool Extrapolated;
        }

        internal readonly int Width, Height;
        internal readonly IntVec3 Center;
        internal readonly IReadOnlyList<Node> Nodes;
        protected readonly ModuleBase bendNoise, widthNoise, shallowizer, bankNoise;

        internal GenerationRiverTerrain(int width, int height, IntVec3 center, IReadOnlyList<Node> nodes,
            ModuleBase bendNoise, ModuleBase shallowizer, ModuleBase bankNoise, ModuleBase widthNoise)
        {
            Width = width; Height = height; Center = center; Nodes = nodes;
            this.bendNoise = bendNoise; this.shallowizer = shallowizer; this.bankNoise = bankNoise; this.widthNoise = widthNoise;
        }

        protected virtual float CurveAmplitude => 40f;
        protected virtual float WidthNoiseFactor(Node node) => 6f / node.Width;

        internal bool InPad(int x, int z) => x >= -Pad && z >= -Pad && x < Width + Pad && z < Height + Pad;

        protected float TValue(Node node, Vector2 point)
        {
            Vector2 vector = new Vector2(node.Start.x, node.Start.z);
            Vector2 vector2 = new Vector2(node.End.x, node.End.z) - vector;
            Vector2 lhs = point - vector;
            float num = Vector2.Dot(vector2, vector2);
            return Vector2.Dot(lhs, vector2) / num;
        }

        protected virtual Vector2 DisplacedPoint(Node node, float t)
        {
            Vector3 vector = node.End - node.Start;
            Vector3 vector2 = node.Start + t * vector;
            float num = -4f * Mathf.Pow(t, 2f) + 4f * t;
            float num2 = t * Vector3.Distance(node.Start, node.End);
            Vector3 normalized = new Vector3(0f - vector.z, 0f, vector.x).normalized;
            float num3 = (float)bendNoise.GetValue(num2 * WidthNoiseFactor(node), 0.0, node.Seed);
            Vector3 vector3 = vector2 + num3 * CurveAmplitude * normalized * num;
            return new Vector2(vector3.x, vector3.z);
        }

        protected virtual float RiverWidthAt(Node node, Vector2 cell)
        {
            return node.Width / 2f * (1f + widthNoise.GetValue(cell) * 0.15f * WidthNoiseFactor(node));
        }

        internal float SegmentDepth(Node node, int x, int z)
        {
            float num3 = float.MinValue;
            Vector2 vector = new Vector2(x, z);
            float tValue = TValue(node, vector);
            if (!(tValue < 0f) && !(tValue > 1f))
            {
                Vector2 displacedPoint = DisplacedPoint(node, tValue);
                float riverWidthAt = RiverWidthAt(node, vector);
                Vector2 normalized = (DisplacedPoint(node, tValue - 0.001f) - displacedPoint).normalized;
                float num4 = Mathf.Abs(Vector2.Dot(vector - displacedPoint, new Vector2(0f - normalized.y, normalized.x)));
                num3 = riverWidthAt - num4;
            }
            return num3;
        }

        internal float DepthAt(int x, int z, out float riverWidth)
        {
            float num = float.MinValue;
            riverWidth = 0f;
            foreach (Node node in Nodes)
            {
                float d = SegmentDepth(node, x, z);
                if (d > num)
                {
                    num = Mathf.Max(num, d);
                    riverWidth = node.Width;
                }
            }
            return num;
        }

        private float ContinuedDepth(int x, int z, out float riverWidth)
        {
            riverWidth = 0f;
            float best = float.MinValue;
            var cell = new Vector2(x, z);
            foreach (Node node in Nodes)
                for (int end = 0; end < 2; end++)
                {
                    Vector3 at = end == 0 ? node.Start : node.End;
                    if (!OpenEnd(at) || Shared(node, at)) continue;
                    Vector2 point = DisplacedPoint(node, end == 0 ? 0f : 1f);
                    Vector2 inner = DisplacedPoint(node, end == 0 ? 0.001f : 0.999f);
                    Vector2 dir = (point - inner).normalized;
                    float t = TValue(node, cell);
                    if (end == 0 ? t >= 0f : t <= 1f) continue;
                    Vector2 offset = cell - point;
                    float d = RiverWidthAt(node, cell) - Mathf.Abs(Vector2.Dot(offset, new Vector2(-dir.y, dir.x)));
                    if (d > best) { best = d; riverWidth = node.Width; }
                }
            return best;
        }

        private bool OpenEnd(Vector3 at) =>
            at.x < -Pad / 2f || at.z < -Pad / 2f || at.x > Width + Pad / 2f || at.z > Height + Pad / 2f;

        private bool Shared(Node node, Vector3 at)
        {
            foreach (Node other in Nodes)
            {
                if (other == node) continue;
                if ((other.Start - at).sqrMagnitude < 1f || (other.End - at).sqrMagnitude < 1f) return true;
            }
            return false;
        }

        internal TerrainDef RiverTerrainAt(IntVec3 c, TerrainDef existing, float depth, Terrains terrains)
        {
            if (existing.IsWater && !existing.IsRiver && existing != TerrainDefOf.Marsh) return null;
            if (depth > 4f && shallowizer.GetValue(c) > ShallowFactor) return terrains.DeepAt(c);
            if (depth > 0f) return terrains.ShallowAt(c);
            return null;
        }

        internal TerrainDef BankTerrainAt(IntVec3 c, TerrainDef existing, float depth, Terrains terrains)
        {
            if (existing.IsWater && existing != TerrainDefOf.Marsh) return null;
            if (!terrains.BankAllowed(existing)) return null;
            float value = bankNoise.GetValue(c);
            int num = terrains.BankSize.Lerped(value);
            if (depth > (float)(-num)) return terrains.BankAt(c);
            return null;
        }

        internal Cell At(int x, int z, TerrainDef existing, bool edifice, Terrains terrains)
        {
            var c = new IntVec3(x, 0, z);
            var result = new Cell { Extrapolated = !InPad(x, z) };
            float depth = DepthAt(x, z, out float riverWidth);
            if (x < 0 || z < 0 || x >= Width || z >= Height)
            {
                float continued = ContinuedDepth(x, z, out float continuedWidth);
                if (continued > depth) { depth = continued; riverWidth = continuedWidth; result.Extrapolated = true; }
            }
            TerrainDef river = RiverTerrainAt(c, existing, depth, terrains);
            TerrainDef bank = BankTerrainAt(c, existing, depth, terrains);
            float value = bankNoise.GetValue(c);
            float num = (float)terrains.BankSize.Lerped(value) * RiverCaveBankFactor;
            bool wide = riverWidth > RemoveRoofWidthThreshold;
            result.Depth = depth; result.RiverWidth = riverWidth;
            if (depth > 0f - num)
            {
                result.ClearsEdifice = true;
                if (wide) result.RemovesRoof = true;
                else if (edifice) result.CollapseCandidate = true;
            }
            if (river != null) { result.Terrain = river; result.River = true; }
            else if (bank != null && !edifice) result.Terrain = bank;
            TerrainDef after = AfterRiverAt(c, result.Terrain ?? existing, terrains);
            if (after != null) result.Terrain = after;
            return result;
        }

        protected virtual TerrainDef AfterRiverAt(IntVec3 c, TerrainDef current, Terrains terrains) => null;

        internal sealed class FlowSamples
        {
            internal readonly List<IntVec3> Cells = new();
            internal readonly List<float> Widths = new();
            internal readonly List<Vector2> Steps = new();
        }

        internal FlowSamples Flow()
        {
            var samples = new FlowSamples();
            int num = 2;
            foreach (Node node in Nodes)
            {
                float magnitude = (node.End - node.Start).magnitude;
                Vector3 normalized = (node.End - node.Start).normalized;
                Vector2 vector = (node.Start - normalized).ToVector2();
                for (int i = 0; (float)i < magnitude; i += num)
                {
                    Vector2 displacedPoint = DisplacedPoint(node, (float)i / magnitude);
                    IntVec3 item = displacedPoint.ToVector3().ToIntVec3();
                    samples.Cells.Add(item);
                    samples.Widths.Add(node.Width);
                    samples.Steps.Add(displacedPoint - vector);
                    vector = displacedPoint;
                }
            }
            return samples;
        }

        internal static Vector2 FlowAt(FlowSamples samples, int x, int z)
        {
            var intVec = new IntVec3(x, 0, z);
            int num3 = 0;
            Vector2 zero = Vector2.zero;
            for (int m = 0; m < samples.Cells.Count; m++)
            {
                if (!((samples.Cells[m] - intVec).Magnitude > samples.Widths[m] * 1.5f / 2f + 1f))
                {
                    num3++;
                    zero += samples.Steps[m];
                }
            }
            zero /= (float)num3;
            return zero;
        }

        internal const float FlowGateTolerance = 0.01f;

        internal bool MatchesFlow(IReadOnlyList<float> flowMap, Func<int, int, bool> isRiver, out string detail)
        {
            if (flowMap == null || flowMap.Count != Width * Height * 2)
            {
                detail = flowMap == null ? "no saved river flow map" : "saved river flow map has " + flowMap.Count + " values, expected " + Width * Height * 2;
                return false;
            }
            FlowSamples samples = Flow();
            int compared = 0, differ = 0;
            for (int x = 0; x < Width; x++)
                for (int z = 0; z < Height; z++)
                {
                    if (!isRiver(x, z)) continue;
                    compared++;
                    int n = (x * Height + z) * 2;
                    Vector2 mine = FlowAt(samples, x, z);
                    if (!Same(mine.x, flowMap[n]) || !Same(mine.y, flowMap[n + 1])) differ++;
                }
            detail = $"flow {compared - differ}/{compared} river cells match the saved flow map";
            if (compared == 0) return Nodes.Count == 0;
            return differ <= compared * FlowGateTolerance;

            static bool Same(float a, float b) => float.IsNaN(a) ? float.IsNaN(b) : Math.Abs(a - b) <= 1e-4f * (1f + Math.Abs(b));
        }

        internal static bool EdgeNodes(IntVec3 center, int width, int height, float angle, out Vector3 a, out Vector3 b)
        {
            float slope = Mathf.Tan((450f - angle) % 360f * ((float)Math.PI / 180f));
            List<Vector2> intersections = new List<Vector2>();
            GenGeo.LineRectIntersection(new Vector2(center.x, center.z), slope, new Vector2(-25f, -25f), new Vector2(width + 25, height + 25), ref intersections);
            if (intersections.Count < 2) { a = b = default; return false; }
            a = new Vector3(intersections[0].x, 0f, intersections[0].y);
            b = new Vector3(intersections[1].x, 0f, intersections[1].y);
            return true;
        }

        internal static bool IsFlowingAToB(Vector3 a, Vector3 b, float angle) => Mathf.RoundToInt((b - a).AngleFlat() - angle) % 360 == 0;

        internal static Node PlainNode(IntVec3 center, int width, int height, float heading, float riverWidth, int seed)
        {
            if (!EdgeNodes(center, width, height, heading, out Vector3 a, out Vector3 b)) return null;
            bool forward = IsFlowingAToB(a, b, heading);
            return new Node { Start = forward ? a : b, End = forward ? b : a, Width = riverWidth, Seed = seed };
        }

        internal static bool Reproducible(Type worker) => worker == typeof(TileMutatorWorker_River)
            || worker == typeof(TileMutatorWorker_RiverIsland) || worker == typeof(TileMutatorWorker_RiverConfluence)
            || worker == typeof(TileMutatorWorker_RiverDelta) || worker == typeof(TileMutatorWorker_Headwater);

        private sealed class Unreproducible : Exception
        {
            internal Unreproducible(string reason) : base(reason) { }
        }

        internal static GenerationRiverTerrain Reproduce(Map map, int width, int height, TileMutatorWorker_River worker, bool odyssey,
            Func<IntVec3, TerrainDef> terrainAt, out string reason)
        {
            reason = null;
            Type type = worker.GetType();
            if (!Reproducible(type))
            {
                reason = "river variant not reproduced: " + type.Name;
                return null;
            }
            T Field<T>(Type owner, string name) => (T)AccessTools.Field(owner, name).GetValue(worker);
            Type river = typeof(TileMutatorWorker_River);
            ModuleBase bend = Field<ModuleBase>(river, "riverBendNoise"), shallow = Field<ModuleBase>(river, "shallowizer");
            ModuleBase bank = Field<ModuleBase>(river, "riverbankNoise"), widthNoise = Field<ModuleBase>(river, "riverWidthNoise");
            IntVec3 center = Field<IntVec3>(river, "riverCenter");
            if (bend == null || shallow == null || bank == null || widthNoise == null)
            {
                reason = "river Init was not replayed";
                return null;
            }
            var nodes = new List<Node>();
            GenerationRiverTerrain result;
            if (type == typeof(TileMutatorWorker_RiverIsland))
                result = new IslandRiverTerrain(width, height, center, nodes, bend, shallow, bank, widthNoise,
                    Field<float>(type, "riverIslandAmplitude"), Field<float>(type, "riverIslandCentrality"), Field<float>(type, "widthFrac"));
            else if (type == typeof(TileMutatorWorker_RiverDelta))
                result = new DeltaRiverTerrain(width, height, center, nodes, bend, shallow, bank, widthNoise);
            else if (type == typeof(TileMutatorWorker_Headwater))
            {
                ModuleBase lake = Field<ModuleBase>(type, "lakeNoise");
                if (lake == null)
                {
                    reason = "headwater Init did not run (no Odyssey; the original throws)";
                    return null;
                }
                result = new HeadwaterRiverTerrain(width, height, center, nodes, bend, shallow, bank, widthNoise, lake);
            }
            else result = new GenerationRiverTerrain(width, height, center, nodes, bend, shallow, bank, widthNoise);
            try
            {
                if (type == typeof(TileMutatorWorker_River)) PlainGraph(map, result, nodes);
                else if (type == typeof(TileMutatorWorker_RiverIsland)) { if (odyssey) IslandGraph(map, result, nodes); }
                else if (type == typeof(TileMutatorWorker_Headwater)) HeadwaterGraph(map, result, nodes);
                else if (type == typeof(TileMutatorWorker_RiverConfluence)) { if (odyssey) ConfluenceGraph(map, result, nodes); }
                else if (odyssey) DeltaGraph(map, result, nodes, terrainAt);
            }
            catch (Unreproducible missing)
            {
                reason = missing.Message;
                return null;
            }
            return result;
        }

        private static List<SurfaceTile.RiverLink> Links(SurfaceTile surface)
        {
            List<SurfaceTile.RiverLink> rivers = surface.Rivers;
            if (rivers.NullOrEmpty()) throw new Unreproducible("river Worker on a tile without river links (the original throws)");
            return rivers;
        }

        private void Edges(float angle, out Vector3 a, out Vector3 b)
        {
            if (!EdgeNodes(Center, Width, Height, angle, out a, out b)) throw new Unreproducible("river line misses the padded map (the original throws)");
        }

        private static void PlainGraph(Map map, GenerationRiverTerrain river, List<Node> graph)
        {
            if (!(map.TileInfo is SurfaceTile casted)) return;
            List<SurfaceTile.RiverLink> source = Links(casted).OrderBy(rl => -((SurfaceTile)rl.neighbor.Tile).riverDist).ToList();
            float headingFromTo = Find.WorldGrid.GetHeadingFromTo(source.First().neighbor.Tile.tile, source.Last().neighbor.Tile.tile);
            river.Edges(headingFromTo, out Vector3 vector, out Vector3 vector2);
            bool forward = IsFlowingAToB(vector, vector2, headingFromTo);
            var node = new Node { Seed = Rand.Int };
            node.Start = forward ? vector : vector2; node.End = forward ? vector2 : vector; node.Width = source.First().river.widthOnMap;
            graph.Add(node);
        }

        private static void IslandGraph(Map map, GenerationRiverTerrain river, List<Node> graph)
        {
            if (!(map.TileInfo is SurfaceTile casted)) return;
            List<SurfaceTile.RiverLink> source = Links(casted).OrderBy(rl => -((SurfaceTile)rl.neighbor.Tile).riverDist).ToList();
            float headingFromTo = Find.WorldGrid.GetHeadingFromTo(source.First().neighbor.Tile.tile, source.Last().neighbor.Tile.tile);
            river.Edges(headingFromTo, out Vector3 vector, out Vector3 vector2);
            bool forward = IsFlowingAToB(vector, vector2, headingFromTo);
            var nodeA = new Node { Seed = Rand.Int };
            nodeA.Start = forward ? vector : vector2; nodeA.End = forward ? vector2 : vector; nodeA.Width = source.First().river.widthOnMap;
            var nodeB = new Node { Seed = Rand.Int };
            nodeB.Start = forward ? vector : vector2; nodeB.End = forward ? vector2 : vector; nodeB.Width = source.First().river.widthOnMap;
            graph.Add(nodeA);
            graph.Add(nodeB);
        }

        private static void HeadwaterGraph(Map map, GenerationRiverTerrain river, List<Node> graph)
        {
            if (!(map.TileInfo is SurfaceTile casted)) return;
            SurfaceTile.RiverLink riverLink = Links(casted).First();
            float headingFromTo = Find.WorldGrid.GetHeadingFromTo(casted.tile, riverLink.neighbor.Tile.tile);
            river.Edges(headingFromTo, out Vector3 vector, out Vector3 vector2);
            bool forward = IsFlowingAToB(vector, vector2, headingFromTo);
            var node = new Node { Seed = Rand.Int };
            node.Start = river.Center.ToVector3Shifted(); node.End = forward ? vector2 : vector; node.Width = riverLink.river.widthOnMap;
            graph.Add(node);
        }

        private static void ConfluenceGraph(Map map, GenerationRiverTerrain river, List<Node> graph)
        {
            if (!(map.TileInfo is SurfaceTile casted)) return;
            List<SurfaceTile.RiverLink> list = Links(casted).ToList();
            SurfaceTile.RiverLink item = list.OrderBy(rl => ((SurfaceTile)rl.neighbor.Tile).riverDist).First();
            list.Remove(item);
            if (!list.Any(rl => !rl.neighbor.Tile.WaterCovered)) throw new Unreproducible("confluence without a land river link (the original throws)");
            SurfaceTile.RiverLink item2 = list.Where(rl => !rl.neighbor.Tile.WaterCovered).MaxBy(rl => rl.river.widthOnMap);
            list.Remove(item2);
            List<SurfaceTile.RiverLink> list2 = list.Where(rl => !rl.neighbor.Tile.WaterCovered).ToList();
            List<SurfaceTile.RiverLink> list3 = list.Where(rl => rl.neighbor.Tile.WaterCovered).ToList();
            float angle = Find.WorldGrid.GetHeadingFromTo(item2.neighbor.Tile.tile, item.neighbor.Tile.tile);
            Rot4 rot = Find.World.CoastDirectionAt(map.Tile);
            if (rot != Rot4.Invalid) angle = rot.AsAngle;
            river.Edges(angle, out Vector3 item3, out Vector3 item4);
            bool forward = IsFlowingAToB(item3, item4, angle);
            var riverNode = new Node { Seed = Rand.Int };
            riverNode.Start = forward ? item3 : item4; riverNode.End = forward ? item4 : item3; riverNode.Width = item2.river.widthOnMap;
            graph.Add(riverNode);
            float tValue = river.TValue(riverNode, new Vector2(river.Center.x, river.Center.z));
            Vector3 vector = river.DisplacedPoint(riverNode, tValue).ToVector3();
            foreach (SurfaceTile.RiverLink item9 in list2)
            {
                float headingFromTo = Find.WorldGrid.GetHeadingFromTo(item9.neighbor.Tile.tile, casted.tile);
                river.Edges(headingFromTo, out Vector3 vector2, out Vector3 vector3);
                bool flowing = IsFlowingAToB(vector2, vector3, headingFromTo);
                var node = new Node { Seed = Rand.Int };
                node.Start = flowing ? vector2 : vector3; node.End = vector; node.Width = item9.river.widthOnMap;
                graph.Add(node);
            }
            foreach (SurfaceTile.RiverLink item10 in list3)
            {
                float headingFromTo2 = Find.WorldGrid.GetHeadingFromTo(casted.tile, item10.neighbor.Tile.tile);
                river.Edges(headingFromTo2, out Vector3 vector4, out Vector3 vector5);
                bool flowing = IsFlowingAToB(vector4, vector5, headingFromTo2);
                var node = new Node { Seed = Rand.Int };
                node.Start = vector; node.End = flowing ? vector5 : vector4; node.Width = item10.river.widthOnMap;
                graph.Add(node);
            }
        }

        private static FloatRange SegmentLengthRange => new FloatRange(30f, 70f);
        private static FloatRange SplitAngleRange => new FloatRange(15f, 30f);
        private static FloatRange InitialLengthRange => new FloatRange(0.2f, 0.5f);

        private static void DeltaGraph(Map map, GenerationRiverTerrain river, List<Node> graph, Func<IntVec3, TerrainDef> terrainAt)
        {
            if (!(map.TileInfo is SurfaceTile casted)) return;
            List<SurfaceTile.RiverLink> rivers = Links(casted);
            if (!rivers.Any(rl => !rl.neighbor.Tile.WaterCovered)) throw new Unreproducible("delta without a land river link (the original throws)");
            SurfaceTile.RiverLink riverLink = rivers.Where(rl => !rl.neighbor.Tile.WaterCovered).MaxBy(rl => rl.river.widthOnMap);
            float? coast = Find.World.CoastAngleAt(map.Tile, BiomeDefOf.Ocean) ?? Find.World.CoastAngleAt(map.Tile, BiomeDefOf.Lake);
            if (!coast.HasValue) throw new Unreproducible("delta on a tile without a coast angle (the original throws)");
            float num = (coast.Value + 180f) % 360f;
            float angle = (450f - num) % 360f;
            river.Edges(angle, out Vector3 item, out Vector3 item2);
            var riverNode = new Node { Seed = Rand.Int };
            if (!IsFlowingAToB(item, item2, angle))
            {
                riverNode.Start = item2;
                riverNode.End = item2 + (item - item2) * InitialLengthRange.RandomInRange;
            }
            else
            {
                riverNode.Start = item;
                riverNode.End = item + (item2 - item) * InitialLengthRange.RandomInRange;
            }
            riverNode.Width = riverLink.river.widthOnMap;
            graph.Add(riverNode);
            SplitRiver(river, graph, riverNode, num, terrainAt);
            MergeNearbyNodes(graph, riverNode);
        }

        private static void SplitRiver(GenerationRiverTerrain river, List<Node> graph, Node prevSegment, float baseAngle, Func<IntVec3, TerrainDef> terrainAt)
        {
            Vector3 v = prevSegment.End;
            if (!(v.x >= 0f && v.z >= 0f && v.x < (float)river.Width && v.z < (float)river.Height)) return;
            TerrainDef there = terrainAt(prevSegment.End.ToIntVec3());
            if (there == null) throw new Unreproducible("delta: no terrain at a segment end");
            if (there.IsOcean) return;
            Vector3 normalized = (prevSegment.End - prevSegment.Start).normalized;
            Vector3 vector = new Vector3(0f - normalized.z, 0f, normalized.x);
            float num = 5f * (prevSegment.Width / 10f);
            if (!Rand.Chance(0.9f))
            {
                float f = (baseAngle + SplitAngleRange.RandomInRange - SplitAngleRange.Average) * ((float)Math.PI / 180f);
                float randomInRange = SegmentLengthRange.RandomInRange;
                float width = prevSegment.Width;
                Vector3 start = prevSegment.End - normalized * width / 4f;
                Vector3 end = new Vector3(prevSegment.End.x + randomInRange * Mathf.Cos(f), 0f, prevSegment.End.z + randomInRange * Mathf.Sin(f));
                var riverNode = new Node { Seed = Rand.Int };
                riverNode.Start = start; riverNode.End = end; riverNode.Width = width;
                graph.Add(riverNode);
                prevSegment.Children.Add(riverNode);
                SplitRiver(river, graph, riverNode, baseAngle, terrainAt);
                return;
            }
            float f2 = (baseAngle - SplitAngleRange.RandomInRange - num) * ((float)Math.PI / 180f);
            float randomInRange2 = SegmentLengthRange.RandomInRange;
            float num2 = Mathf.Max(prevSegment.Width * Rand.Range(0.33f, 0.67f), 1.5f);
            Vector3 start2 = prevSegment.End - normalized * num2 / 4f - vector * (prevSegment.Width / 2f - num2 / 2f);
            Vector3 end2 = new Vector3(prevSegment.End.x + randomInRange2 * Mathf.Cos(f2), 0f, prevSegment.End.z + randomInRange2 * Mathf.Sin(f2));
            var riverNode2 = new Node { Seed = Rand.Int };
            riverNode2.Start = start2; riverNode2.End = end2; riverNode2.Width = num2 * 1.1f;
            graph.Add(riverNode2);
            prevSegment.Children.Add(riverNode2);
            SplitRiver(river, graph, riverNode2, baseAngle, terrainAt);
            f2 = (baseAngle + SplitAngleRange.RandomInRange + num) * ((float)Math.PI / 180f);
            randomInRange2 = SegmentLengthRange.RandomInRange;
            num2 = Mathf.Max(prevSegment.Width - num2, 1.5f) + 1f;
            Vector3 start3 = prevSegment.End - normalized * num2 / 4f + vector * (prevSegment.Width / 2f - num2 / 2f);
            Vector3 end3 = new Vector3(prevSegment.End.x + randomInRange2 * Mathf.Cos(f2), 0f, prevSegment.End.z + randomInRange2 * Mathf.Sin(f2));
            var riverNode3 = new Node { Seed = Rand.Int };
            riverNode3.Start = start3; riverNode3.End = end3; riverNode3.Width = num2 * 1.1f;
            graph.Add(riverNode3);
            prevSegment.Children.Add(riverNode3);
            SplitRiver(river, graph, riverNode3, baseAngle, terrainAt);
        }

        private static void MergeNearbyNodes(List<Node> graph, Node root)
        {
            var markedForRemoval = new HashSet<Node>();
            var list = new List<Vector3>();
            var queue = new Queue<Node>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                Node riverNode = queue.Dequeue();
                bool flag = false;
                foreach (Vector3 item in list)
                {
                    if (!((riverNode.End - item).magnitude < 25f)) continue;
                    riverNode.End = item;
                    foreach (Node childNode in riverNode.Children) RecursivelyMarkForRemoval(childNode);
                    flag = true;
                }
                if (flag) continue;
                list.Add(riverNode.End);
                foreach (Node item2 in riverNode.Children.InRandomOrder()) queue.Enqueue(item2);
            }
            foreach (Node item3 in markedForRemoval) graph.Remove(item3);

            void RecursivelyMarkForRemoval(Node node)
            {
                markedForRemoval.Add(node);
                foreach (Node childNode2 in node.Children) RecursivelyMarkForRemoval(childNode2);
            }
        }
    }

    internal sealed class IslandRiverTerrain : GenerationRiverTerrain
    {
        private readonly float amplitude, centrality, widthFrac;

        internal IslandRiverTerrain(int width, int height, IntVec3 center, IReadOnlyList<Node> nodes, ModuleBase bendNoise,
            ModuleBase shallowizer, ModuleBase bankNoise, ModuleBase widthNoise, float amplitude, float centrality, float widthFrac)
            : base(width, height, center, nodes, bendNoise, shallowizer, bankNoise, widthNoise)
        {
            this.amplitude = amplitude; this.centrality = centrality; this.widthFrac = widthFrac;
        }

        protected override float CurveAmplitude => 20f;
        private Node NodeA => Nodes.Count > 0 ? Nodes[0] : null;

        protected override Vector2 DisplacedPoint(Node node, float t)
        {
            Vector3 vector = node.End - node.Start;
            Vector3 vector2 = node.Start + t * vector;
            float num = -4f * Mathf.Pow(t, 2f) + 4f * t;
            float num2 = t * Vector3.Distance(node.Start, node.End);
            Vector3 normalized = new Vector3(0f - vector.z, 0f, vector.x).normalized;
            float num3 = Mathf.InverseLerp(centrality, 1f - centrality, t);
            float num4 = (float)bendNoise.GetValue(num2 * WidthNoiseFactor(node), 0.0, node.Seed);
            float num5 = -4f * Mathf.Pow(num3, 2f) + 4f * num3;
            num4 = ((node != NodeA) ? (num4 - amplitude * num5) : (num4 + amplitude * num5));
            Vector3 vector3 = vector2 + num4 * CurveAmplitude * normalized * num;
            return new Vector2(vector3.x, vector3.z);
        }

        protected override float RiverWidthAt(Node node, Vector2 cell)
        {
            float tValue = TValue(node, cell);
            float num = ((node == NodeA) ? widthFrac : (1f - widthFrac));
            float num2 = Mathf.InverseLerp(centrality, 1f - centrality, tValue);
            float num3 = -4f * Mathf.Pow(num2, 2f) + 4f * num2;
            float num4 = 1f - num * num3;
            return Mathf.Max(node.Width / 2f * num4 * (1f + widthNoise.GetValue(cell) * 0.15f), 1.5f);
        }
    }

    internal sealed class DeltaRiverTerrain : GenerationRiverTerrain
    {
        internal DeltaRiverTerrain(int width, int height, IntVec3 center, IReadOnlyList<Node> nodes, ModuleBase bendNoise,
            ModuleBase shallowizer, ModuleBase bankNoise, ModuleBase widthNoise)
            : base(width, height, center, nodes, bendNoise, shallowizer, bankNoise, widthNoise) { }

        protected override float CurveAmplitude => 10f;
        protected override float WidthNoiseFactor(Node node) => 1f;
    }

    internal sealed class HeadwaterRiverTerrain : GenerationRiverTerrain
    {
        private readonly ModuleBase lakeNoise;

        internal HeadwaterRiverTerrain(int width, int height, IntVec3 center, IReadOnlyList<Node> nodes, ModuleBase bendNoise,
            ModuleBase shallowizer, ModuleBase bankNoise, ModuleBase widthNoise, ModuleBase lakeNoise)
            : base(width, height, center, nodes, bendNoise, shallowizer, bankNoise, widthNoise)
        {
            this.lakeNoise = lakeNoise;
        }

        protected override TerrainDef AfterRiverAt(IntVec3 c, TerrainDef current, Terrains terrains)
        {
            float valAt = (float)lakeNoise.GetValue(c.x, 0.0, c.z);
            if (valAt > 0.75f) return terrains.DeepFreshAt(c);
            if (valAt > 0.5f) return terrains.ShallowFreshAt(c);
            if (valAt > 0.45f && !current.IsWater && !current.IsIce) return terrains.LakeshoreAt(c);
            return null;
        }
    }
}
