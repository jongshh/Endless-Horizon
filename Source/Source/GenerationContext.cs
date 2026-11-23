using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal sealed class GenerationContext
    {
        internal sealed class Capture
        {
            internal int Seed;
            internal GenStepWithParams[] Steps;
        }
        private static readonly ConditionalWeakTable<Map, Capture> captures = new();
        private static readonly ConditionalWeakTable<Map, StrongBox<int>> componentDraws = new();
        internal const int VanillaComponentDraws = 1;
        private const int MaxComponentDraws = 4096;
        internal readonly int InitDraws;
        internal readonly string InitDrawsSource;
        internal readonly Map Map;
        internal int Seed;
        internal readonly int Width, Height;
        internal string SeedSource;
        internal readonly GenStepWithParams[] Steps;
        internal TerrainDef CoastalBeachOverride, LakeBeachOverride;
        internal TerrainDef MudOverride;
        internal bool PreventPatches, PreventPonds;
        internal readonly List<string> Unsupported = new();
        internal readonly List<string> Applied = new();
        internal readonly List<string> Geological = new();
        internal float? MineableDensity;
        internal Func<TerrainPatchMaker,int> PatchSeed;
        internal readonly Dictionary<TerrainPatchMaker, PatchState> Patches = new();
        internal bool InitializingPatches;
        private ModuleBase biomeNoise;
        private BiomeDef secondary;
        internal Func<int, int, BiomeDef> ExternalBiome = null;

        internal TerrainDef BeachAt(IntVec3 c) => CoastalBeachOverride ?? BiomeAt(c.x, c.z).coastalBeachTerrain ?? TerrainDefOf.Sand;
        internal TerrainDef LakeshoreAt(IntVec3 c) => LakeBeachOverride ?? BiomeAt(c.x, c.z).lakeBeachTerrain ?? TerrainDefOf.Sand;
        internal TerrainDef MudAt(IntVec3 c) => MudOverride ?? BiomeAt(c.x, c.z).mudTerrain ?? TerrainDefOf.Mud;
        internal TerrainDef ShallowFreshAt(IntVec3 c) => BiomeAt(c.x, c.z).waterShallowTerrain ?? TerrainDefOf.WaterShallow;
        internal TerrainDef DeepFreshAt(IntVec3 c) => BiomeAt(c.x, c.z).waterDeepTerrain ?? TerrainDefOf.WaterDeep;
        internal TerrainDef ShallowOceanAt(IntVec3 c) => BiomeAt(c.x, c.z).oceanShallowTerrain ?? TerrainDefOf.WaterOceanShallow;
        internal TerrainDef DeepOceanAt(IntVec3 c) => BiomeAt(c.x, c.z).oceanDeepTerrain ?? TerrainDefOf.WaterOceanDeep;

        internal GenerationContext(Map map)
        {
            Map = map; Width = map.Size.x; Height = map.Size.z;
            CoastalBeachOverride=map.TileInfo.Mutators.FirstOrDefault(m=>m.overrideCoastalBeachTerrain!=null)?.overrideCoastalBeachTerrain;
            LakeBeachOverride=map.TileInfo.Mutators.FirstOrDefault(m=>m.overrideLakeBeachTerrain!=null)?.overrideLakeBeachTerrain;
            MudOverride=map.TileInfo.Mutators.FirstOrDefault(m=>m.overrideMudTerrain!=null)?.overrideMudTerrain;
            PreventPatches=map.TileInfo.Mutators.Any(m=>m.preventPatches);
            PreventPonds=map.TileInfo.Mutators.Any(m=>m.preventsPondGeneration);
            bool measured=componentDraws.TryGetValue(map,out StrongBox<int> draws);
            InitDraws=measured?draws.Value:VanillaComponentDraws;
            InitDrawsSource=measured?"measured":"vanilla default";
            if (captures.TryGetValue(map, out Capture saved))
            {
                Seed = saved.Seed; SeedSource = "generation capture"; Steps = saved.Steps;
            }
            else
            {
                Seed = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode());
                SeedSource = "world/tile replay";
                Steps = ResolveSteps(map);
            }
            AuditSteps();
            PatchSeed = GenerationStableSeed.For(map, Seed, Unsupported);
        }

        private void AuditSteps()
        {
            Type[] kernels={typeof(GenStep_ElevationFertility),typeof(GenStep_ScaleElevation),typeof(GenStep_MutatorPostElevationFertility),
                typeof(GenStep_RocksFromGrid),typeof(GenStep_Terrain),typeof(GenStep_MutatorPostTerrain)};
            foreach (var step in Steps)
            {
                Type type=step.def.genStep.GetType();
                if (kernels.Contains(type)) continue;
                if (step.def.order<300 || type.Assembly!=typeof(GenStep).Assembly)
                    Unsupported.Add("GenStep: "+type.FullName+" ("+step.def.defName+", order "+step.def.order+")");
            }
            foreach (Type type in kernels.Where(t=>t!=typeof(GenStep_ScaleElevation)))
                if (Steps.Count(s=>s.def.genStep.GetType()==type)>1) Unsupported.Add("repeated replay stage: "+type.Name);
            int terrain=StepIndex(typeof(GenStep_Terrain));
            if (Steps.Select((s,i)=>(s,i)).Any(p=>p.s.def.genStep is GenStep_ScaleElevation && p.i>terrain))
                Unsupported.Add("elevation scaling after Terrain");
        }

        internal static bool GeneratedThisSession(Map map) => captures.TryGetValue(map,out _);

        internal static int? DrawsBetween(ulong before, ulong after)
        {
            if ((uint)before!=(uint)after) return null;
            long count=(long)(after>>32)-(long)(before>>32);
            return count>=0 && count<=MaxComponentDraws ? (int)count : null;
        }

        internal static void RecordDraws(Map map, ulong before, ulong after)
        {
            if (DrawsBetween(before,after) is int count) componentDraws.AddOrUpdate(map,new StrongBox<int>(count));
        }

        internal void SkipComponentDraws()
        {
            for (int i=0;i<InitDraws;i++) _=Rand.Int;
        }

        internal string StepTrace() => string.Join(" → ",Steps.Select((s,i)=>
            $"{s.def.defName??s.def.genStep.GetType().Name}@{s.def.order}:seedPart={EffectiveSeedPart(Steps,i)}"));

        internal void ResetForVanilla()
        {
            ExternalBiome = null;
            InitializingPatches = false;
            MineableDensity = null;
            Patches.Clear();
            Applied.Clear();
        }

        internal static void Record(Map map, int seed, IEnumerable<GenStepWithParams> steps)
        {
            if (map == null) return;
            var sorted=steps.OrderBy(s=>s.def.order).ThenBy(s=>s.def.index).ToList();
            sorted.RemoveWhere(a=>sorted.Any(b=>b.def.preventsGenSteps!=null && b.def.preventsGenSteps.Contains(a.def)));
            captures.AddOrUpdate(map, new Capture { Seed = seed, Steps = sorted.ToArray() });
        }

        internal static GenStepWithParams[] ResolveSteps(Map map)
        {
            var defs = map.generatorDef.genSteps.Concat(map.TileInfo.Mutators.SelectMany(m => m.extraGenSteps))
                .Concat(map.Biome.extraGenSteps).ToList();
            var prevented = new HashSet<GenStepDef>(map.Biome.preventGenSteps.Concat(map.TileInfo.Mutators.SelectMany(m => m.preventGenSteps)));
            foreach (GenStepDef d in defs) if (d.preventsGenSteps != null) prevented.UnionWith(d.preventsGenSteps);
            var parts = Find.Scenario == null ? Array.Empty<ScenPart>() : (IEnumerable<ScenPart>)AccessTools.Field(typeof(Scenario), "parts").GetValue(Find.Scenario);
            foreach (ScenPart p in parts)
                if (p is ScenPart_DisableMapGen && p.def.genStep != null) prevented.Add(p.def.genStep);
            return defs.Where(d => !prevented.Contains(d)).OrderBy(d => d.order).ThenBy(d => d.index)
                .Select(d => new GenStepWithParams(d, default)).ToArray();
        }

        internal static int EffectiveSeedPart(IReadOnlyList<GenStepWithParams> steps, int index)
        {
            int part = steps[index].def.genStep.SeedPart, duplicate = 0;
            for (int i = 0; i < index; i++) if (steps[i].def.genStep.SeedPart == part) duplicate++;
            return part + duplicate;
        }

        internal int SeedFor(Type stepType, int fallback)
        {
            for (int i = 0; i < Steps.Length; i++)
                if (stepType.IsInstanceOfType(Steps[i].def.genStep)) return Gen.HashCombineInt(Seed, EffectiveSeedPart(Steps, i));
            Unsupported.Add("missing generation step: " + stepType.Name);
            return Gen.HashCombineInt(Seed, fallback);
        }

        internal int StepIndex(Type type) => Array.FindIndex(Steps, s => type.IsInstanceOfType(s.def.genStep));

        internal void CleanupPatches(IEnumerable<TerrainPatchMaker> makers)
        {
            if (makers != null) foreach (TerrainPatchMaker maker in makers) Patches.Remove(maker);
        }

        internal BiomeDef BiomeAt(int x, int z)
        {
            if (ExternalBiome != null) return ExternalBiome(x, z);
            return BaseBiomeAt(x, z);
        }
        internal BiomeDef BaseBiomeAt(int x, int z) => biomeNoise != null && biomeNoise.GetValue(x, 0, z) < 0 ? secondary : Map.Biome;

        internal void InitMixed(TileMutatorWorker_MixedBiome worker)
        {
            if (!ModsConfig.OdysseyActive) return;
            var scratch=(List<RimWorld.Planet.PlanetTile>)AccessTools.Field(typeof(TileMutatorWorker_MixedBiome),"tmpNeighbors").GetValue(null);
            RimWorld.Planet.PlanetTile[] previous=scratch.ToArray();
            try { secondary = worker.SecondaryBiome(Map.Tile, Map.Tile.Layer); }
            finally { scratch.Clear(); scratch.AddRange(previous); }
            if (secondary == null) { Unsupported.Add("MixedBiome secondary biome unavailable"); return; }
            var neighbors = new List<RimWorld.Planet.PlanetTile>();
            Find.World.grid.GetTileNeighbors(Map.Tile, neighbors);
            float mean = GenMath.MeanAngle(neighbors.Where(t => t.Tile.PrimaryBiome == secondary)
                .Select(t => Find.WorldGrid.GetHeadingFromTo(t, Map.Tile)).ToList());
            ModuleBase n = new DistFromAxis_Directional(Width / 2f);
            n = new Rotate(0, (450f - mean) % 360f, 0, n);
            n = new Translate(-Width / 2f, 0, -Height / 2f, n);
            n = MapNoiseUtility.AddDisplacementNoise(n, 0.006f, 40f, 2);
            biomeNoise = MapNoiseUtility.AddDisplacementNoise(n, 0.015f, 20f, 6);
            Applied.Add("MixedBiome");
        }

        internal TerrainDef PatchAt(TerrainPatchMaker maker, IntVec3 c, float fertility)
        {
            if (fertility < maker.minFertility || fertility > maker.maxFertility) return null;
            if (!Patches.TryGetValue(maker, out PatchState p))
            {
                if (!InitializingPatches)
                {
                    if (!Unsupported.Contains("patch not initialised by original map pass")) Unsupported.Add("patch not initialised by original map pass");
                    return null;
                }
                Patches.Add(maker, p = new PatchState(maker, PatchSeed?.Invoke(maker)));
            }
            return p.At(c, Width, Height);
        }

        internal sealed class PatchState
        {
            internal readonly TerrainPatchMaker Maker;
            internal readonly ModuleBase Noise;
            private readonly Dictionary<(int, int), bool> sizePass = new();
            internal PatchState(TerrainPatchMaker maker, int? seed = null)
            {
                Maker = maker;
                Noise = new Perlin(maker.perlinFrequency, maker.perlinLacunarity, maker.perlinPersistence,
                    maker.perlinOctaves, seed ?? Rand.Range(0, int.MaxValue), QualityMode.Medium);
            }
            internal TerrainDef At(IntVec3 c, int w, int h)
            {
                TerrainDef t = TerrainThreshold.TerrainAtValue(Maker.thresholds, Noise.GetValue(c));
                if (Maker.minSize <= 0 || t == null) return t;
                if (sizePass.TryGetValue((c.x, c.z), out bool pass)) return pass ? t : null;
                bool inside = c.x >= 0 && c.z >= 0 && c.x < w && c.z < h;
                var seen = new HashSet<(int, int)> { (c.x, c.z) };
                var queue = new Queue<IntVec3>(); queue.Enqueue(c);
                int count = 0;
                while (queue.Count > 0 && count < Maker.minSize)
                {
                    IntVec3 at = queue.Dequeue(); count++;
                    foreach (IntVec3 d in GenAdj.CardinalDirections)
                    {
                        IntVec3 n = at + d;
                        if (inside && (n.x < 0 || n.z < 0 || n.x >= w || n.z >= h)) continue;
                        if (seen.Contains((n.x, n.z)) || TerrainThreshold.TerrainAtValue(Maker.thresholds, Noise.GetValue(n)) == null) continue;
                        seen.Add((n.x, n.z)); queue.Enqueue(n);
                    }
                }
                pass = count >= Maker.minSize;
                foreach (var point in seen) sizePass[point] = pass;
                return pass ? t : null;
            }
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.ConstructComponents))]
    internal static class Patch_ComponentDraws
    {
        private static Func<ulong> state;

        private static ulong Read()
        {
            state ??= AccessTools.MethodDelegate<Func<ulong>>(AccessTools.PropertyGetter(typeof(Rand),"StateCompressed"));
            return state();
        }

        [HarmonyPriority(Priority.First)]
        internal static void Prefix(out ulong? __state)
        {
            __state = null;
            try { __state = Read(); }
            catch (Exception ex)
            {
                Log.ErrorOnce(HorizonLog.Prefix + "Reading the random counter failed; the outside assumes the vanilla value. " + ex, 0x45484452);
            }
        }

        [HarmonyPriority(Priority.Last)]
        internal static void Postfix(Map __instance, ulong? __state)
        {
            if (__state == null) return;
            try { GenerationContext.RecordDraws(__instance, __state.Value, Read()); }
            catch (Exception ex)
            {
                Log.ErrorOnce(HorizonLog.Prefix + "Recording the random counter failed; the outside assumes the vanilla value. " + ex, 0x45484453);
            }
        }
    }

    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateContentsIntoMap))]
    internal static class Patch_GenerationIdentity
    {
        [HarmonyPriority(Priority.Last)]
        internal static void Prefix(Map map, int seed, IEnumerable<GenStepWithParams> genStepDefs)
        {
            try
            {
                GenerationContext.Record(map, seed, genStepDefs.OrderBy(s => s.def.order).ThenBy(s => s.def.index));
            }
            catch (Exception ex)
            {
                Log.Error(HorizonLog.Prefix + "Reading the generation order failed; the map generates normally and the outside replays from world and tile. " + ex);
            }
        }
    }
}
