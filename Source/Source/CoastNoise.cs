using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal static class CoastNoise
    {
        public sealed class Source
        {
            public TileMutatorWorker_Coast worker;
            public ModuleBase noise;
            public string origin;
        }

        private static readonly ConditionalWeakTable<Map, Source> captured = new ConditionalWeakTable<Map, Source>();

        internal static readonly AccessTools.FieldRef<TileMutatorWorker_Coast, ModuleBase> NoiseRef =
            AccessTools.FieldRefAccess<TileMutatorWorker_Coast, ModuleBase>("coastNoise");

        private static readonly Func<TileMutatorWorker_Coast, IntVec3, float> GetNoiseValue =
            AccessTools.MethodDelegate<Func<TileMutatorWorker_Coast, IntVec3, float>>(
                AccessTools.Method(typeof(TileMutatorWorker_Coast), "GetNoiseValue"));

        private static readonly Func<TileMutatorWorker_Coast, IntVec3, Map, TerrainDef> CoastTerrainAt =
            AccessTools.MethodDelegate<Func<TileMutatorWorker_Coast, IntVec3, Map, TerrainDef>>(
                AccessTools.Method(typeof(TileMutatorWorker_Coast), "CoastTerrainAt"));

        private static readonly Func<TileMutatorWorker_Coast, float> MaxForShallowWater =
            AccessTools.MethodDelegate<Func<TileMutatorWorker_Coast, float>>(
                AccessTools.PropertyGetter(typeof(TileMutatorWorker_Coast), "MaxForShallowWater"));

        private static readonly Func<TileMutatorWorker_Coast, float> MaxForDeepWater =
            AccessTools.MethodDelegate<Func<TileMutatorWorker_Coast, float>>(
                AccessTools.PropertyGetter(typeof(TileMutatorWorker_Coast), "MaxForDeepWater"));

        private static readonly Func<TileMutatorWorker_Coast, float> MaxForSand =
            AccessTools.MethodDelegate<Func<TileMutatorWorker_Coast, float>>(
                AccessTools.PropertyGetter(typeof(TileMutatorWorker_Coast), "MaxForSand"));

        public static float ShallowMax(TileMutatorWorker_Coast worker) => MaxForShallowWater(worker);

        public static Func<TerrainDef, bool> SeaPredicate(Map map)
        {
            bool anyOcean = false;
            for (int z = 0; z < map.Size.z && !anyOcean; z += 3)
            {
                for (int x = 0; x < map.Size.x && !anyOcean; x += 3)
                {
                    anyOcean = map.terrainGrid.BaseTerrainAt(new IntVec3(x, 0, z))?.IsOcean ?? false;
                }
            }
            if (anyOcean)
            {
                return t => t != null && t.IsOcean;
            }
            return t => t != null && t.IsWater && !t.IsRiver && t != TerrainDefOf.Marsh;
        }

        public static bool Ignorable(Map map, IntVec3 c)
        {
            TerrainDef t = map.terrainGrid.BaseTerrainAt(c);
            if (t != null && t.IsWater)
            {
                return false;
            }
            Building edifice = c.GetEdifice(map);
            return (edifice != null && edifice.def.mineable) || (t != null && t.categoryType == TerrainDef.TerrainCategoryType.Stone);
        }

        public static TileMutatorWorker_Coast CoastWorkerOf(Map map)
        {
            return map.TileInfo?.Mutators?.Select(m => m.Worker).OfType<TileMutatorWorker_Coast>().FirstOrDefault();
        }

        public static void CaptureAfterGeneration(Map map)
        {
            TileMutatorWorker_Coast worker = CoastWorkerOf(map);
            if (worker == null)
            {
                return;
            }
            ModuleBase noise = NoiseRef(worker);
            if (noise == null)
            {
                return;
            }
            captured.AddOrUpdate(map, new Source { worker = worker, noise = noise, origin = "capture" });
        }

        public static void Store(Map map, Source source) => captured.AddOrUpdate(map, source);

        public static void Forget(Map map) => captured.Remove(map);

        public static Source Captured(Map map)
        {
            return captured.TryGetValue(map, out Source s) ? s : null;
        }

        public static Source Replay(Map map)
        {
            TileMutatorWorker_Coast worker = CoastWorkerOf(map);
            if (worker == null || map.IsPocketMap)
            {
                return null;
            }
            TileMutatorWorker_Coast replayed = new GenerationMutators(new GenerationContext(map)).Workers.OfType<TileMutatorWorker_Coast>().FirstOrDefault();
            ModuleBase noise = replayed == null ? null : NoiseRef(replayed);
            return noise == null ? null : new Source { worker = worker, noise = noise, origin = "replay" };
        }

        public static Source ReplayValidated(Map map, out string report)
        {
            Source rep;
            try
            {
                rep = Replay(map);
            }
            catch (Exception ex)
            {
                report = "replay failed: " + ex.GetType().Name;
                Log.ErrorOnce(HorizonLog.Prefix + "Coast noise replay failed: " + ex, 0x45484352);
                return null;
            }
            if (rep == null)
            {
                report = "replay unavailable";
                return null;
            }
            float iou = WaterIoU(rep, map, 2);
            if (iou >= 0.85f)
            {
                captured.AddOrUpdate(map, rep);
                report = $"replay (iou={iou:F2})";
                return rep;
            }
            report = $"replay rejected (iou={iou:F2})";
            return null;
        }

        public static float WaterIoU(Source s, Map map, int step)
        {
            int tp = 0, fp = 0, fn = 0;
            float max = ShallowMax(s.worker);
            Func<TerrainDef, bool> isSea = SeaPredicate(map);
            using (new Scope(s))
            {
                for (int z = 0; z < map.Size.z; z += step)
                {
                    for (int x = 0; x < map.Size.x; x += step)
                    {
                        var c = new IntVec3(x, 0, z);
                        if (Ignorable(map, c))
                        {
                            continue;
                        }
                        bool predicted = Value(s, c) < max;
                        TerrainDef t = map.terrainGrid.BaseTerrainAt(c);
                        bool actual = isSea(t);
                        if (predicted && actual) tp++;
                        else if (predicted) fp++;
                        else if (actual) fn++;
                    }
                }
            }
            return tp + fp + fn == 0 ? 0f : tp / (float)(tp + fp + fn);
        }

        public readonly struct Scope : IDisposable
        {
            private readonly TileMutatorWorker_Coast worker;
            private readonly ModuleBase previous;

            public Scope(Source source)
            {
                worker = source.worker;
                previous = NoiseRef(worker);
                NoiseRef(worker) = source.noise;
            }

            public void Dispose()
            {
                NoiseRef(worker) = previous;
            }
        }

        public static float Value(Source s, IntVec3 c) => GetNoiseValue(s.worker, c);

        internal static TerrainDef TerrainAt(Source s, IntVec3 c, IntVec3 inside, Map map, TerrainDef previous)
        {
            float v = Value(s, c);
            bool fresh = s.worker is TileMutatorWorker_Lakeshore;
            if (v < MaxForDeepWater(s.worker))
                return fresh ? MapGenUtility.DeepFreshWaterTerrainAt(inside, map) : MapGenUtility.DeepOceanWaterTerrainAt(inside, map);
            if (v < MaxForShallowWater(s.worker))
                return fresh ? MapGenUtility.ShallowFreshWaterTerrainAt(inside, map) : MapGenUtility.ShallowOceanWaterTerrainAt(inside, map);
            if (v < MaxForSand(s.worker) && previous != null && !previous.IsWater && !previous.IsIce)
                return fresh ? MapGenUtility.LakeshoreTerrainAt(inside, map) : MapGenUtility.BeachTerrainAt(inside, map);
            return null;
        }

        public static TerrainDef WorkerTerrainAt(Source s, IntVec3 c, Map map) => CoastTerrainAt(s.worker, c, map);
    }
}
