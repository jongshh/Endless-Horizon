using System.Collections.Generic;
using RimWorld;
using Verse;

namespace EndlessHorizon
{
    internal static class OutsideSources
    {
        private static readonly HashSet<ThingDef> failedDefs = new HashSet<ThingDef>();

        internal static void PrintFailed(ThingDef def) => failedDefs.Add(def);

        internal static bool IsShown(Thing t)
        {
            ThingDef def = t.def;
            bool meshDrawn = def.drawerType == DrawerType.MapMeshOnly || def.drawerType == DrawerType.MapMeshAndRealTime;
            if (!meshDrawn && !(def.category == ThingCategory.Plant && def.drawerType != DrawerType.None))
            {
                return false;
            }
            if (t is Plant sownPlant && sownPlant.sown)
            {
                return false;
            }
            if (def.size.x != 1 || def.size.z != 1 || failedDefs.Contains(def))
            {
                return false;
            }
            if (def.category == ThingCategory.Plant)
            {
                return true;
            }
            if (def.mineable)
            {
                return false;
            }
            return def.thingCategories != null && (def.thingCategories.Contains(ThingCategoryDefOf.Chunks) || def.thingCategories.Contains(ThingCategoryDefOf.StoneChunks));
        }
    }
}
