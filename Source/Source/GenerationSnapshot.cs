using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class GenerationSnapshot
    {
        internal readonly int Width, Height, Ring, Stride;
        internal readonly float[] Elevation, Fertility, Caves;
        internal readonly BiomeDef[] Biomes;
        internal readonly TerrainDef[] BaseTerrain, Terrain;
        internal readonly RoofDef[] Roofs;
        internal readonly bool[] Solid;
        internal bool SolidReady;
        internal bool OwnsRoof;
        internal bool TerrainReady, OwnsTerrain, OwnsFlow;
        internal bool OwnsGeologicalTerrain;
        internal readonly double[] FlowAlpha, FlowBeta;
        internal bool[] RiverOwned;
        internal bool OwnsVanillaRiverAt(int x, int z) => RiverOwned != null && TerrainReady && Covers(x, z) && RiverOwned[Index(x, z)];
        internal GenerationSnapshot(int width, int height, int ring)
        {
            Width = width; Height = height; Ring = ring; Stride = width + 2 * ring;
            int count = Stride * (height + 2 * ring);
            Elevation = new float[count]; Fertility = new float[count]; Caves = new float[count];
            Biomes = new BiomeDef[count]; BaseTerrain = new TerrainDef[count]; Terrain = new TerrainDef[count];
            Roofs = new RoofDef[count];
            Solid = new bool[count];
            FlowAlpha = new double[count]; FlowBeta = new double[count];
        }
        internal bool Covers(int x, int z) => x >= -Ring && z >= -Ring && x < Width + Ring && z < Height + Ring;
        internal int Index(int x, int z) => (z + Ring) * Stride + x + Ring;

        internal Vector3 WaterMovementAt(int x,int z)
        {
            if (!OwnsFlow || !Covers(x,z)) return Vector3.zero;
            bool inside=x>=0 && z>=0 && x<Width && z<Height;
            int left=inside && x==0?x:Math.Max(x-1,-Ring),right=inside && x==Width-1?x:Math.Min(x+1,Width+Ring-1);
            int down=inside && z==0?z:Math.Max(z-1,-Ring),up=inside && z==Height-1?z:Math.Min(z+1,Height+Ring-1);
            var gradient=new Vector3((float)((FlowAlpha[Index(right,z)]-FlowAlpha[Index(left,z)])/Math.Max(1,right-left)),0,
                (float)((FlowAlpha[Index(x,up)]-FlowAlpha[Index(x,down)])/Math.Max(1,up-down)));
            if (!float.IsFinite(gradient.x)||!float.IsFinite(gradient.z)) return Vector3.zero;
            return gradient.magnitude>1e-4f?gradient.normalized/gradient.magnitude:Vector3.zero;
        }

        internal IEnumerable<bool> BuildVanilla(GenReconstruction generator)
        {
            GenerationContext context=generator.Context;
            for (int z=-Ring;z<Height+Ring;z++)
            {
                for (int x=-Ring;x<Width+Ring;x++)
                {
                    int i=Index(x,z);
                    Elevation[i]=generator.BaseElevationAt(x,z); Fertility[i]=generator.BaseFertilityAt(x,z);
                    Biomes[i]=context.BiomeAt(x,z);
                }
                yield return false;
            }
            generator.Snapshot=this;
            foreach (bool step in GenerationRocks.Build(generator,this)) yield return step;
            foreach (bool step in BuildBaseTerrain(generator)) yield return step;
            Array.Copy(BaseTerrain,Terrain,Terrain.Length);
            var random=new GenerationRandom(context.SeedFor(typeof(GenStep_MutatorPostTerrain),562343345));
            foreach (bool step in ApplyVanillaTerrain(generator,random,generator.Mutators.Workers)) yield return step;
            generator.Mutators.SettleRivers("the PostTerrain stage did not run");
            TerrainReady=true;
            OwnsTerrain=generator.Mutators.TerrainComplete;
        }

        private IEnumerable<bool> BuildBaseTerrain(GenReconstruction generator)
        {
            GenerationContext context=generator.Context;
            var random=new GenerationRandom(context.SeedFor(typeof(GenStep_Terrain),262606459));
            for (int z=0;z<Height;z++)
            {
                using (random.Enter())
                {
                    context.InitializingPatches=true;
                    try
                    {
                        for (int x=0;x<Width;x++)
                        {
                            int i=Index(x,z);
                            BaseTerrain[i]=generator.BasicTerrain(new IntVec3(x,0,z),Elevation[i],Fertility[i]);
                        }
                    }
                    finally { context.InitializingPatches=false; }
                }
                yield return false;
            }
            for (int z=-Ring;z<Height+Ring;z++)
            {
                for (int x=-Ring;x<Width+Ring;x++)
                {
                    if (x>=0 && z>=0 && x<Width && z<Height) continue;
                    int i=Index(x,z);
                    BaseTerrain[i]=generator.BasicTerrain(new IntVec3(x,0,z),Elevation[i],Fertility[i]);
                }
                yield return false;
            }
            context.CleanupPatches(context.Map.Biome.terrainPatchMakers);
        }

        private IEnumerable<bool> ApplyVanillaTerrain(GenReconstruction generator,GenerationRandom random,IEnumerable<TileMutatorWorker> list)
        {
            GenerationContext context=generator.Context;
            if (context.StepIndex(typeof(GenStep_MutatorPostTerrain))<0) yield break;
            generator.Mutators.RockAt??=(x,z)=>generator.Rocks==null?null:generator.RockAt(x,z);
            foreach (TileMutatorWorker worker in list)
            {
                GenerationRiverTerrain river=null;
                GenerationRiverTerrain.Terrains riverTerrains=null;
                if (generator.Mutators.PendingRiver(worker))
                {
                    var plain=(TileMutatorWorker_River)worker;
                    string reason=null;
                    try
                    {
                        using (random.Enter())
                            river=GenerationRiverTerrain.Reproduce(context.Map,Width,Height,plain,ModsConfig.OdysseyActive,
                                c=>Terrain[Index(c.x,c.z)],out reason);
                    }
                    catch (Exception ex)
                    {
                        river=null; reason="reproduction failed ("+ex.GetType().Name+")";
                        context.Unsupported.Add("PostTerrain random stream after: "+worker.GetType().FullName);
                        Log.Error(HorizonLog.Prefix+"Reproducing the vanilla river failed; the continued river is used instead. "+ex);
                    }
                    yield return false;
                    if (river!=null)
                    {
                        bool match=false;
                        try
                        {
                            Map map=context.Map;
                            match=river.MatchesFlow(map.waterInfo?.riverFlowMap,
                                (x,z)=>map.terrainGrid.BaseTerrainAt(new IntVec3(x,0,z))?.IsRiver==true,out reason);
                        }
                        catch (Exception ex)
                        {
                            reason="flow gate failed ("+ex.GetType().Name+")";
                            Log.Error(HorizonLog.Prefix+"Checking the reproduced river against the saved flow map failed; the continued river is used instead. "+ex);
                        }
                        generator.Mutators.RiverGate=(match?"passed, ":"failed, ")+reason;
                        if (!match) { river=null; reason="flow gate: "+reason; }
                        yield return false;
                    }
                    if (river!=null)
                    {
                        riverTerrains=GenerationRiverTerrain.Terrains.Vanilla(context.Map.Biome,context.Map.TileInfo.Mutators,context.BiomeAt);
                        RiverOwned??=new bool[Elevation.Length];
                        generator.Mutators.RiverReproduced(plain);
                    }
                    else generator.Mutators.RiverNotReproduced(plain,reason);
                }
                for (int z=0;z<Height;z++)
                {
                    using (random.Enter())
                    {
                        context.InitializingPatches=true;
                        try
                        {
                            for (int x=0;x<Width;x++) Apply(x,z);
                        }
                        finally { context.InitializingPatches=false; }
                    }
                    yield return false;
                }
                for (int z=-Ring;z<Height+Ring;z++)
                {
                    for (int x=-Ring;x<Width+Ring;x++)
                        if (x<0 || z<0 || x>=Width || z>=Height) Apply(x,z);
                    yield return false;
                }
                if (worker is TileMutatorWorker_Patches) context.CleanupPatches(worker.def.terrainPatchMakers);

                void Apply(int x,int z)
                {
                    if (river!=null) { ApplyRiver(river,riverTerrains,x,z); return; }
                    int i=Index(x,z); var c=new IntVec3(x,0,z);
                    bool rock=Solid[i] && BaseTerrain[i]?.supportsRock==true;
                    Terrain[i]=generator.Mutators.TerrainAt(worker,c,Terrain[i],rock);
                }
            }
        }

        internal void ApplyRiver(GenerationRiverTerrain river,GenerationRiverTerrain.Terrains terrains,int x,int z)
        {
            int i=Index(x,z);
            TerrainDef existing=Terrain[i];
            if (existing==null) return;
            GenerationRiverTerrain.Cell cell=river.At(x,z,existing,Solid[i],terrains);
            if (cell.Depth>float.MinValue && RiverOwned!=null) RiverOwned[i]=true;
            Terrain[i]=cell.Terrain??existing;
            if (cell.River) Elevation[i]=-1f;
            if (cell.ClearsEdifice && Solid[i]) Solid[i]=false;
            if (cell.RemovesRoof) Roofs[i]=null;
        }

        internal IEnumerable<bool> BuildGeological(GenReconstruction generator, GenerationGeological gl)
        {
            GenerationContext context = generator.Context;
            generator.Mutators.SettleRivers("not reproduced on a Geological Landforms map");
            gl.ElevationInput = (x,z) => generator.ElevationBeforeGeologicalAt((int)Math.Round(x), (int)Math.Round(z));
            gl.FertilityInput = (x,z) => generator.BaseFertilityAt((int)Math.Round(x), (int)Math.Round(z));
            gl.CavesInput = (x,z) => 0;
            gl.TerrainInput = (x,z) => null;
            gl.BiomeInput = (x,z) => context.BaseBiomeAt((int)Math.Round(x), (int)Math.Round(z));
            Func<double,double,double> elev, fert, caves;
            Func<double,double,BiomeDef> biomes;
            Func<double,double,double> continuedElevation = gl.ElevationInput, continuedFertility = gl.FertilityInput, continuedCaves = gl.CavesInput;
            if (gl.FiniteRiver)
            {
                gl.ElevationInput = (x,z) => OnMap(x,z) ? continuedElevation(x,z) : 1d;
                gl.FertilityInput = (x,z) => OnMap(x,z) ? continuedFertility(x,z) : 0d;
                gl.CavesInput = (x,z) => 0d;
            }
            gl.Enter();
            try
            {
                context.MineableDensity = gl.Mineables();
                elev = gl.Output<double>("OutputElevation", "Get"); fert = gl.Output<double>("OutputFertility", "Get");
                caves = gl.Output<double>("OutputCaves", "Get");
                if (gl.FiniteRiver)
                {
                    GenerationGeological.Value(elev, Width/2, Height/2, 0d);
                    GenerationGeological.Value(fert, Width/2, Height/2, 0d);
                    GenerationGeological.Value(caves, Width/2, Height/2, 0d);
                }
            }
            finally
            {
                gl.Exit();
                gl.ElevationInput = continuedElevation; gl.FertilityInput = continuedFertility; gl.CavesInput = continuedCaves;
            }
            for (int stage=0;stage<3;stage++)
            {
                gl.Stage=stage==0?"elevation cells":stage==1?"fertility cells":"caves cells";
                for (int z=-Ring;z<Height+Ring;z++)
                {
                    gl.Enter(false);
                    try
                    {
                        for (int x=-Ring;x<Width+Ring;x++)
                        {
                            int i=Index(x,z);
                            if(stage==0) Elevation[i]=(float)GenerationGeological.Value(elev,x,z,(double)generator.ElevationBeforeGeologicalAt(x,z));
                            else if(stage==1) Fertility[i]=(float)GenerationGeological.Value(fert,x,z,(double)generator.BaseFertilityAt(x,z));
                            else Caves[i]=Elevation[i]>0.7f?(float)GenerationGeological.Value(caves,x,z,0d):0;
                        }
                    }
                    finally { gl.Exit(); }
                    yield return false;
                }
                if(stage==0) gl.ElevationInput=(x,z)=>Sample(Elevation,x,z,generator.ElevationBeforeGeologicalAt);
                else if(stage==1) gl.FertilityInput=(x,z)=>Sample(Fertility,x,z,generator.BaseFertilityAt);
                else gl.CavesInput=(x,z)=>Sample(Caves,x,z,(_,_)=>0);
            }
            for (int z=-Ring;z<Height+Ring;z++)
            {
                for (int x=-Ring;x<Width+Ring;x++)
                {
                    int i=Index(x,z);
                    Elevation[i]=generator.Mutators.ElevationAfterGeological(new IntVec3(x,0,z),Elevation[i]);
                }
                yield return false;
            }
            gl.Enter(false);
            try { biomes=gl.BiomeOutput(); }
            finally { gl.Exit(); }
            for(int z=-Ring;z<Height+Ring;z++)
            {
                gl.Enter(false);
                try
                {
                    for(int x=-Ring;x<Width+Ring;x++) Biomes[Index(x,z)]=biomes==null?context.BaseBiomeAt(x,z):biomes(x,z)??context.Map.Biome;
                }
                finally { gl.Exit(); }
                yield return false;
            }
            if (gl.TransitionsApplied && gl.PostTransitions(out float root,out float pass))
                foreach (bool step in GenerationBiomeTransition.Process(context,this,root,pass,generator.Mutators.ElevationRandom)) yield return step;
            context.ExternalBiome = (x,z) => Covers(x,z) ? Biomes[Index(x,z)] : context.BaseBiomeAt(x,z);
            gl.BiomeInput=(x,z)=>context.BiomeAt((int)Math.Round(x),(int)Math.Round(z));
            generator.Snapshot = this;
            foreach (bool step in GenerationRocks.Build(generator,this)) yield return step;
            foreach (bool step in BuildBaseTerrain(generator)) yield return step;
            Array.Copy(BaseTerrain,Terrain,Terrain.Length);
            var random=new GenerationRandom(context.SeedFor(typeof(GenStep_MutatorPostTerrain),562343345));
            foreach (bool step in ApplyVanillaTerrain(generator,random,generator.Mutators.WorkersBeforeGeological.ToList())) yield return step;
            TerrainDef[] previousTerrain=(TerrainDef[])Terrain.Clone();
            gl.TerrainInput = (x,z) => Covers((int)Math.Round(x),(int)Math.Round(z)) ? previousTerrain[Index((int)Math.Round(x),(int)Math.Round(z))] : null;
            Func<double,double,TerrainDef> basis, stone, cave, river;
            Func<double,double,double> alpha, beta;
            Func<double,double,RoofDef> roof;
            gl.Enter(false);
            try
            {
                basis = gl.Output<TerrainDef>("OutputTerrain", "GetBase"); stone = gl.Output<TerrainDef>("OutputTerrain", "GetStone");
                cave = gl.Output<TerrainDef>("OutputTerrain", "GetCave"); river = gl.Output<TerrainDef>("OutputWaterFlow", "GetRiverTerrain");
                alpha = gl.Output<double>("OutputWaterFlow", "GetFlowAlpha"); beta = gl.Output<double>("OutputWaterFlow", "GetFlowBeta");
                roof = gl.Output<RoofDef>("OutputRoofGrid", "Get");
            }
            finally { gl.Exit(); }
            OwnsGeologicalTerrain = basis != null || stone != null || cave != null || river != null;
            OwnsTerrain = generator.Mutators.TerrainComplete && !gl.FiniteRiver;
            OwnsFlow = alpha != null && beta != null && OwnsGeologicalTerrain && !gl.FiniteRiver;
            OwnsRoof = roof != null;
            gl.Stage="terrain cells";
            for (int phase=0;phase<2;phase++)
            for (int x=phase==0?0:-Ring;x<(phase==0?Width:Width+Ring);x++)
            {
                IDisposable rng=random.Enter();
                try
                {
                    gl.Enter(false);
                    context.InitializingPatches=phase==0;
                    for (int z=phase==0?0:-Ring;z<(phase==0?Height:Height+Ring);z++)
                    {
                        if (phase==1 && x>=0 && z>=0 && x<Width && z<Height) continue;
                        int i = Index(x,z);
                        TerrainDef rt = GenerationGeological.Value<TerrainDef>(river,x,z,null);
                        bool rock=Solid[i] && BaseTerrain[i]?.supportsRock==true;
                        TerrainDef t=null;
                        if (rt!=null || !rock)
                        {
                            if (rt==null && Caves[i]>0) t=GenerationGeological.Value<TerrainDef>(cave,x,z,null);
                            else
                            {
                                t=gl.Priority(GenerationGeological.Value<TerrainDef>(basis,x,z,null),rt);
                                if (t==null && stone!=null)
                                    t=generator.PatchTerrain(Biomes[i],new IntVec3(x,0,z),Fertility[i])
                                        ?? GenerationGeological.Value<TerrainDef>(stone,x,z,null)
                                        ?? TerrainThreshold.TerrainAtValue(Biomes[i].terrainsByFertility,Fertility[i]);
                            }
                        }
                        Terrain[i] = t ?? Terrain[i];
                        FlowAlpha[i] = GenerationGeological.Value(alpha,x,z,0d);
                        FlowBeta[i] = GenerationGeological.Value(beta,x,z,0d);
                        Roofs[i] = GenerationGeological.Value<RoofDef>(roof,x,z,null);
                    }
                }
                finally { context.InitializingPatches=false; try { gl.Exit(); } finally { rng.Dispose(); } }
                yield return false;
            }
            foreach (bool step in ApplyVanillaTerrain(generator,random,generator.Mutators.WorkersAfterGeological.ToList())) yield return step;
            TerrainReady=true;
            foreach (string graph in context.Geological) context.Applied.Add("GL:"+graph.Substring(0,graph.IndexOf('(')));
            if (gl.TransitionsApplied) context.Applied.Add("GL transitions");

            bool OnMap(double x,double z)
            {
                int cx=(int)Math.Round(x), cz=(int)Math.Round(z);
                return cx>=0 && cz>=0 && cx<Width && cz<Height;
            }
            double Sample(float[] field,double x,double z,Func<int,int,float> fallback)
            {
                int ix=(int)Math.Round(x),iz=(int)Math.Round(z);
                if(Covers(ix,iz)) return field[Index(ix,iz)];
                if(!context.Unsupported.Contains("GL stage input beyond computed domain")) context.Unsupported.Add("GL stage input beyond computed domain");
                return fallback(ix,iz);
            }
        }
    }
}
