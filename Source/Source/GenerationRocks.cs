using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class GenerationRocks
    {
        internal static IEnumerable<bool> Build(GenReconstruction generator,GenerationSnapshot snapshot)
        {
            GenerationContext context=generator.Context; Map map=context.Map;
            GenerationCaves caves=generator.Mutators?.Caves;
            if (caves!=null) foreach (bool unit in caves.Run(generator,snapshot)) yield return unit;
            int stage=context.StepIndex(typeof(GenStep_RocksFromGrid));
            if (stage<0 || map.TileInfo.WaterCovered) { snapshot.SolidReady=true; yield break; }
            for(int z=-snapshot.Ring;z<snapshot.Height+snapshot.Ring;z++)
            {
                for(int x=-snapshot.Ring;x<snapshot.Width+snapshot.Ring;x++)
                {
                    int i=snapshot.Index(x,z);
                    snapshot.Solid[i]=snapshot.Elevation[i]>0.7f && snapshot.Caves[i]<=0;
                }
                yield return false;
            }
            var step=(GenStep_RocksFromGrid)context.Steps[stage].def.genStep;
            float maximum=(float)AccessTools.Field(typeof(GenStep_RocksFromGrid),"maxMineableValue").GetValue(step);
            bool ignore=(bool)AccessTools.Field(typeof(GenStep_RocksFromGrid),"ignoreMaxIfGravship").GetValue(step);
            float? custom=(float?)AccessTools.Field(typeof(GenStep_RocksFromGrid),"overrideBlotchesPer10kCells").GetValue(step);
            float density=context.MineableDensity??custom??GenStep_RocksFromGrid.GetResourceBlotchesPer10KCellsForMap(map);
            var chooser=new GenStep_ScatterLumpsMineable { maxValue=ModsConfig.OdysseyActive && ignore && map.wasSpawnedViaGravShipLanding?float.MaxValue:maximum };
            var random=new GenerationRandom(context.SeedFor(typeof(GenStep_RocksFromGrid),1182952823));
            int count;
            using(random.Enter())
            {
#if EH_DEV
                GenerationReference.CheckMineralRandom(map);
#endif
                float rate=new FloatRange(density,density).RandomInRange;
                if (!map.IsStartingMap) rate*=Current.Game.storyteller.difficulty.nomadicMineableResourcesFactor;
                count=GenStep_Scatterer.CountFromPer10kCells(rate,map);
            }
            bool tutorial=Current.ProgramState==ProgramState.MapInitializing && Find.Storyteller.def.tutorialMode;
            Func<ThingDef> choose=()=>tutorial?null:(ThingDef)AccessTools.Method(typeof(GenStep_ScatterLumpsMineable),"ChooseThingDef").Invoke(chooser,null);
            foreach (bool unit in Scatter(map,snapshot,count,choose,random)) yield return unit;
            snapshot.SolidReady=true;
            context.Applied.Add("RocksFromGrid mineral footprints (original domain)");
        }

        internal static IEnumerable<bool> Scatter(Map map,GenerationSnapshot snapshot,int count,Func<ThingDef> choose,GenerationRandom random)
        {
            var natural=(bool[])snapshot.Solid.Clone();
            var spots=new List<IntVec3>();
            for(int n=0;n<count;n++)
            {
                bool found;
                using(random.Enter())
                {
                    found=CellFinderLoose.TryFindRandomNotEdgeCellWith(5,c=>natural[snapshot.Index(c.x,c.z)]
                        && !spots.Any(s=>(s-c).LengthHorizontalSquared<=25),map,out IntVec3 center);
                    if(found)
                    {
                        ThingDef ore=choose();
                        if (ore!=null)
                        {
                            int cells=ore.building.mineableScatterLumpSizeRange.RandomInRange;
                            foreach(IntVec3 c in GridShapeMaker.IrregularLump(center,map,cells,c=>snapshot.Caves[snapshot.Index(c.x,c.z)]==0))
                            {
                                int i=snapshot.Index(c.x,c.z);
                                snapshot.Solid[i]=true; natural[i]=ore.building.isNaturalRock;
                            }
                        }
                        spots.Add(center);
                    }
                }
                if(!found) break;
                yield return false;
            }
        }
    }
}
