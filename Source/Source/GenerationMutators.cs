using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal sealed class GenerationMutators
    {
        [ThreadStatic] private static bool initializing;
        [ThreadStatic] private static IntVec3 replayedLakeCenter;
        internal static bool Initializing => initializing;
        private readonly List<TileMutatorWorker_River> pendingRivers = new();
        internal bool RiverReplayed { get; private set; }
        internal string RiverGate;
        private readonly GenerationContext context;
        private readonly List<TileMutatorWorker> workers = new();
        private readonly List<Func<IntVec3, float, float>> elevation = new();
        internal GenerationRandom ElevationRandom;
        internal bool OwnsCoast { get; private set; }
        internal bool TerrainComplete { get; private set; } = true;
        internal bool UnresolvedWater { get; private set; }
        internal void MarkIncomplete() => TerrainComplete = false;
        internal Func<int, int, ThingDef> RockAt;
        internal IReadOnlyList<TileMutatorWorker> Workers => workers;
        private static readonly MethodInfo clone = AccessTools.Method(typeof(object), "MemberwiseClone");
        private static readonly HashSet<string> safeInitializers = new(StringComparer.Ordinal)
        {
            "TileMutatorWorker_Coast", "TileMutatorWorker_Archipelago", "TileMutatorWorker_CoastalIsland",
            "TileMutatorWorker_CoastalAtoll", "TileMutatorWorker_Bay", "TileMutatorWorker_Cove",
            "TileMutatorWorker_Peninsula", "TileMutatorWorker_Fjord", "TileMutatorWorker_Lakeshore",
            "TileMutatorWorker_Lake", "TileMutatorWorker_LakeWithIsland", "TileMutatorWorker_LakeWithIslands",
            "TileMutatorWorker_DryLake", "TileMutatorWorker_ToxicLake", "TileMutatorWorker_Pond",
            "TileMutatorWorker_Basin", "TileMutatorWorker_Valley", "TileMutatorWorker_Plateau",
            "TileMutatorWorker_River", "TileMutatorWorker_Oasis", "TileMutatorWorker_Wetland", "TileMutatorWorker_Dunes", "TileMutatorWorker_Cliffs"
        };
        private static readonly HashSet<string> streamOnlyInitializers = new(StringComparer.Ordinal)
        {
            "TileMutatorWorker_Caves", "TileMutatorWorker_Cavern", "TileMutatorWorker_CaveLakes", "TileMutatorWorker_IceCaves",
            "TileMutatorWorker_LavaCaves", "TileMutatorWorker_Chasm", "TileMutatorWorker_Crevasse", "TileMutatorWorker_Hollow",
            "TileMutatorWorker_HotSprings", "TileMutatorWorker_IceDunes", "TileMutatorWorker_LavaFlow", "TileMutatorWorker_LavaLake",
            "TileMutatorWorker_LavaCrater", "TileMutatorWorker_Iceberg", "TileMutatorWorker_Headwater",
            "TileMutatorWorker_RiverConfluence", "TileMutatorWorker_RiverDelta", "TileMutatorWorker_RiverIsland",
            "TileMutatorWorker_UndergroundCave"
        };
        private static readonly Dictionary<string, string> waterReasons = new(StringComparer.Ordinal)
        {
            ["TileMutatorWorker_LavaFlow"] = "its pond patch maker reads the map's fertility grid and keeps per-map state",
            ["TileMutatorWorker_Iceberg"] = "a Coast subclass whose coast noise is reshaped (SmoothMin) and which spawns SolidIce",
            ["TileMutatorWorker_LavaLake"] = "a flood fill over the map's islands decides the lava, not the cell",
            ["TileMutatorWorker_LavaCrater"] = "a flood fill over the map's islands decides the lava, not the cell",
        };
        internal static bool AuditedInit(Type worker) => worker == typeof(TileMutatorWorker_MixedBiome)
            || safeInitializers.Contains(worker.Name) || streamOnlyInitializers.Contains(worker.Name);

        internal GenerationMutators(GenerationContext context)
        {
            this.context = context;
            Rand.PushState(context.Seed);
            bool previousInitializing=initializing;
            IntVec3 previousLakeCenter=replayedLakeCenter;
            replayedLakeCenter=IntVec3.Invalid;
            initializing = true;
            bool lakeCenterSet=false;
            try
            {
                context.SkipComponentDraws();
                foreach (TileMutatorDef def in context.Map.TileInfo.Mutators)
                {
                    TileMutatorWorker original = def.Worker;
                    if (original == null) continue;
                    var worker = (TileMutatorWorker)clone.Invoke(original, null);
                    if (worker.GetType().Assembly != typeof(TileMutatorWorker).Assembly)
                    {
                        if (worker.GetType().FullName == "GeologicalLandforms.TileMutatorWorker_Landform")
                        {
                            geologicalAt ??= workers.Count;
                            geologicalElevationAt ??= elevationWorkers.Count;
                        }
                        else
                        {
                            context.Unsupported.Add("worker: " + worker.GetType().FullName);
                            TerrainNotReplayed(worker.GetType());
                            if (AccessTools.Method(worker.GetType(),"Init",new[] { typeof(Map) }).DeclaringType != typeof(TileMutatorWorker))
                                context.Unsupported.Add("Init random stream after: " + worker.GetType().FullName);
                        }
                        continue;
                    }
                    workers.Add(worker);
                    elevationWorkers.Add(worker);
                    if (worker is TileMutatorWorker_MixedBiome mixed) context.InitMixed(mixed);
                    else
                    {
                        MethodInfo init = AccessTools.Method(worker.GetType(), "Init", new[] { typeof(Map) });
                        if (init.DeclaringType == typeof(TileMutatorWorker)) continue;
                        string name = worker.GetType().Name;
                        if (safeInitializers.Contains(name)) InitInOrder(worker, ref lakeCenterSet);
                        else
                        {
                            bool initialised = false;
                            if (streamOnlyInitializers.Contains(name)) initialised = InitForStream(worker, ref lakeCenterSet);
                            else context.Unsupported.Add("Init random stream after: " + worker.GetType().FullName);
                            if (initialised && GenerationCaves.Replays(worker)) continue;
                            if (initialised && GenerationRiverTerrain.Reproducible(worker.GetType())) continue;
                            if (initialised && worker is TileMutatorWorker_HotSprings && ElevationOnly(worker)) continue;
                            if (waterReasons.TryGetValue(worker.GetType().Name, out string why))
                                context.Unsupported.Add("water: " + worker.GetType().Name.Replace("TileMutatorWorker_", "") + ": " + why);
                            workers.Remove(worker);
                            if (initialised && ElevationOnly(worker))
                                context.Unsupported.Add("terrain: " + worker.GetType().FullName);
                            else
                            {
                                elevationWorkers.Remove(worker);
                                context.Unsupported.Add("Init: " + worker.GetType().FullName);
                            }
                            TerrainNotReplayed(worker.GetType());
                        }
                    }
                }
            }
            finally { initializing = previousInitializing; replayedLakeCenter = previousLakeCenter; Rand.PopState(); }
            ElevationRandom = new GenerationRandom(context.SeedFor(typeof(GenStep_MutatorPostElevationFertility),1239847543));
            BuildElevation();
            foreach (TileMutatorWorker worker in workers)
            {
                MethodInfo post = AccessTools.Method(worker.GetType(), "GeneratePostTerrain");
                bool supported = worker is TileMutatorWorker_Coast || worker is TileMutatorWorker_Lake
                    || worker is TileMutatorWorker_Dunes || worker is TileMutatorWorker_Wetland || worker is TileMutatorWorker_Patches
                    || worker is TileMutatorWorker_HotSprings || GenerationCaves.ReplaysTerrain(worker);
                if (worker is TileMutatorWorker_Dunes || worker is TileMutatorWorker_Wetland || worker is TileMutatorWorker_Patches
                    || worker is TileMutatorWorker_HotSprings)
                    if (!context.Applied.Contains(worker.GetType().Name)) context.Applied.Add(worker.GetType().Name);
                if (worker is TileMutatorWorker_River && GenerationRiverTerrain.Reproducible(worker.GetType()))
                {
                    pendingRivers.Add((TileMutatorWorker_River)worker);
                    continue;
                }
                if (!supported && post.DeclaringType != typeof(TileMutatorWorker))
                {
                    TerrainNotReplayed(worker.GetType());
                    context.Unsupported.Add("terrain: " + worker.GetType().FullName);
                    if (worker is TileMutatorWorker_Caves) context.Unsupported.Add("PostTerrain random stream after: " + worker.GetType().FullName);
                }
            }
            if (workers.Count(w => w is TileMutatorWorker_River) > 1) SettleRivers("more than one River Worker shares one river graph");
        }

        private static bool ElevationOnly(TileMutatorWorker worker) =>
            worker is TileMutatorWorker_HotSprings && Noise(worker, "springNoise") != null
            || worker is TileMutatorWorker_LavaFlow && Noise(worker, "rockNoise") != null;

        internal void CavesFailed()
        {
            TerrainComplete = false;
            UnresolvedWater = true;
        }

        private void InitInOrder(TileMutatorWorker worker, ref bool lakeCenterSet)
        {
            Map map = context.Map;
            if (worker is TileMutatorWorker_River river && GenerationRiverTerrain.Reproducible(worker.GetType()))
            {
                InitRiver(river, map, lakeCenterSet && replayedLakeCenter.IsValid ? replayedLakeCenter : (IntVec3?)null,
                    ModsConfig.OdysseyActive, context.Width);
                return;
            }
            if (worker is TileMutatorWorker_River && !lakeCenterSet && map.waterInfo?.lakeCenter.IsValid == true
                && (ModsConfig.OdysseyActive || !(worker is TileMutatorWorker_Headwater)))
                AccessTools.Method(worker.GetType(),"GetRiverCenter").Invoke(worker,new object[] { map });
            worker.Init(map);
            if (worker is TileMutatorWorker_Lake && ModsConfig.OdysseyActive) lakeCenterSet = true;
        }

        internal static void InitPlainRiver(TileMutatorWorker_River worker, Map map, IntVec3? lakeCenter) =>
            InitRiver(worker, map, lakeCenter, false, 0f);

        internal static void InitRiver(TileMutatorWorker_River worker, Map map, IntVec3? lakeCenter, bool odyssey, float mapWidth)
        {
            if (worker is TileMutatorWorker_Headwater && !odyssey) return;
            IntVec3 center = lakeCenter ?? (IntVec3)AccessTools.Method(typeof(TileMutatorWorker_River), "GetRiverCenter").Invoke(worker, new object[] { map });
            float frequency = (float)AccessTools.PropertyGetter(typeof(TileMutatorWorker_River), "GetCurveFrequency").Invoke(worker, null);
            ModuleBase bend = new Perlin(frequency, 2.0, 1.0, 2, Rand.Int, QualityMode.Medium);
            ModuleBase shallow = new Perlin(0.029999999329447746, 2.0, 0.5, 3, Rand.Int, QualityMode.Medium);
            shallow = new Abs(shallow);
            ModuleBase bank = new Perlin(0.029999999329447746, 2.0, 2.0, 2, Rand.Int, QualityMode.Medium);
            ModuleBase width = new Perlin(0.019999999552965164, 2.0, 1.0, 3, Rand.Int, QualityMode.Medium);
            Type t = typeof(TileMutatorWorker_River);
            AccessTools.Field(t, "riverCenter").SetValue(worker, center);
            AccessTools.Field(t, "riverBendNoise").SetValue(worker, bend);
            AccessTools.Field(t, "shallowizer").SetValue(worker, shallow);
            AccessTools.Field(t, "riverbankNoise").SetValue(worker, bank);
            AccessTools.Field(t, "riverWidthNoise").SetValue(worker, width);
            if (worker is TileMutatorWorker_RiverIsland)
            {
                Type island = typeof(TileMutatorWorker_RiverIsland);
                FloatRange Range(string name) => (FloatRange)AccessTools.Field(island, name).GetValue(null);
                AccessTools.Field(island, "riverIslandAmplitude").SetValue(worker, Range("RiverIslandAmplitudeRange").RandomInRange);
                AccessTools.Field(island, "riverIslandCentrality").SetValue(worker, Range("RiverIslandCentralityRange").RandomInRange);
                AccessTools.Field(island, "widthFrac").SetValue(worker, Range("RiverWidthFracRange").RandomInRange);
            }
            else if (worker is TileMutatorWorker_Headwater)
            {
                ModuleBase lakeNoise = new DistFromPoint(mapWidth * 0.35f);
                lakeNoise = new ScaleBias(-1.0, 1.0, lakeNoise);
                lakeNoise = new Scale(TileMutatorWorker_Lake.LakeSquashRange.RandomInRange, 1.0, 1.0, lakeNoise);
                lakeNoise = new Rotate(0.0, Rand.Range(0f, 360f), 0.0, lakeNoise);
                lakeNoise = new Translate(-map.Center.x, 0.0, -map.Center.z, lakeNoise);
                lakeNoise = MapNoiseUtility.AddDisplacementNoise(lakeNoise, 0.006f, 15f, 2);
                lakeNoise = MapNoiseUtility.AddDisplacementNoise(lakeNoise, 0.015f, 15f);
                AccessTools.Field(typeof(TileMutatorWorker_Headwater), "lakeNoise").SetValue(worker, lakeNoise);
            }
        }

        internal bool PendingRiver(TileMutatorWorker worker) => worker is TileMutatorWorker_River river && pendingRivers.Contains(river);

        internal void RiverReproduced(TileMutatorWorker_River worker)
        {
            if (!pendingRivers.Remove(worker)) return;
            RiverReplayed = true;
            if (!context.Applied.Contains(worker.GetType().Name)) context.Applied.Add(worker.GetType().Name);
        }

        internal void RiverNotReproduced(TileMutatorWorker_River worker, string reason)
        {
            if (!pendingRivers.Remove(worker)) return;
            TerrainNotReplayed(worker.GetType());
            context.Unsupported.Add("terrain: " + worker.GetType().FullName);
            string variant = worker.GetType() == typeof(TileMutatorWorker_River) ? "" : worker.GetType().Name.Replace("TileMutatorWorker_", "") + ": ";
            context.Unsupported.Add("river: " + variant + (reason ?? "not reproduced"));
        }

        internal void SettleRivers(string reason)
        {
            foreach (TileMutatorWorker_River worker in pendingRivers.ToList()) RiverNotReproduced(worker, reason);
        }

        private bool InitForStream(TileMutatorWorker worker, ref bool lakeCenterSet)
        {
            try { InitInOrder(worker, ref lakeCenterSet); return true; }
            catch (Exception ex)
            {
                context.Unsupported.Add("Init random stream after: " + worker.GetType().FullName);
                Log.Error(HorizonLog.Prefix + "Replaying a Worker's Init draws failed; later Workers may not match. " + ex);
                return false;
            }
        }

        internal void TerrainNotReplayed(Type worker)
        {
            if (AccessTools.Method(worker, "GeneratePostTerrain").DeclaringType == typeof(TileMutatorWorker)) return;
            TerrainComplete = false;
            if (worker != typeof(TileMutatorWorker_River)) UnresolvedWater = true;
        }

        private int? geologicalAt, elevationSplit, geologicalElevationAt;
        private readonly List<TileMutatorWorker> elevationWorkers = new();
        internal GenerationCaves Caves { get; private set; }
        internal IEnumerable<TileMutatorWorker> WorkersBeforeGeological => workers.Take(geologicalAt ?? workers.Count);
        internal IEnumerable<TileMutatorWorker> WorkersAfterGeological => workers.Skip(geologicalAt ?? workers.Count);

        private static ModuleBase Noise(object w, string name) => (ModuleBase)AccessTools.Field(w.GetType(), name).GetValue(w);
        private static float Limit(object w, string name) => (float)AccessTools.Property(w.GetType(), name).GetValue(w);
        private void BuildElevation()
        {
            ElevationRandom ??= new GenerationRandom(context.SeedFor(typeof(GenStep_MutatorPostElevationFertility),1239847543));
            using (ElevationRandom.Enter())
            {
                int index = -1;
                bool elevationOrder = elevationWorkers != null && elevationWorkers.Count > 0;
                int? geological = elevationOrder ? geologicalElevationAt : geologicalAt;
                foreach (TileMutatorWorker worker in elevationOrder ? elevationWorkers : workers)
                {
                    if (++index == geological) elevationSplit = elevation.Count;
                    if (worker is TileMutatorWorker_Mountain && Caves != null)
                        context.Unsupported.Add("caves: random stream (Mountain listed after a Caves Worker)");
                    if (worker is TileMutatorWorker_Coast)
                    {
                        ModuleBase n = Noise(worker, "coastNoise");
                        float threshold = Limit(worker, "MaxForDeepWater");
                        elevation.Add((c, old) => n != null && n.GetValue(c) < threshold ? 0 : old);
                        if (worker is TileMutatorWorker_Archipelago)
                        {
                            ModuleBase arch = Noise(worker, "archNoise");
                            float sand = Limit(worker, "MaxForSand");
                            elevation.Add((c, old) => arch.GetValue(c) < sand ? 0 : old);
                        }
                        if (worker is TileMutatorWorker_CoastalAtoll)
                        {
                            ModuleBase inner=Noise(worker,"coastNoise");
                            elevation.Add((c,old) => inner.GetValue(c)<Limit(worker,"MaxForSand") ? 0 : old);
                        }
                        OwnsCoast = true;
                        context.Applied.Add(worker.GetType().Name);
                    }
                    else if (worker is TileMutatorWorker_Basin)
                    {
                        ModuleBase basin = Noise(worker, "basinNoise"), lake = Noise(worker, "lakeNoise");
                        elevation.Add((c, old) => lake.GetValue(c) > 0.45f ? 0 : basin.GetValue(c) > 0.5f ? basin.GetValue(c) : old);
                        context.Applied.Add(worker.GetType().Name);
                    }
                    else if (worker is TileMutatorWorker_Lake)
                    {
                        ModuleBase lake = Noise(worker, "lakeNoise");
                        elevation.Add((c, old) => lake.GetValue(c) > 0.45f ? 0 : old);
                        context.Applied.Add(worker.GetType().Name);
                    }
                    else if (worker is TileMutatorWorker_Cliffs)
                    {
                        ModuleBase cliff = Noise(worker,"cliffNoise");
                        elevation.Add((c,old) => cliff.GetValue(c)>0.75f ? cliff.GetValue(c) : old);
                        context.Applied.Add(worker.GetType().Name);
                    }
                    else if (worker is TileMutatorWorker_Valley)
                    {
                        ModuleBase valley = Noise(worker, "valleyNoise");
                        float cap = context.Map.TileInfo.WaterCovered ? 0 : float.MaxValue;
                        elevation.Add((c, old) => Mathf.Min(old + valley.GetValue(c), cap));
                        context.Applied.Add(worker.GetType().Name);
                    }
                    else if (worker is TileMutatorWorker_Plateau)
                    {
                        ModuleBase plateau = Noise(worker, "plateauNoise");
                        elevation.Add((c, old) => plateau.GetValue(c) > 0 ? plateau.GetValue(c) : old);
                        context.Applied.Add(worker.GetType().Name);
                    }
                    else if (worker is TileMutatorWorker_Mountain)
                    {
                        Map map = context.Map;
                        float angle = (Find.World.CoastAngleAt(map.Tile, BiomeDefOf.Ocean) ?? Find.World.CoastAngleAt(map.Tile, BiomeDefOf.Lake)) ?? Rand.Range(0, 360);
                        ModuleBase n = new DistFromAxis_Directional(map.Size.x * 0.15f);
                        n = new Translate(-map.Size.x * 0.2f, 0, 0, n);
                        n = new Clamp(0, 1, new ScaleBias(0.5, 0.5, n));
                        n = new Rotate(0, angle, 0, n);
                        n = new Translate(-map.Size.x / 2f, 0, -map.Size.z / 2f, n);
                        n = MapNoiseUtility.AddDisplacementNoise(n, 0.003f, 20f, 2);
                        float cap = map.TileInfo.WaterCovered ? 0 : float.MaxValue;
                        elevation.Add((c, old) => Mathf.Min(old + n.GetValue(c), cap));
                        context.Applied.Add(worker.GetType().Name);
                    }
                    else if (worker is TileMutatorWorker_HotSprings)
                    {
                        ModuleBase spring = Noise(worker, "springNoise");
                        elevation.Add((c, old) => spring.GetValue(c) > 0.65f ? 0f : old);
                        context.Applied.Add(worker.GetType().Name + " elevation");
                    }
                    else if (worker is TileMutatorWorker_LavaFlow)
                    {
                        ModuleBase lava = Noise(worker, "rockNoise");
                        elevation.Add((c, old) => !(lava.GetValue(c) > 0.85f) ? 0f : old);
                        context.Applied.Add(worker.GetType().Name + " elevation");
                    }
                    else if (worker is TileMutatorWorker_Headwater && Noise(worker, "lakeNoise") is ModuleBase headwater)
                    {
                        elevation.Add((c, old) => (float)headwater.GetValue(c.x, 0.0, c.z) > 0.45f ? 0f : old);
                        context.Applied.Add(worker.GetType().Name + " elevation");
                    }
                    else if (worker is TileMutatorWorker_Caves cave && GenerationCaves.Replays(worker))
                    {
                        if (worker is TileMutatorWorker_Cavern)
                        {
                            ModuleBase opening = Noise(worker, "openingNoise");
                            elevation.Add((c, old) => { float v = opening.GetValue(c); return v > 0.5f ? v : old; });
                        }
                        Caves ??= new GenerationCaves(context, this);
                        Caves.Add(cave, elevation.Count);
                    }
                    else
                    {
                        MethodInfo post = AccessTools.Method(worker.GetType(), "GeneratePostElevationFertility");
                        if (post.DeclaringType != typeof(TileMutatorWorker)) context.Unsupported.Add("elevation: " + worker.GetType().FullName);
                    }
                }
            }
        }

        internal float ElevationAt(IntVec3 c, float value)
        {
            foreach (GenStepWithParams step in context.Steps)
            {
                if (step.def.genStep is GenStep_ScaleElevation scale) value *= scale.factor;
                else if (step.def.genStep is GenStep_MutatorPostElevationFertility)
                    foreach (var transform in elevation) value = transform(c, value);
            }
            return value;
        }

        internal float ElevationBeforeGeological(IntVec3 c, float value) => ElevationBefore(c, value, elevationSplit ?? elevation.Count);

        internal float ElevationBefore(IntVec3 c, float value, int split)
        {
            foreach (GenStepWithParams step in context.Steps)
            {
                if (step.def.genStep is GenStep_ScaleElevation scale) value *= scale.factor;
                else if (step.def.genStep is GenStep_MutatorPostElevationFertility)
                {
                    for (int i = 0; i < split; i++) value = elevation[i](c, value);
                    break;
                }
            }
            return value;
        }

        internal float ElevationAfterGeological(IntVec3 c, float value)
        {
            int split = elevationSplit ?? elevation.Count;
            bool reached = false;
            foreach (GenStepWithParams step in context.Steps)
            {
                if (step.def.genStep is GenStep_ScaleElevation scale) { if (reached) value *= scale.factor; }
                else if (step.def.genStep is GenStep_MutatorPostElevationFertility && !reached)
                {
                    for (int i = split; i < elevation.Count; i++) value = elevation[i](c, value);
                    reached = true;
                }
            }
            return value;
        }

        internal TerrainDef TerrainAt(IntVec3 c, TerrainDef value, bool rock)
        {
            foreach (TileMutatorWorker worker in workers)
                value = TerrainAt(worker,c,value,rock);
            return value;
        }

        internal TerrainDef TerrainAt(TileMutatorWorker worker, IntVec3 c, TerrainDef value, bool rock)
        {
            if (worker is TileMutatorWorker_Caves cave && Caves != null) return Caves.TerrainAt(cave, c, value);
            if (value == null) return null;
            if (worker is TileMutatorWorker_Coast coast)
            {
                TerrainDef replacement = CoastTerrainAt(coast,c,value);
                if (replacement != null && (value.categoryType != TerrainDef.TerrainCategoryType.Stone || !rock && replacement.IsWater)) value = replacement;
            }
            else if (worker is TileMutatorWorker_Lake lake)
            {
                value=LakeTerrainAt(lake,c,value);
            }
            else if (worker is TileMutatorWorker_Dunes)
            {
                if (value == TerrainDefOf.Sand && Noise(worker,"dunesNoise").GetValue(c) <= 0.2f) value=TerrainDefOf.SoftSand;
            }
            else if (worker is TileMutatorWorker_Wetland && !rock && !value.IsWater)
            {
                float n=Noise(worker,"wetlandNoise").GetValue(c);
                if (n>0.35f) value=value.IsRock ? context.MudAt(c) : context.ShallowFreshAt(c);
                else if (n>0.015f && !value.IsIce) value=context.MudAt(c);
            }
            else if (worker is TileMutatorWorker_HotSprings)
            {
                float n = Noise(worker, "springNoise").GetValue(c);
                if (n > 0.85f) value = TerrainDefOf.HotSpring;
                else if (n > 0.65f) value = (RockAt?.Invoke(c.x, c.z) ?? ThingDefOf.Sandstone).building.naturalTerrain;
            }
            else if (worker is TileMutatorWorker_Patches && !context.PreventPatches)
            {
                if (value.categoryType!=TerrainDef.TerrainCategoryType.Stone && !value.IsWater
                    && value!=context.BeachAt(c) && value!=context.LakeshoreAt(c))
                    foreach (TerrainPatchMaker p in worker.def.terrainPatchMakers)
                    {
                        if (p.isPond && context.PreventPonds) continue;
                        TerrainDef replacement=context.PatchAt(p,c,rock ? 0f : value.fertility);
                        if (replacement!=null) value=replacement;
                    }
            }
            return value;
        }

        private sealed class CoastRule
        {
            internal ModuleBase Noise;
            internal float Deep, Shallow, Sand;
            internal bool Fresh;
        }
        private Dictionary<TileMutatorWorker_Coast, CoastRule> coastRules;

        internal TerrainDef CoastTerrainAt(TileMutatorWorker_Coast worker, IntVec3 c, TerrainDef previous)
        {
            coastRules ??= new Dictionary<TileMutatorWorker_Coast, CoastRule>();
            if (!coastRules.TryGetValue(worker, out CoastRule rule))
                coastRules.Add(worker, rule = new CoastRule
                {
                    Noise = Noise(worker,"coastNoise"), Fresh = worker is TileMutatorWorker_Lakeshore,
                    Deep = Limit(worker,"MaxForDeepWater"), Shallow = Limit(worker,"MaxForShallowWater"), Sand = Limit(worker,"MaxForSand")
                });
            if (rule.Noise == null) return null;
            float n = rule.Noise.GetValue(c);
            if (n < rule.Deep) return rule.Fresh ? context.DeepFreshAt(c) : context.DeepOceanAt(c);
            if (n < rule.Shallow) return rule.Fresh ? context.ShallowFreshAt(c) : context.ShallowOceanAt(c);
            if (n < rule.Sand && !previous.IsWater && !previous.IsIce) return rule.Fresh ? context.LakeshoreAt(c) : context.BeachAt(c);
            return null;
        }

        internal TerrainDef LakeTerrainAt(TileMutatorWorker_Lake worker,IntVec3 c,TerrainDef previous)
        {
            float n=(float)AccessTools.Method(worker.GetType(),"GetValAt").Invoke(worker,new object[] { c,context.Map });
            BiomeDef biome=context.BiomeAt(c.x,c.z);
            if (worker is TileMutatorWorker_DryLake)
                return n>0.5f?TerrainDefOf.DryLakeBed:n>0.45f?TerrainDefOf.Sand:previous;
            if (worker is TileMutatorWorker_Oasis)
                return n>0.75f?biome.waterDeepTerrain??TerrainDefOf.WaterDeep
                    :n>0.57f?biome.waterShallowTerrain??TerrainDefOf.WaterShallow
                    :n>0.45f && !previous.IsWater?TerrainDefOf.SoilRich
                    :n>0.3f && !previous.IsWater?TerrainDefOf.Soil:previous;
            bool deep=(bool)AccessTools.Property(worker.GetType(),"GenerateDeepWater").GetValue(worker);
            if (deep && n>0.75f) return worker is TileMutatorWorker_ToxicLake?TerrainDefOf.ToxicWaterDeep:biome.waterDeepTerrain??TerrainDefOf.WaterDeep;
            if (n>0.5f) return worker is TileMutatorWorker_ToxicLake?TerrainDefOf.ToxicWaterShallow:biome.waterShallowTerrain??TerrainDefOf.WaterShallow;
            if (n>0.45f && !previous.IsWater && !previous.IsIce) return context.LakeBeachOverride??biome.lakeBeachTerrain??TerrainDefOf.Sand;
            return previous;
        }

        internal static void InitLake(TileMutatorWorker_Lake worker, Map map)
        {
            if (!ModsConfig.OdysseyActive) return;
            IntVec3 center = (IntVec3)AccessTools.Method(worker.GetType(), "GetLakeCenter").Invoke(worker, new object[] { map });
            if (initializing) replayedLakeCenter = center;
            float radius = Limit(worker, "LakeRadius"), decay = Limit(worker, "LakeFalloffDecay");
            ModuleBase n = new ScaleBias(-1, 1, new DistFromPoint(map.Size.x * radius));
            n = new Scale(TileMutatorWorker_Lake.LakeSquashRange.RandomInRange, 1, 1, n);
            n = new Rotate(0, Rand.Range(0f, 360f), 0, n);
            n = new Translate(-center.x, 0, -center.z, n);
            n = new Power(new Clamp(0, 1, n), new Const(decay));
            n = MapNoiseUtility.AddDisplacementNoise(n, 0.006f, 40f, 2, map.Tile.tileId);
            n = MapNoiseUtility.AddDisplacementNoise(n, 0.015f, 15f, 4, map.Tile.tileId);
            AccessTools.Field(typeof(TileMutatorWorker_Lake), "lakeNoise").SetValue(worker, n);
            if (worker is TileMutatorWorker_LakeWithIsland)
            {
                ModuleBase island=new DistFromPoint(map.Size.x*0.4f);
                island=new Rotate(0,Rand.Range(0f,360f),0,island);
                island=new Translate(-center.x,0,-center.z,island);
                island=MapNoiseUtility.AddDisplacementNoise(island,0.006f,40f,2,map.Tile.tileId);
                island=MapNoiseUtility.AddDisplacementNoise(island,0.015f,15f,4,map.Tile.tileId);
                AccessTools.Field(typeof(TileMutatorWorker_Lake),"lakeNoise").SetValue(worker,new Min(n,island));
            }
        }
    }

    [HarmonyPatch]
    internal static class Patch_IndependentLakeInit
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(TileMutatorWorker_Lake),nameof(TileMutatorWorker_Lake.Init));
            yield return AccessTools.DeclaredMethod(typeof(TileMutatorWorker_LakeWithIsland),nameof(TileMutatorWorker_LakeWithIsland.Init));
        }
        internal static bool Prefix(TileMutatorWorker_Lake __instance, Map map)
        {
            if (!GenerationMutators.Initializing) return true;
            GenerationMutators.InitLake(__instance, map); return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_ReplayNoiseDebug
    {
        internal static IEnumerable<MethodBase> TargetMethods() => typeof(NoiseDebugUI).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "StoreNoiseRender");
        internal static bool Prefix() => !GenerationMutators.Initializing;
    }
}
