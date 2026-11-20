using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    public sealed class CoastSearch
    {
        public const int MaxIterations = 16384;
        private const int SampleCount = 48;

        private static readonly ConditionalWeakTable<Map, CoastSearch> searches = new ConditionalWeakTable<Map, CoastSearch>();
        private static readonly Action<ulong> SetRandState =
            AccessTools.MethodDelegate<Action<ulong>>(AccessTools.PropertySetter(typeof(Rand), "StateCompressed"));

        private readonly Map map;
        private readonly TileMutatorWorker_Coast worker;
        private readonly uint seed;
        private readonly List<IntVec3> samples = new List<IntVec3>();
        private readonly List<bool> sampleWater = new List<bool>();
        private int k;
        private readonly CoastNoise.Source probe;
        private readonly float shallowMax;
        public bool Done;
        public CoastNoise.Source Result;
        public string Report = "searching";
        public long SpentMs;

        public static void Forget(Map map) => searches.Remove(map);

        public static CoastSearch For(Map map, TileMutatorWorker_Coast worker)
        {
            if (!searches.TryGetValue(map, out CoastSearch s))
            {
                s = new CoastSearch(map, worker);
                searches.AddOrUpdate(map, s);
            }
            return s;
        }

        private CoastSearch(Map map, TileMutatorWorker_Coast worker)
        {
            this.map = map;
            this.worker = worker;
            seed = (uint)Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode());
            probe = new CoastNoise.Source { worker = worker };
            shallowMax = CoastNoise.ShallowMax(worker);
            isSea = CoastNoise.SeaPredicate(map);
            PickShoreSamples();
            if (samples.Count < 12)
            {
                Done = true;
                Report = "no shoreline to match";
            }
        }

        private Func<TerrainDef, bool> isSea;

        private bool IsSea(TerrainDef t) => isSea(t);

        private void PickShoreSamples()
        {
            var shore = new List<IntVec3>();
            for (int z = 3; z < map.Size.z - 3; z += 2)
            {
                for (int x = 3; x < map.Size.x - 3; x += 2)
                {
                    var c = new IntVec3(x, 0, z);
                    if (CoastNoise.Ignorable(map, c))
                    {
                        continue;
                    }
                    bool water = IsSea(map.terrainGrid.BaseTerrainAt(c));
                    bool near = false;
                    for (int dz = -3; dz <= 3 && !near; dz += 3)
                    {
                        for (int dx = -3; dx <= 3 && !near; dx += 3)
                        {
                            var n = new IntVec3(x + dx, 0, z + dz);
                            near = !CoastNoise.Ignorable(map, n) && IsSea(map.terrainGrid.BaseTerrainAt(n)) != water;
                        }
                    }
                    if (near)
                    {
                        shore.Add(c);
                    }
                }
            }
            var rng = new System.Random((int)seed);
            for (int i = 0; i < SampleCount && shore.Count > 0; i++)
            {
                int j = rng.Next(shore.Count);
                samples.Add(shore[j]);
                sampleWater.Add(IsSea(map.terrainGrid.BaseTerrainAt(shore[j])));
                shore.RemoveAt(j);
            }
        }

        public bool Step(float budgetMs)
        {
            if (Done)
            {
                return false;
            }
            var sw = Stopwatch.StartNew();
            ModuleBase previous = CoastNoise.NoiseRef(worker);
            Rand.PushState();
            try
            {
                while (k < MaxIterations && sw.Elapsed.TotalMilliseconds < budgetMs)
                {
                    SetRandState(seed | ((ulong)(uint)k << 32));
                    worker.Init(map);
                    ModuleBase noise = CoastNoise.NoiseRef(worker);
                    int hits = 0;
                    for (int i = 0; i < samples.Count; i++)
                    {
                        if ((CoastNoise.Value(probe, samples[i]) < shallowMax) == sampleWater[i])
                        {
                            hits++;
                        }
                    }
                    if (hits >= samples.Count * 0.9f)
                    {
                        var candidate = new CoastNoise.Source { worker = worker, noise = noise, origin = $"recovered k={k}" };
                        float iou = CoastNoise.WaterIoU(candidate, map, 2);
                        if (iou >= 0.9f)
                        {
                            candidate.origin = $"recovered (k={k}, iou={iou:F2})";
                            Result = candidate;
                            Done = true;
                            Report = candidate.origin;
                            break;
                        }
                    }
                    k++;
                }
            }
            finally
            {
                Rand.PopState();
                CoastNoise.NoiseRef(worker) = previous;
            }
            SpentMs += sw.ElapsedMilliseconds;
            if (!Done && k >= MaxIterations)
            {
                Done = true;
                Report = $"not recovered after {MaxIterations} tries";
            }
            if (Done)
            {
                Log.Message($"{EndlessHorizonMod.LogPrefix}coast search: {Report}, {SpentMs} ms total.");
                if (Result != null)
                {
                    CoastNoise.Store(map, Result);
                }
                return true;
            }
            return false;
        }
    }

    public sealed class CoastFallback
    {
        private readonly ModuleBase noise;
        public readonly string Report;

        private CoastFallback(ModuleBase noise, string report)
        {
            this.noise = noise;
            Report = report;
        }

        public static CoastFallback TryCreate(Map map)
        {
            float? angle = Find.World.CoastAngleAt(map.Tile, BiomeDefOf.Ocean);
            if (!angle.HasValue)
            {
                return null;
            }
            float bestOffset = 0.15f, bestIou = -1f;
            Func<TerrainDef, bool> isSea = CoastNoise.SeaPredicate(map);
            for (float offset = 0f; offset <= 0.45f; offset += 0.025f)
            {
                ModuleBase f = MapNoiseUtility.FalloffAtAngle(angle.Value, offset, map);
                int tp = 0, fp = 0, fn = 0;
                for (int z = 0; z < map.Size.z; z += 5)
                {
                    for (int x = 0; x < map.Size.x; x += 5)
                    {
                        if (CoastNoise.Ignorable(map, new IntVec3(x, 0, z)))
                        {
                            continue;
                        }
                        bool predicted = f.GetValue(x, 0, z) < 0.5;
                        TerrainDef t = map.terrainGrid.BaseTerrainAt(new IntVec3(x, 0, z));
                        bool actual = isSea(t);
                        if (predicted && actual) tp++;
                        else if (predicted) fp++;
                        else if (actual) fn++;
                    }
                }
                float iou = tp + fp + fn == 0 ? 0f : tp / (float)(tp + fp + fn);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    bestOffset = offset;
                }
            }
            int seed = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode()) ^ 0x434F4153;
            ModuleBase n = MapNoiseUtility.FalloffAtAngle(angle.Value, bestOffset, map);
            n = MapNoiseUtility.AddDisplacementNoise(n, 0.006f, 30f, 2, seed);
            n = MapNoiseUtility.AddDisplacementNoise(n, 0.015f, 25f, 4, seed + 2);
            return new CoastFallback(n, $"fallback (angle={angle.Value:F0}, offset={bestOffset:F3}, fitIou={bestIou:F2})");
        }

        public float SeaScore(IntVec3 c, float distance, bool mirroredIsSea)
        {
            float fit = 0.5f - (float)noise.GetValue(c.x, 0, c.z);
            float mirror = mirroredIsSea ? 0.12f : -0.12f;
            float t = Mathf.Clamp01(distance / 14f);
            t = t * t * (3f - 2f * t);
            return Mathf.Lerp(mirror, fit, t);
        }

        public bool PredictedSea(IntVec3 c) => noise.GetValue(c.x, 0, c.z) < 0.5;
    }
}
