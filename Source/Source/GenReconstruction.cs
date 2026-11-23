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
    internal sealed class GenReconstruction
    {
        private const int ElevationFertilitySeedPart = 826504671;
        private const int TerrainSeedPart = 262606459;

        private static readonly ConditionalWeakTable<Map, GenReconstruction> cache = new ConditionalWeakTable<Map, GenReconstruction>();

        internal GenerationContext Context;
        internal GenerationMutators Mutators;
        internal GenerationSnapshot Snapshot;
        public BiomeDef BiomeAt(int x, int z) => Context.BiomeAt(x, z);
        public bool OwnsCoast => Snapshot?.OwnsTerrain == true || Snapshot?.OwnsGeologicalTerrain == true || Mutators?.OwnsCoast == true;
        public bool OwnsTerrain => Snapshot?.OwnsTerrain == true;
        public bool IsRock(IntVec3 c, TerrainDef terrain) => (Snapshot?.SolidReady==true && Snapshot.Covers(c.x,c.z) ? Snapshot.Solid[Snapshot.Index(c.x,c.z)] : ElevationAt(c.x,c.z)>0.7f) && terrain?.supportsRock == true
            && !terrain.IsWater && (Snapshot == null || !Snapshot.Covers(c.x,c.z) || Snapshot.Caves[Snapshot.Index(c.x,c.z)] <= 0);
        public ModuleBase Elevation;
        public ModuleBase Fertility;
        public List<RockNoises.RockNoise> Rocks;
        public float WaterCap = float.MaxValue;
        public string Report = "not run";
        public float RockMatch, TerrainMatch;
        public bool Usable;
        public long Ms;

        private IEnumerator<bool> work;
        private bool ready;
        private int buildRing;
        internal GenerationMetrics Metrics;

        public static GenReconstruction For(Map map)
        {
            foreach (bool step in Prepare(map)) { }
            return cache.GetValue(map, _ => throw new InvalidOperationException("Missing generation context."));
        }

        internal static GenReconstruction Peek(Map map) =>
            map != null && cache.TryGetValue(map, out GenReconstruction r) && r.ready && r.buildRing == HorizonSettings.Current.ringWidth ? r : null;

        internal static IEnumerable<bool> Prepare(Map map)
        {
            if (!cache.TryGetValue(map,out GenReconstruction r) || r.buildRing != HorizonSettings.Current.ringWidth)
            {
                r?.work?.Dispose();
                r = new GenReconstruction { buildRing = HorizonSettings.Current.ringWidth };
                r.work = r.BuildSteps(map).GetEnumerator();
                cache.AddOrUpdate(map,r);
            }
            while (!r.ready)
            {
                bool more;
#if EH_DEV
                ulong before=GenerationReference.Enabled?GenerationReference.MapFingerprint(map):0;
#endif
                var clock=Stopwatch.StartNew();
                Rand.PushState();
                try
                {
                    more=r.work.MoveNext();
#if EH_DEV
                    if (GenerationReference.Enabled && before!=GenerationReference.MapFingerprint(map))
                        throw new InvalidOperationException("Generation replay changed the live map fingerprint (terrain/biome/roof/fog/Things).");
#endif
                }
                catch (Exception ex)
                {
                    more=false; r.Usable=false;
                    r.Report="failed: "+ex;
                    Log.Error(HorizonLog.Prefix+"Generator reconstruction failed; the outside uses the mirror instead. "+ex);
                }
                finally
                {
                    Rand.PopState();
                    HorizonPerf.Record("generation replay unit",clock.Elapsed.TotalMilliseconds);
                }
                if (!more)
                {
                    r.work.Dispose(); r.work=null; r.ready=true;
                    break;
                }
                yield return false;
            }
        }

        public static void Forget(Map map)
        {
            if (cache.TryGetValue(map,out GenReconstruction r)) r.work?.Dispose();
            cache.Remove(map);
        }

        private IEnumerable<bool> BuildSteps(Map map)
        {
            var sw=Stopwatch.StartNew();
            if (map.IsPocketMap || map.generatorDef==null || map.generatorDef.isUnderground)
            {
                Report="skipped (pocket/underground map)"; yield break;
            }
            Context=new GenerationContext(map);
            GenerationGeological gl=null;
            Exception glFailure=null;
            try { gl=GenerationGeological.TryCreate(Context); }
            catch (Exception ex) { glFailure=ex; }
            using (gl)
            {
                int sourceSeed=Context.Seed;
                Mutators=new GenerationMutators(Context);
                yield return false;
                BuildRocks(map,sourceSeed); BuildElevationFertility(map,sourceSeed);
                yield return false;
                var snapshot=new GenerationSnapshot(map.Size.x,map.Size.z,buildRing);
                if (gl!=null)
                {
                    using IEnumerator<bool> stages=snapshot.BuildGeological(this,gl).GetEnumerator();
                    while (true)
                    {
                        bool more;
                        try { using (new GenerationRockNoiseScope(Rocks)) more=stages.MoveNext(); }
                        catch (Exception ex) { glFailure=ex; break; }
                        if (!more) break;
                        yield return stages.Current;
                    }
                }
                if (gl==null || glFailure!=null)
                {
                    if (glFailure!=null)
                    {
                        Snapshot=null;
                        Context.ResetForVanilla();
                        Mutators=new GenerationMutators(Context);
                        Mutators.MarkIncomplete();
                        Context.Unsupported.Add("GL replay failed at "+(gl?.Stage??"load")+" ("+glFailure.GetBaseException().GetType().Name+": "+glFailure.GetBaseException().Message+"); vanilla replay is judged instead");
                        Log.Warning(HorizonLog.Prefix+"Geological Landforms replay failed; the vanilla replay is used where it matches the map. "+glFailure);
                        snapshot=new GenerationSnapshot(map.Size.x,map.Size.z,buildRing);
                    }
                    foreach (bool step in snapshot.BuildVanilla(this)) yield return step;
                }
            }
            foreach (bool step in CompareSteps(map)) yield return step;
            Ms=sw.ElapsedMilliseconds;
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

        internal sealed class GenerationRockNoiseScope : IDisposable
        {
            private readonly List<RockNoises.RockNoise> previous;
            private bool disposed;

            internal GenerationRockNoiseScope(List<RockNoises.RockNoise> rocks)
            {
                previous = RockNoises.rockNoises;
                RockNoises.rockNoises = rocks;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                RockNoises.rockNoises = previous;
            }
        }

        private void BuildElevationFertility(Map map, int mapSeed)
        {
            Rand.PushState(Context.SeedFor(typeof(GenStep_ElevationFertility), ElevationFertilitySeedPart));
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

        public float ElevationAt(int x, int z)
        {
            if (Snapshot != null && Snapshot.Covers(x,z)) return Snapshot.Elevation[Snapshot.Index(x,z)];
            return BaseElevationAt(x,z);
        }
        internal float BaseElevationAt(int x, int z)
        {
            float value = Elevation == null ? 0f : Mathf.Min((float)Elevation.GetValue(x, 0, z), WaterCap);
            return Mutators?.ElevationAt(new IntVec3(x, 0, z), value) ?? value;
        }

        internal float ElevationBeforeGeologicalAt(int x, int z)
        {
            float value = Elevation == null ? 0f : Mathf.Min((float)Elevation.GetValue(x, 0, z), WaterCap);
            return Mutators?.ElevationBeforeGeological(new IntVec3(x, 0, z), value) ?? value;
        }

        public float FertilityAt(int x, int z) => Snapshot != null && Snapshot.Covers(x,z) ? Snapshot.Fertility[Snapshot.Index(x,z)] : BaseFertilityAt(x,z);
        internal float BaseFertilityAt(int x, int z) => (float)Fertility.GetValue(x, 0, z);

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

        public float EvaluatorMatch;
        public TerrainDef[] Predicted;
        public string TopMismatches = "";

        private IEnumerable<bool> CompareSteps(Map map)
        {
            int w=map.Size.x,h=map.Size.z,n=w*h;
            Predicted=new TerrainDef[n];
            var elevation=new float[n]; var fertility=new float[n];
            var random=new GenerationRandom(Context.SeedFor(typeof(GenStep_Terrain),TerrainSeedPart));
            for (int z=0;z<h;z++)
            {
                using (random.Enter())
                {
                    Context.InitializingPatches=true;
                    try
                    {
                        for (int x=0;x<w;x++)
                        {
                            var c=new IntVec3(x,0,z); int i=z*w+x;
                            elevation[i]=ElevationAt(x,z); fertility[i]=FertilityAt(x,z);
                            Predicted[i]=FinalTerrain(map,c,elevation[i],fertility[i]);
                        }
                    }
                    finally { Context.InitializingPatches=false; }
                }
                yield return false;
            }
            Metrics=new GenerationMetrics();
            GenerationExternalCells external=GenerationExternalCells.For(map,this);
            yield return false;
            int rockTotal=0,rockHit=0,terrainTotal=0,terrainHit=0;
            var misses=new Dictionary<string,int>();
            for (int z=0;z<h;z++)
            {
                for (int x=0;x<w;x++)
                {
                    var c=new IntVec3(x,0,z); int i=z*w+x;
                    if (Developed(map,c)) continue;
                    TerrainDef actual=map.terrainGrid.BaseTerrainAt(c), predicted=Predicted[i];
                    Building edifice=c.GetEdifice(map);
                    bool actualRock=edifice!=null && edifice.def.mineable;
                    bool expectedRock=IsRock(c,predicted);
                    bool same=actual!=null && predicted!=null && TerrainBlend.Matches(actual,predicted);
                    bool owned=external.Contains(c,actual,predicted,actualRock,expectedRock);
                    Metrics.Record(x,z,w,h,map.BiomeAt(c),BiomeAt(x,z),actual,predicted,same,actualRock==expectedRock,owned);
                    if(owned) continue;
                    rockTotal++; if(actualRock==expectedRock) rockHit++;
                    if(actual==null || predicted==null) continue;
                    terrainTotal++; if(same) terrainHit++;
                    else
                    {
                        string key=actual.defName+"→"+predicted.defName;
                        misses.TryGetValue(key,out int count); misses[key]=count+1;
                    }
                }
                yield return false;
            }
            RockMatch=rockTotal==0?0:rockHit/(float)rockTotal;
            TerrainMatch=terrainTotal==0?0:terrainHit/(float)terrainTotal;
            EvaluatorMatch=TerrainMatch;
            Usable=RockMatch>=0.9f && TerrainMatch>=0.8f && Metrics.Acceptable;
            TopMismatches=string.Join(", ",misses.OrderByDescending(kv=>kv.Value).Take(3).Select(kv=>kv.Key+" "+kv.Value));
            Report=$"rock/open {RockMatch:P1} ({rockTotal} cells), terrain {TerrainMatch:P1}, outside evaluator {EvaluatorMatch:P1} ({terrainTotal} cells, water included), "+
                $"usable {Usable}; rejected=[{string.Join(", ",Metrics.Rejections())}]; currentMapExact={Metrics.Exact}; seed={Context.Seed} ({Context.SeedSource}); initDraws={Context.InitDraws} ({Context.InitDrawsSource}); size={w}x{h}; applied=[{string.Join(",",Context.Applied)}]; gl=[{string.Join(",",Context.Geological)}]; unsupported=[{string.Join(",",Context.Unsupported.Distinct())}]; riverGate={Mutators?.RiverGate??"none"}; top misses [{TopMismatches}]; {Metrics.Summary()}";
#if EH_DEV
            Report+="; "+GenerationReference.Compare(map,Snapshot)+"; steps=["+Context.StepTrace()+"]";
#endif
        }

        private TerrainDef Evaluate(Map map, BiomeDef biome, IntVec3 c, float elev, float fert)
        {
            TerrainDef t = PatchTerrain(biome,c,fert);
            if (t == null)
            {
                if (elev > 0.55f && elev < 0.61f && !biome.noGravel) t = biome.gravelTerrain ?? TerrainDefOf.Gravel;
                else if (elev >= 0.61f) t = RockAt(c.x, c.z)?.building?.naturalTerrain;
            }
            if (t == null) t = TerrainThreshold.TerrainAtValue(biome.terrainsByFertility, fert);
            t ??= TerrainDefOf.Sand;
            bool caves=Snapshot!=null && Snapshot.Covers(c.x,c.z) && Snapshot.Caves[Snapshot.Index(c.x,c.z)]>0;
            bool solid=Snapshot?.SolidReady==true && Snapshot.Covers(c.x,c.z)?Snapshot.Solid[Snapshot.Index(c.x,c.z)]:elev>0.7f;
            if ((solid || caves) && t.supportsRock) t = RockAt(c.x, c.z)?.building?.naturalTerrain ?? t;
            return t;
        }

        internal TerrainDef PatchTerrain(BiomeDef biome,IntVec3 c,float fertility)
        {
            if (!Context.PreventPatches && biome.terrainPatchMakers!=null)
                foreach (TerrainPatchMaker p in biome.terrainPatchMakers)
                {
                    if (Context.PreventPonds && p.isPond) continue;
                    TerrainDef result=Context.PatchAt(p,c,fertility);
                    if (result!=null) return result;
                }
            return null;
        }

        private TerrainDef FinalTerrain(Map map, IntVec3 c, float elev, float fert)
        {
            if (Snapshot?.TerrainReady == true && Snapshot.Covers(c.x,c.z)) return Snapshot.Terrain[Snapshot.Index(c.x,c.z)];
            BiomeDef biome = Context.BiomeAt(c.x, c.z);
            TerrainDef basic = Evaluate(map, biome, c, elev, fert);
            return Mutators?.TerrainAt(c, basic, elev > 0.7f && basic?.supportsRock == true) ?? basic;
        }

        internal TerrainDef BasicTerrain(IntVec3 c, float elev, float fert) => Evaluate(Context.Map, Context.BiomeAt(c.x,c.z), c, elev, fert);

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
                return FinalTerrain(map, c, elev, FertilityAt(c.x, c.z));
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(HorizonLog.Prefix + "Generator terrain for an outside cell failed; the cell uses the mirror. " + ex, 0x45484754);
                return null;
            }
        }
    }
}
