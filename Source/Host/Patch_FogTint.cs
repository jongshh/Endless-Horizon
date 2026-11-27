using System;
using HarmonyLib;
using Verse;

namespace EndlessHorizon
{
    [HarmonyPatch(typeof(SectionLayer_FogOfWar), nameof(SectionLayer_FogOfWar.Regenerate))]
    internal static class Patch_FogLayerRegenerated
    {
        private static readonly AccessTools.FieldRef<SectionLayer, Section> SectionRef =
            AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        public static void Postfix(SectionLayer_FogOfWar __instance)
        {
            try
            {
                Map map = SectionRef(__instance)?.map;
                if (map != null) HorizonRegistry.Peek(map)?.FogRegenerated();
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(HorizonLog.Prefix + "Following the map fog colour failed. " + ex, 0x45484652);
            }
        }
    }
}
