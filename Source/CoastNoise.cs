using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    public static class CoastNoise
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
            ModuleBase previous = NoiseRef(worker);
            ModuleBase replayed;
            Rand.PushState(Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode()));
            try
            {
                foreach (TileMutatorDef mutator in map.TileInfo.Mutators)
                {
                    TileMutatorWorker w = mutator.Worker;
                    if (w == null)
                    {
                        continue;
                    }
                    w.Init(map);
                    if (w == worker)
                    {
                        break;
                    }
                }
                replayed = NoiseRef(worker);
            }
            finally
            {
                Rand.PopState();
                NoiseRef(worker) = previous;
            }
            return replayed == null ? null : new Source { worker = worker, noise = replayed, origin = "replay" };
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
                Log.WarningOnce(EndlessHorizonMod.LogPrefix + "Coast noise replay failed: " + ex, 0x45484352);
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

        public static TerrainDef WorkerTerrainAt(Source s, IntVec3 c, Map map) => CoastTerrainAt(s.worker, c, map);
    }
}
