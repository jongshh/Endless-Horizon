using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;
using RimWorld;

namespace EndlessHorizon
{
    internal static class GenerationStableSeed
    {
        internal static Func<TerrainPatchMaker, int> For(Map map, int mapSeed, List<string> unsupported)
        {
            try
            {
                Type api = AccessTools.TypeByName("MapPreview.MapPreviewAPI");
                if (api == null || map.Tile.tileId < 0) return null;
                object condition = AccessTools.Field(api, "ShouldUseStableSeed")?.GetValue(null);
                if (condition == null) return null;
                if (!(bool)AccessTools.Method(condition.GetType(), "Apply", new[] { typeof(Map) }).Invoke(condition, new object[] { map })) return null;
                int original = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode());
                int seed = GenerationGeological.ReadMapSeed(Find.World.components, map.Tile, original);
                if (seed != original) unsupported.Add("MapPreview reroll: displacement noise seeds");
                List<TerrainPatchMaker> primary = map.Biome.terrainPatchMakers;
                MethodInfo hash = AccessTools.Method(AccessTools.TypeByName("MapPreview.Patches.Patch_RimWorld_TerrainPatchMaker"),
                    "GetHashCode", new[] { typeof(TerrainPatchMaker) });
                return maker => Seed(seed, primary.IndexOf(maker), () => (int)hash.Invoke(null, new object[] { maker }));
            }
            catch (Exception ex)
            {
                unsupported.Add("MapPreview stable patch seeds");
                Log.Error(HorizonLog.Prefix + "Reading MapPreview's patch seeds failed; terrain patches may not match. " + ex);
                return null;
            }
        }

        internal static int Seed(int mapSeed, int indexInPrimary, Func<int> hash) =>
            indexInPrimary >= 0 ? mapSeed ^ (9305 + indexInPrimary) : mapSeed ^ hash();
    }
}
