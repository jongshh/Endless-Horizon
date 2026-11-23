#if EH_DEV
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace EndlessHorizon
{
    internal static class GenerationReference
    {
        internal static readonly bool Enabled=Environment.GetEnvironmentVariable("ENDLESS_HORIZON_GENERATION_REFERENCE")=="1";
        private sealed class Reference
        {
            internal float[] Elevation,Fertility,Caves;
            internal BiomeDef[] Biomes;
            internal TerrainDef[] Basis,Final;
            internal bool[] Solid;
            internal object MineralState;
            internal bool? MineralRngAligned;
        }
        private static readonly ConditionalWeakTable<Map,Reference> references=new();
        internal static ulong MapFingerprint(Map map)
        {
            ulong hash=14695981039346656037UL;
            void Mix(int value) { unchecked { hash=(hash^(uint)value)*1099511628211UL; } }
            foreach (IntVec3 c in map.AllCells)
            {
                Mix(RuntimeHelpers.GetHashCode(map.terrainGrid.TerrainAt(c)));
                Mix(RuntimeHelpers.GetHashCode(map.terrainGrid.BaseTerrainAt(c)));
                Mix(RuntimeHelpers.GetHashCode(map.BiomeAt(c)));
                Mix(RuntimeHelpers.GetHashCode(c.GetRoof(map)));
                Mix(map.fogGrid.IsFogged(c)?1:0);
            }
            foreach (Thing thing in map.listerThings.AllThings)
            {
                Mix(thing.thingIDNumber);Mix(thing.Position.x);Mix(thing.Position.z);Mix(thing.stackCount);
            }
            return hash;
        }
        internal static void BeforeTerrain(Map map)
        {
            if (!Enabled || MapGenerator.mapBeingGenerated!=map) return;
            int n=map.cellIndices.NumGridCells;
            Reference r=references.GetValue(map,_=>new Reference());
            r.Elevation=new float[n];r.Fertility=new float[n];r.Caves=new float[n];r.Biomes=new BiomeDef[n];r.Solid=new bool[n];
            for (int i=0;i<n;i++)
            {
                IntVec3 c=map.cellIndices.IndexToCell(i);
                r.Elevation[i]=MapGenerator.Elevation[c]; r.Fertility[i]=MapGenerator.Fertility[c];
                r.Caves[i]=MapGenerator.Caves[c]; r.Biomes[i]=map.BiomeAt(c);
                r.Solid[i]=c.GetEdifice(map)?.def.Fillage==FillCategory.Full;
            }
        }
        internal static void BeforeMinerals(Map map)
        {
            if (!Enabled || MapGenerator.mapBeingGenerated!=map) return;
            Reference r=references.GetValue(map,_=>new Reference());
            r.MineralState ??= AccessTools.Property(typeof(Rand),"StateCompressed").GetValue(null);
        }
        internal static void CheckMineralRandom(Map map)
        {
            if (Enabled && references.TryGetValue(map,out Reference r) && r.MineralState!=null)
                r.MineralRngAligned=Equals(r.MineralState,AccessTools.Property(typeof(Rand),"StateCompressed").GetValue(null));
        }
        internal static void AfterTerrain(Map map,bool final)
        {
            if (!Enabled || MapGenerator.mapBeingGenerated!=map || !references.TryGetValue(map,out Reference r)) return;
            TerrainDef[] result=map.AllCells.Select(c=>map.terrainGrid.TerrainAt(c)).ToArray();
            if (final) r.Final=result; else r.Basis=result;
        }
        internal static string Compare(Map map,GenerationSnapshot snapshot)
        {
            if (!references.TryGetValue(map,out Reference r) || r.Elevation==null) return "reference=not captured";
            if (snapshot==null) return "reference=no replay snapshot";
            int e=0,f=0,caves=0,biomes=0,basis=0,final=0,classes=0,solid=0;
            float maxE=0,maxF=0,maxC=0;
            for (int z=0;z<snapshot.Height;z++) for (int x=0;x<snapshot.Width;x++)
            {
                int i=z*snapshot.Width+x,j=snapshot.Index(x,z);
                float de=Math.Abs(r.Elevation[i]-snapshot.Elevation[j]),df=Math.Abs(r.Fertility[i]-snapshot.Fertility[j]),dc=Math.Abs(r.Caves[i]-snapshot.Caves[j]);
                maxE=Math.Max(maxE,de);maxF=Math.Max(maxF,df);maxC=Math.Max(maxC,dc);
                if (!float.IsFinite(de)||de>1e-5f) e++; if (!float.IsFinite(df)||df>1e-5f) f++; if (!float.IsFinite(dc)||dc>1e-5f) caves++;
                if ((r.Elevation[i]>0.7f)!=(snapshot.Elevation[j]>0.7f) || (r.Caves[i]>0)!=(snapshot.Caves[j]>0)) classes++;
                if (r.Biomes[i]!=snapshot.Biomes[j]) biomes++;
                if (r.Basis!=null && r.Basis[i]!=snapshot.BaseTerrain[j]) basis++;
                if (r.Final!=null && r.Final[i]!=snapshot.Terrain[j]) final++;
                if (r.Solid[i]!=snapshot.Solid[j]) solid++;
            }
            return $"reference errors e/f/caves/biome/base/final/class/solid={e}/{f}/{caves}/{biomes}/{basis}/{final}/{classes}/{solid}; maxAbs={maxE:G5}/{maxF:G5}/{maxC:G5}; terrainCaptured={r.Basis!=null}/{r.Final!=null}; mineralRngAligned={r.MineralRngAligned?.ToString()??"not captured"}; liveMapReadOnly=checked";
        }
    }

    [HarmonyPatch(typeof(GenStep_Terrain),nameof(GenStep_Terrain.Generate))]
    internal static class Patch_GenerationTerrainReference
    {
        internal static void Prefix(Map map)=>GenerationReference.BeforeTerrain(map);
        internal static void Postfix(Map map)=>GenerationReference.AfterTerrain(map,false);
    }
    [HarmonyPatch(typeof(GenStep_MutatorPostTerrain),nameof(GenStep_MutatorPostTerrain.Generate))]
    internal static class Patch_GenerationFinalReference
    {
        internal static void Postfix(Map map)=>GenerationReference.AfterTerrain(map,true);
    }
    [HarmonyPatch(typeof(GenStep_ScatterLumpsMineable),nameof(GenStep_ScatterLumpsMineable.Generate))]
    internal static class Patch_GenerationMineralReference
    {
        [HarmonyPriority(Priority.Last)]
        internal static void Prefix(Map map)=>GenerationReference.BeforeMinerals(map);
    }
}
#endif
