using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal sealed class GenerationCaves
    {
        internal readonly struct Exit
        {
            internal readonly IntVec3 Cell, Next;
            internal readonly float Direction, Width;
            internal Exit(IntVec3 cell, IntVec3 next, float direction, float width) { Cell = cell; Next = next; Direction = direction; Width = width; }
        }

        private sealed class Stage
        {
            internal TileMutatorWorker_Caves Worker;
            internal int Transforms;
        }

        private const int RowsPerUnit = 16;
        private static Action<List<IntVec3>, Map, MapGenCavesUtility.CaveGenParms> removeSmall;
        private readonly GenerationContext context;
        private readonly GenerationMutators mutators;
        private readonly List<Stage> stages = new();
        private readonly int w, h;
        private readonly Dictionary<TileMutatorWorker, (ModuleBase water, ModuleBase gravel)> terrainNoise = new();
        internal float[] Grid { get; private set; }
        internal readonly List<Exit> Exits = new();
        internal double LongestUnitMs;
        private float[] elevation, finalElevation;
        private bool[] member;
        private Map shadow;

        internal GenerationCaves(GenerationContext context, GenerationMutators mutators)
        {
            this.context = context; this.mutators = mutators;
            w = context.Width; h = context.Height;
        }

        internal static bool Replays(TileMutatorWorker worker)
        {
            Type type = worker.GetType();
            if (type != typeof(TileMutatorWorker_Caves) && type != typeof(TileMutatorWorker_Cavern)
                && type != typeof(TileMutatorWorker_LavaCaves) && type != typeof(TileMutatorWorker_CaveLakes)) return false;
            if (Field(worker, "directionNoise") == null) return false;
            return type != typeof(TileMutatorWorker_Cavern) || Field(worker, "openingNoise") != null && Field(worker, "holesNoise") != null;
        }

        internal static bool ReplaysTerrain(TileMutatorWorker worker) =>
            (worker.GetType() == typeof(TileMutatorWorker_Caves) || worker.GetType() == typeof(TileMutatorWorker_Cavern)) && Replays(worker);

        private static ModuleBase Field(object worker, string name) => (ModuleBase)AccessTools.Field(worker.GetType(), name)?.GetValue(worker);
        private static float Property(object worker, string name) => (float)AccessTools.Property(worker.GetType(), name).GetValue(worker);

        internal void Add(TileMutatorWorker_Caves worker, int transforms) => stages.Add(new Stage { Worker = worker, Transforms = transforms });

        internal bool Pending(TileMutatorWorker worker) => stages.Any(s => s.Worker == worker);

        private void ListDropped()
        {
            foreach (Stage stage in stages) context.Unsupported.Add("terrain: " + stage.Worker.GetType().FullName);
        }

        internal IEnumerable<bool> Run(GenReconstruction generator, GenerationSnapshot snapshot)
        {
            finalElevation = null; Grid = null; Exits.Clear();
            if (context.ExternalBiome != null)
            {
                context.Unsupported.Add("caves: vanilla Caves Worker on a GL map");
                ListDropped();
                mutators.CavesFailed();
                yield break;
            }
            if (context.StepIndex(typeof(GenStep_MutatorPostElevationFertility)) < 0) yield break;
            using IEnumerator<bool> units = Build(generator, snapshot).GetEnumerator();
            while (true)
            {
                bool more;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try { more = units.MoveNext(); }
                catch (Exception ex)
                {
                    Grid = null; Exits.Clear(); elevation = null; member = null; shadow = null;
                    for (int z = 0; z < h; z++) for (int x = 0; x < w; x++) snapshot.Caves[snapshot.Index(x, z)] = 0f;
                    mutators.CavesFailed();
                    context.Unsupported.Add("caves: replay failed (" + ex.GetType().Name + ")");
                    ListDropped();
                    Log.Error(HorizonLog.Prefix + "Replaying the cave tunnels failed; caves are predicted as rock. " + ex);
                    yield break;
                }
                LongestUnitMs = Math.Max(LongestUnitMs, clock.Elapsed.TotalMilliseconds);
                if (!more) break;
                yield return false;
            }
        }

        private IEnumerable<bool> Build(GenReconstruction generator, GenerationSnapshot snapshot)
        {
            GenerationRandom random = mutators.ElevationRandom;
            var grid = new float[w * h];
            Grid = grid;
            shadow = Shadow(w, h);
            member = new bool[w * h];
            foreach (Stage stage in stages)
            {
                elevation = new float[w * h];
                for (int z0 = 0; z0 < h; z0 += RowsPerUnit)
                {
                    for (int z = z0; z < Math.Min(h, z0 + RowsPerUnit); z++)
                        for (int x = 0; x < w; x++)
                        {
                            float raw = generator.Elevation == null ? 0f : Mathf.Min((float)generator.Elevation.GetValue(x, 0, z), generator.WaterCap);
                            elevation[z * w + x] = mutators.ElevationBefore(new IntVec3(x, 0, z), raw, stage.Transforms);
                        }
                    yield return false;
                }
                IEnumerable<bool> cut = stage.Worker is TileMutatorWorker_Cavern ? Cavern(stage.Worker, random) : Caves(stage.Worker, random);
                foreach (bool unit in cut) yield return unit;
            }
            for (int z = 0; z < h; z++) for (int x = 0; x < w; x++) snapshot.Caves[snapshot.Index(x, z)] = grid[z * w + x];
            finalElevation = snapshot.Elevation;
            this.snapshot = snapshot;
            elevation = null; member = null; shadow = null;
            foreach (Stage stage in stages) context.Applied.Add(stage.Worker.GetType().Name);
        }
        private GenerationSnapshot snapshot;

        private static Map Shadow(int width, int height)
        {
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.info = new MapInfo { Size = new IntVec3(width, 1, height) };
            map.cellIndices = new CellIndices(map);
            map.floodFiller = new FloodFiller(map);
            return map;
        }

        private bool Inside(IntVec3 c) => (uint)c.x < (uint)w && (uint)c.z < (uint)h;
        private bool IsRock(IntVec3 c) => Inside(c) && elevation[c.z * w + c.x] > 0.7f;
        private bool Contains(IntVec3 c) => Inside(c) && member[c.z * w + c.x];
        private float CaveAt(IntVec3 c) => Grid[c.z * w + c.x];

        private void Members(List<IntVec3> group)
        {
            Array.Clear(member);
            foreach (IntVec3 c in group) member[c.z * w + c.x] = true;
        }

        private static MapGenCavesUtility.CaveGenParms Parms(TileMutatorWorker_Caves worker)
        {
            MapGenCavesUtility.CaveGenParms parms = MapGenCavesUtility.CaveGenParms.Default;
            parms.widthOffsetPerCell = Property(worker, "WidthOffsetPerCell");
            parms.minTunnelWidth = Property(worker, "MinTunnelWidth");
            parms.branchChance = Property(worker, "BranchChance");
            parms.allowBranchingAfterThisManyCells = 10;
            return parms;
        }

        private IEnumerable<bool> Caves(TileMutatorWorker_Caves worker, GenerationRandom random)
        {
            removeSmall ??= AccessTools.MethodDelegate<Action<List<IntVec3>, Map, MapGenCavesUtility.CaveGenParms>>(
                AccessTools.Method(typeof(MapGenCavesUtility), "RemoveSmallDisconnectedSubGroups"));
            MapGenCavesUtility.CaveGenParms parms = Parms(worker);
            ModuleBase direction = Field(worker, "directionNoise");
            var visited = new bool[w * h];
            var group = new List<IntVec3>();
            Predicate<IntVec3> rock = IsRock;
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    if (visited[i] || !(elevation[i] > 0.7f)) continue;
                    group.Clear();
                    shadow.floodFiller.FloodFill(new IntVec3(x, 0, z), rock, delegate(IntVec3 c)
                    {
                        visited[c.z * w + c.x] = true;
                        group.Add(c);
                    });
                    yield return false;
                    GenMorphology.Open(group, 6, shadow);
                    yield return false;
                    removeSmall(group, shadow, parms);
                    if (group.Count < parms.minRocksToGenerateAnyTunnel) continue;
                    Members(group);
                    yield return false;
                    foreach (bool unit in OpenTunnels(group, direction, parms, random)) yield return unit;
                    foreach (bool unit in ClosedTunnels(group, direction, parms, random)) yield return unit;
                }
            }
        }

        private IEnumerable<bool> OpenTunnels(List<IntVec3> group, ModuleBase direction, MapGenCavesUtility.CaveGenParms parms, GenerationRandom random)
        {
            int a;
            using (random.Enter())
            {
                a = GenMath.RoundRandom((float)group.Count * Rand.Range(0.9f, 1.1f) * parms.openTunnelsPer10k / 10000f);
                a = Mathf.Min(a, parms.maxOpenTunnelsPerRockGroup);
                if (a > 0) a = Rand.RangeInclusive(1, a);
            }
            float num = parms.tunnelsWidthPerRockCount.Evaluate(group.Count);
            for (int i = 0; i < a; i++)
            {
                var pick = new Pick();
                for (int j = 0; j < 10; j++)
                {
                    using (random.Enter()) TryEdgeStart(group, ref pick);
                    yield return false;
                }
                using (random.Enter())
                {
                    float width = Rand.Range(num * 0.8f, num);
                    Dig(pick.Start, pick.Dir, width, group, false, direction, parms, null);
                }
                yield return false;
            }
        }

        private struct Pick
        {
            internal IntVec3 Start;
            internal float Cave, Dir, Dist;
            internal bool Set;
        }

        private void TryEdgeStart(List<IntVec3> group, ref Pick pick)
        {
            IntVec3 intVec = FindRandomEdgeCellForTunnel(group);
            float distToCave = GetDistToCave(intVec, 40f, false);
            float num4 = FindBestInitialDir(intVec, out float dist);
            if (!pick.Set || !pick.Start.IsValid || distToCave > pick.Cave || (Mathf.Approximately(distToCave, pick.Cave) && dist > pick.Dist))
            {
                pick.Start = intVec; pick.Cave = distToCave; pick.Dir = num4; pick.Dist = dist; pick.Set = true;
            }
        }

        private IEnumerable<bool> ClosedTunnels(List<IntVec3> group, ModuleBase direction, MapGenCavesUtility.CaveGenParms parms, GenerationRandom random)
        {
            int a;
            using (random.Enter())
            {
                a = GenMath.RoundRandom((float)group.Count * Rand.Range(0.9f, 1.1f) * parms.closedTunnelsPer10k / 10000f);
                a = Mathf.Min(a, parms.maxClosedTunnelsPerRockGroup);
                if (a > 0) a = Rand.RangeInclusive(0, a);
            }
            float num = parms.tunnelsWidthPerRockCount.Evaluate(group.Count);
            for (int i = 0; i < a; i++)
            {
                using (random.Enter())
                {
                    IntVec3 start = IntVec3.Invalid;
                    float num2 = -1f;
                    for (int j = 0; j < 7; j++)
                    {
                        IntVec3 intVec = group.RandomElement();
                        float distToCave = GetDistToCave(intVec, 30f, true);
                        if (!start.IsValid || distToCave > num2)
                        {
                            start = intVec;
                            num2 = distToCave;
                        }
                    }
                    float width = Rand.Range(num * 0.8f, num);
                    Dig(start, Rand.Range(0f, 360f), width, group, true, direction, parms, null);
                }
                yield return false;
            }
        }

        private IEnumerable<bool> Cavern(TileMutatorWorker_Caves worker, GenerationRandom random)
        {
            ModuleBase direction = Field(worker, "directionNoise"), holes = Field(worker, "holesNoise");
            List<IntVec3> list = FindLargestContiguousRockFormation();
            Members(list);
            yield return false;
            int count;
            using (random.Enter()) count = ((IntRange)AccessTools.Field(typeof(TileMutatorWorker_Cavern), "NumTunnelsRange").GetValue(null)).RandomInRange;
            for (int i = 0; i < count; i++)
            {
                var pick = new Pick();
                for (int j = 0; j < 10; j++)
                {
                    using (random.Enter()) TryEdgeStart(list, ref pick);
                    yield return false;
                }
                using (random.Enter())
                {
                    float width = Rand.Range(5.6f, 7f);
                    MapGenCavesUtility.CaveGenParms parms = Parms(worker);
                    parms.widthNoiseAmplitude = 2f;
                    Dig(pick.Start, pick.Dir, width, list, false, direction, parms, null);
                }
                yield return false;
            }
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    if (holes.GetValue(new IntVec3(x, 0, z)) > 0.7f) Grid[z * w + x] = 1f;
        }

        private List<IntVec3> FindLargestContiguousRockFormation()
        {
            var checkedCells = new bool[w * h];
            var largest = new List<IntVec3>();
            var current = new List<IntVec3>();
            Predicate<IntVec3> rock = IsRock;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (!IsRock(cell) || checkedCells[z * w + x]) continue;
                    current.Clear();
                    shadow.floodFiller.FloodFill(cell, rock, delegate(IntVec3 c)
                    {
                        current.Add(c);
                        checkedCells[c.z * w + c.x] = true;
                    });
                    if (current.Count > largest.Count) largest = new List<IntVec3>(current);
                }
            return largest;
        }

        private readonly List<IntVec3> tmpCells = new();
        private IntVec3 FindRandomEdgeCellForTunnel(List<IntVec3> group)
        {
            IntVec3[] cardinalDirections = GenAdj.CardinalDirections;
            tmpCells.Clear();
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].DistanceToEdge(shadow) < 3 || CaveAt(group[i]) > 0f) continue;
                for (int j = 0; j < 4; j++)
                {
                    IntVec3 item = group[i] + cardinalDirections[j];
                    if (!Contains(item))
                    {
                        tmpCells.Add(group[i]);
                        break;
                    }
                }
            }
            if (tmpCells.Count == 0) return group.RandomElement();
            return tmpCells.RandomElement();
        }

        private float FindBestInitialDir(IntVec3 start, out float dist)
        {
            float num = GetDistToNonRock(start, IntVec3.East, 40);
            float num2 = GetDistToNonRock(start, IntVec3.West, 40);
            float num3 = GetDistToNonRock(start, IntVec3.South, 40);
            float num4 = GetDistToNonRock(start, IntVec3.North, 40);
            float num5 = GetDistToNonRock(start, IntVec3.NorthWest, 40);
            float num6 = GetDistToNonRock(start, IntVec3.NorthEast, 40);
            float num7 = GetDistToNonRock(start, IntVec3.SouthWest, 40);
            float num8 = GetDistToNonRock(start, IntVec3.SouthEast, 40);
            dist = Mathf.Max(num, num2, num3, num4, num5, num6, num7, num8);
            return GenMath.MaxByRandomIfEqual(0f, num + num8 / 2f + num6 / 2f, 45f, num8 + num3 / 2f + num / 2f, 90f, num3 + num8 / 2f + num7 / 2f, 135f, num7 + num3 / 2f + num2 / 2f, 180f, num2 + num7 / 2f + num5 / 2f, 225f, num5 + num4 / 2f + num2 / 2f, 270f, num4 + num6 / 2f + num5 / 2f, 315f, num6 + num4 / 2f + num / 2f);
        }

        private void Dig(IntVec3 start, float dir, float width, List<IntVec3> group, bool closed, ModuleBase directionNoise,
            MapGenCavesUtility.CaveGenParms parms, HashSet<IntVec3> visited)
        {
            Vector3 vect = start.ToVector3Shifted();
            IntVec3 intVec = start;
            float num = 0f;
            bool flag = false;
            bool flag2 = false;
            ModuleBase moduleBase = new Perlin(parms.widthNoiseFrequency, 2.0, 0.5, 4, Rand.Int, QualityMode.Medium);
            visited ??= new HashSet<IntVec3>();
            int num2 = 0;
            while (true)
            {
                if (closed)
                {
                    int num3 = GenRadial.NumCellsInRadius(width / 2f + 1.5f);
                    for (int i = 0; i < num3; i++)
                    {
                        IntVec3 intVec2 = intVec + GenRadial.RadialPattern[i];
                        if (!visited.Contains(intVec2) && (!Contains(intVec2) || CaveAt(intVec2) > 0f)) return;
                    }
                }
                if (num2 >= parms.allowBranchingAfterThisManyCells && width > parms.minTunnelWidth + parms.branchedTunnelWidthOffset.max)
                {
                    if (!flag && Rand.Chance(parms.branchChance))
                    {
                        DigInBestDirection(intVec, dir, new FloatRange(40f, 90f), width - parms.branchedTunnelWidthOffset.RandomInRange, group, closed, directionNoise, parms, visited);
                        flag = true;
                    }
                    if (!flag2 && Rand.Chance(parms.branchChance))
                    {
                        DigInBestDirection(intVec, dir, new FloatRange(-90f, -40f), width - parms.branchedTunnelWidthOffset.RandomInRange, group, closed, directionNoise, parms, visited);
                        flag2 = true;
                    }
                }
                float tunnelWidth = Mathf.Max(width + moduleBase.GetValue(intVec) * parms.widthNoiseAmplitude, 1f);
                SetCaveAround(intVec, tunnelWidth, visited, out bool hitAnotherTunnel);
                if (hitAnotherTunnel) break;
                while (vect.ToIntVec3() == intVec)
                {
                    vect += Vector3Utility.FromAngleFlat(dir) * 0.5f;
                    num += 0.5f;
                }
                if (Contains(vect.ToIntVec3()))
                {
                    IntVec3 intVec3 = new IntVec3(intVec.x, 0, vect.ToIntVec3().z);
                    if (IsRock(intVec3))
                    {
                        Grid[intVec3.z * w + intVec3.x] = Mathf.Max(CaveAt(intVec3), width);
                        visited.Add(intVec3);
                    }
                    intVec = vect.ToIntVec3();
                    dir += (float)directionNoise.GetValue(num * 60f, (float)start.x * 200f, (float)start.z * 200f) * parms.directionChangeSpeed;
                    width -= parms.widthOffsetPerCell;
                    if (!(width < parms.minTunnelWidth))
                    {
                        num2++;
                        continue;
                    }
                    break;
                }
                if (!closed && !Inside(vect.ToIntVec3())) Exits.Add(new Exit(intVec, vect.ToIntVec3(), dir, width));
                break;
            }
        }

        private void DigInBestDirection(IntVec3 curIntVec, float curDir, FloatRange dirOffset, float width, List<IntVec3> group, bool closed,
            ModuleBase directionNoise, MapGenCavesUtility.CaveGenParms parms, HashSet<IntVec3> visited)
        {
            int num = -1;
            float dir = -1f;
            for (int i = 0; i < 6; i++)
            {
                float num2 = curDir + dirOffset.RandomInRange;
                int distToNonRock = GetDistToNonRock(curIntVec, num2, 50);
                if (distToNonRock > num)
                {
                    num = distToNonRock;
                    dir = num2;
                }
            }
            if (num >= 18) Dig(curIntVec, dir, width, group, closed, directionNoise, parms, visited);
        }

        private void SetCaveAround(IntVec3 around, float tunnelWidth, HashSet<IntVec3> visited, out bool hitAnotherTunnel)
        {
            hitAnotherTunnel = false;
            int num = GenRadial.NumCellsInRadius(tunnelWidth / 2f);
            for (int i = 0; i < num; i++)
            {
                IntVec3 intVec = around + GenRadial.RadialPattern[i];
                if (IsRock(intVec))
                {
                    if (CaveAt(intVec) > 0f && !visited.Contains(intVec)) hitAnotherTunnel = true;
                    Grid[intVec.z * w + intVec.x] = Mathf.Max(CaveAt(intVec), tunnelWidth);
                    visited.Add(intVec);
                }
            }
        }

        private int GetDistToNonRock(IntVec3 from, IntVec3 offset, int maxDist)
        {
            for (int i = 0; i <= maxDist; i++)
                if (!Contains(from + offset * i)) return i;
            return maxDist;
        }

        private int GetDistToNonRock(IntVec3 from, float dir, int maxDist)
        {
            Vector3 vector = Vector3Utility.FromAngleFlat(dir);
            for (int i = 0; i <= maxDist; i++)
            {
                IntVec3 item = (from.ToVector3Shifted() + vector * i).ToIntVec3();
                if (!Contains(item)) return i;
            }
            return maxDist;
        }

        private float GetDistToCave(IntVec3 cell, float maxDist, bool treatOpenSpaceAsCave)
        {
            int num = GenRadial.NumCellsInRadius(maxDist);
            IntVec3[] radialPattern = GenRadial.RadialPattern;
            for (int i = 0; i < num; i++)
            {
                IntVec3 intVec = cell + radialPattern[i];
                if ((treatOpenSpaceAsCave && !Contains(intVec)) || (Inside(intVec) && CaveAt(intVec) > 0f)) return cell.DistanceTo(intVec);
            }
            return maxDist;
        }

        internal TerrainDef TerrainAt(TileMutatorWorker_Caves worker, IntVec3 c, TerrainDef value)
        {
            if (!terrainNoise.TryGetValue(worker, out var noise))
            {
                if (!context.InitializingPatches) return value;
                ModuleBase water = new Perlin(0.07999999821186066, 2.0, 0.5, 6, Rand.Int, QualityMode.Medium);
                ModuleBase gravel = new Perlin(0.1599999964237213, 2.0, 0.5, 6, Rand.Int, QualityMode.Medium);
                terrainNoise.Add(worker, noise = (water, gravel));
            }
            if (value == null || Grid == null || finalElevation == null || !Inside(c)) return value;
            if (Grid[c.z * w + c.x] <= 0f || finalElevation[snapshot.Index(c.x, c.z)] <= 0f || value.IsRiver) return value;
            float num = (float)noise.water.GetValue(c.x, 0.0, c.z);
            float num2 = (float)noise.gravel.GetValue(c.x, 0.0, c.z);
            if (num > 0.93f) return context.ShallowFreshAt(c);
            if (num2 > 0.55f) return TerrainDefOf.Gravel;
            return value;
        }
    }
}
