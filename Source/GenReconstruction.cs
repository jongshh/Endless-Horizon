using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    public sealed class GenReconstruction
    {
        private const int ElevationFertilitySeedPart = 826504671;
        private const int TerrainSeedPart = 262606459;

        private static readonly ConditionalWeakTable<Map, GenReconstruction> cache = new ConditionalWeakTable<Map, GenReconstruction>();

        public ModuleBase Elevation;
        public ModuleBase Fertility;
        public List<RockNoises.RockNoise> Rocks;
        public float WaterCap = float.MaxValue;
        public string Report = "not run";
        public float RockMatch, TerrainMatch;
        public bool Usable;
        public long Ms;

        public static GenReconstruction For(Map map)
        {
            if (!cache.TryGetValue(map, out GenReconstruction r))
            {
                r = Build(map);
                cache.AddOrUpdate(map, r);
            }
            return r;
        }

        public static void Forget(Map map) => cache.Remove(map);

        private static GenReconstruction Build(Map map)
        {
            var r = new GenReconstruction();
            var sw = Stopwatch.StartNew();
            try
            {
                if (map.IsPocketMap || map.generatorDef == null || map.generatorDef.isUnderground)
                {
                    r.Report = "skipped (pocket/underground map)";
                    return r;
                }
                int mapSeed = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode());
                r.BuildRocks(map, mapSeed);
                r.BuildElevationFertility(map, mapSeed);
                r.Compare(map, mapSeed);
            }
            catch (Exception ex)
            {
                r.Usable = false;
                r.Report = "failed: " + ex.GetType().Name + ": " + ex.Message;
                Log.Warning(EndlessHorizonMod.LogPrefix + "Generator reconstruction failed; the outside uses the mirror instead. " + ex);
            }
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        private void BuildRocks(Map map, int mapSeed)
        {
            List<RockNoises.RockNoise> previous = RockNoises.rockNoises;
            Rand.PushState(mapSeed);
            try
            {
                RockNoises.Init(map);
                Rocks = RockNoises.rockNoises;
            }
            finally
            {
                Rand.PopState();
                RockNoises.rockNoises = previous;
            }
        }

        private void BuildElevationFertility(Map map, int mapSeed)
        {
            Rand.PushState(Gen.HashCombineInt(mapSeed, ElevationFertilitySeedPart));
            try
            {
                bool natural = !map.TileInfo.Mutators.Any(m => m.preventNaturalElevation);
                if (natural)
                {
                    ModuleBase m = new Perlin(GenStep_ElevationFertility.ElevationFreqRange.RandomInRange, 2.0, 0.5, 3, Rand.Range(0, int.MaxValue), QualityMode.High);
                    m = MapNoiseUtility.AddDisplacementNoise(m, GenStep_ElevationFertility.DetailFreqRange.RandomInRange, GenStep_ElevationFertility.DetailStrengthRange.RandomInRange, GenStep_ElevationFertility.DetailOctavesRange.RandomInRange);
                    m = new Scale(new FloatRange(1f, 1.15f).RandomInRange, 1.0, 1.0, m);
                    m = new Rotate(0.0, Rand.Range(0f, 180f), 0.0, m);
                    m = MapNoiseUtility.AddDisplacementNoise(m, new FloatRange(0.01f, 0.02f).RandomInRange, new FloatRange(0f, 15f).RandomInRange, new IntRange(3, 4).RandomInRange);
                    m = new Scale(new FloatRange(1f, 1.15f).RandomInRange, 1.0, 1.0, m);
                    m = new Rotate(0.0, Rand.Range(0f, 180f), 0.0, m);
                    m = new ScaleBias(0.5, 0.5, m);
                    float factor = 1f;
                    switch (map.TileInfo.HillinessForElevationGen)
                    {
                        case Hilliness.Flat: factor = MapGenTuning.ElevationFactorFlat; break;
                        case Hilliness.SmallHills: factor = MapGenTuning.ElevationFactorSmallHills; break;
                        case Hilliness.LargeHills: factor = MapGenTuning.ElevationFactorLargeHills; break;
                        case Hilliness.Mountainous: factor = MapGenTuning.ElevationFactorMountains; break;
                        case Hilliness.Impassable: factor = MapGenTuning.ElevationFactorImpassableMountains; break;
                    }
                    Elevation = new Multiply(m, new Const(factor));
                    WaterCap = map.TileInfo.WaterCovered ? 0f : float.MaxValue;
                }
                ModuleBase f = new Perlin(0.020999999716877937, 2.0, 0.5, 6, Rand.Range(0, int.MaxValue), QualityMode.High);
                Fertility = new ScaleBias(0.5, 0.5, f);
            }
            finally
            {
                Rand.PopState();
            }
        }

        public float ElevationAt(int x, int z) => Elevation == null ? 0f : Mathf.Min((float)Elevation.GetValue(x, 0, z), WaterCap);

        public float FertilityAt(int x, int z) => (float)Fertility.GetValue(x, 0, z);

        public ThingDef RockAt(int x, int z)
        {
            ThingDef best = null;
            float bestValue = -999999f;
            foreach (RockNoises.RockNoise n in Rocks)
            {
                float v = (float)n.noise.GetValue(x, 0, z);
                if (v > bestValue)
                {
                    best = n.rockDef;
                    bestValue = v;
                }
            }
            return best;
        }

        private static readonly AccessTools.FieldRef<TerrainPatchMaker, ModuleBase> patchNoise =
            AccessTools.FieldRefAccess<TerrainPatchMaker, ModuleBase>("noise");

        private readonly Dictionary<TerrainPatchMaker, ModuleBase> patchNoises = new Dictionary<TerrainPatchMaker, ModuleBase>();
        public float EvaluatorMatch;
        public TerrainDef[] Predicted;
        public string TopMismatches = "";

        private void Compare(Map map, int mapSeed)
        {
            var biomes = new HashSet<BiomeDef>();
            if (map.Biome != null) biomes.Add(map.Biome);
            foreach (IntVec3 c in map.AllCells)
            {
                BiomeDef b = map.BiomeAt(c);
                if (b != null) biomes.Add(b);
            }
            var makers = biomes.SelectMany(b => b.terrainPatchMakers ?? new List<TerrainPatchMaker>()).Distinct().ToList();
            List<RockNoises.RockNoise> previousRocks = RockNoises.rockNoises;
            RockNoises.rockNoises = Rocks;
            foreach (TerrainPatchMaker p in makers) p.Cleanup();
            int rockTotal = 0, rockHit = 0, terrainTotal = 0, terrainHit = 0, evalHit = 0;
            var misses = new Dictionary<string, int>();
            Rand.PushState(Gen.HashCombineInt(mapSeed, TerrainSeedPart));
            try
            {
                int n = map.cellIndices.NumGridCells;
                var predictedGrid = new TerrainDef[n];
                var elevGrid = new float[n];
                var fertGrid = new float[n];
                foreach (IntVec3 c in map.AllCells)
                {
                    int i = map.cellIndices.CellToIndex(c);
                    float elev = elevGrid[i] = ElevationAt(c.x, c.z);
                    float fert = fertGrid[i] = FertilityAt(c.x, c.z);
                    predictedGrid[i] = MapGenUtility.TerrainFrom(c, map, elev, fert, elev > 0.7f);
                }
                foreach (TerrainPatchMaker p in makers) patchNoises[p] = patchNoise(p);
                Predicted = predictedGrid;
                foreach (IntVec3 c in map.AllCells)
                {
                    if (Developed(map, c))
                    {
                        continue;
                    }
                    int ci = map.cellIndices.CellToIndex(c);
                    bool predictedRock = elevGrid[ci] > 0.7f;
                    RoofDef roof = c.GetRoof(map);
                    Building edifice = c.GetEdifice(map);
                    bool actualRock = (edifice != null && edifice.def.mineable) || (roof != null && roof.isNatural);
                    rockTotal++;
                    if (actualRock == predictedRock) rockHit++;
                    TerrainDef predicted = predictedGrid[ci];
                    TerrainDef actual = map.terrainGrid.BaseTerrainAt(c);
                    if (actual == null || actual.IsWater || predicted == null || predicted.IsWater)
                    {
                        continue;
                    }
                    terrainTotal++;
                    if (TerrainBlend.Matches(actual, predicted))
                    {
                        terrainHit++;
                    }
                    else
                    {
                        string key = actual.defName + "→" + predicted.defName;
                        misses.TryGetValue(key, out int count);
                        misses[key] = count + 1;
                    }
                    if (TerrainBlend.Matches(actual, Evaluate(map, map.BiomeAt(c) ?? map.Biome, c, elevGrid[ci], fertGrid[ci]))) evalHit++;
                }
            }
            finally
            {
                Rand.PopState();
                RockNoises.rockNoises = previousRocks;
                foreach (TerrainPatchMaker p in makers) p.Cleanup();
            }
            RockMatch = rockTotal == 0 ? 0f : rockHit / (float)rockTotal;
            TerrainMatch = terrainTotal == 0 ? 0f : terrainHit / (float)terrainTotal;
            EvaluatorMatch = terrainTotal == 0 ? 0f : evalHit / (float)terrainTotal;
            Usable = RockMatch >= 0.9f && TerrainMatch >= 0.8f && EvaluatorMatch >= 0.8f;
            TopMismatches = string.Join(", ", misses.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key + " " + kv.Value));
            string mutators = string.Join(",", map.TileInfo.Mutators.Select(m => m.defName));
            Report = $"rock/open {RockMatch:P1} ({rockTotal} cells), terrain {TerrainMatch:P1}, outside evaluator {EvaluatorMatch:P1} ({terrainTotal} cells), " +
                     $"elevation {(Elevation == null ? "none (mutator)" : "natural")}, biome {map.Biome?.defName}{(biomes.Count > 1 ? " +" + (biomes.Count - 1) + " mixed" : "")}, " +
                     $"mutators [{mutators}], top misses [{TopMismatches}], usable {Usable}";
        }

        private TerrainDef Evaluate(Map map, BiomeDef biome, IntVec3 c, float elev, float fert)
        {
            TerrainDef t = null;
            IList<TileMutatorDef> mutators = map.TileInfo.Mutators;
            bool noPonds = mutators.Any(m => m.preventsPondGeneration);
            if (!mutators.Any(m => m.preventPatches) && biome.terrainPatchMakers != null)
            {
                foreach (TerrainPatchMaker p in biome.terrainPatchMakers)
                {
                    if ((noPonds && p.isPond) || fert < p.minFertility || fert > p.maxFertility) continue;
                    if (!patchNoises.TryGetValue(p, out ModuleBase noise) || noise == null) continue;
                    t = TerrainThreshold.TerrainAtValue(p.thresholds, (float)noise.GetValue(c.x, 0, c.z));
                    if (t != null) break;
                }
            }
            if (t == null)
            {
                if (elev > 0.55f && elev < 0.61f && !biome.noGravel) t = biome.gravelTerrain ?? TerrainDefOf.Gravel;
                else if (elev >= 0.61f) t = RockAt(c.x, c.z)?.building?.naturalTerrain;
            }
            if (t == null) t = TerrainThreshold.TerrainAtValue(biome.terrainsByFertility, fert);
            if (t == null) return null;
            if (elev > 0.7f && t.supportsRock) t = RockAt(c.x, c.z)?.building?.naturalTerrain ?? t;
            return t;
        }

        private static bool Developed(Map map, IntVec3 c)
        {
            if (map.areaManager.Home != null && map.areaManager.Home[c]) return true;
            Building b = c.GetEdifice(map);
            if (b != null && !b.def.mineable) return true;
            TerrainDef top = map.terrainGrid.TerrainAt(c);
            if (top != map.terrainGrid.BaseTerrainAt(c) || (top != null && !top.natural)) return true;
            RoofDef roof = c.GetRoof(map);
            if (roof != null && !roof.isNatural) return true;
            return map.zoneManager.ZoneAt(c) != null;
        }

        public TerrainDef TerrainOutside(Map map, IntVec3 c, out float elev)
        {
            elev = 0f;
            try
            {
                if (map.Biome == null || Fertility == null) return null;
                elev = ElevationAt(c.x, c.z);
                return Evaluate(map, map.Biome, c, elev, FertilityAt(c.x, c.z));
            }
            catch (Exception ex)
            {
                Log.WarningOnce(EndlessHorizonMod.LogPrefix + "Generator terrain for an outside cell failed; the cell uses the mirror. " + ex, 0x45484754);
                return null;
            }
        }
    }
}
