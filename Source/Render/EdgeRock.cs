using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class EdgeRock
    {
        public static HorizonSurface SurfaceFor(Map map) => map != null && HorizonActivity.Shows(map) ? SurfaceTable.Shown(map) : null;

        public static void RedrawEdgeRock(Map map, HorizonDomain domain)
        {
            if (map?.mapDrawer == null || map == Redraw.MapBeingRemoved) return;
            try
            {
                Redraw.Suppress = true;
                void Mark(int x, int z)
                {
                    var c = new IntVec3(x, 0, z);
                    Building b = c.GetEdifice(map);
                    if (b != null && b.def.mineable)
                    {
                        map.mapDrawer.MapMeshDirty(c, (ulong)MapMeshFlagDefOf.Things | (ulong)MapMeshFlagDefOf.Buildings);
                    }
                }
                foreach (int i in domain.Edge) Mark(i % domain.W, i / domain.W);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(HorizonLog.Prefix + "Redrawing the map's edge rock failed: " + ex, 0x45484552);
            }
            finally
            {
                Redraw.Suppress = false;
            }
        }
    }

    [HarmonyPatch(typeof(Graphic_LinkedCornerFiller), nameof(Graphic_LinkedCornerFiller.Print))]
    internal static class Patch_CornerFillerPrint
    {
        private static readonly float CoverOffsetDist = new Vector2(0.5f, 0.5f).magnitude - new Vector2(0.5f, 0.5f).magnitude * 0.5f;
        private static readonly Vector2[] CornerFillUVs =
        {
            new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f),
        };

        public static bool Prefix(Graphic_LinkedCornerFiller __instance, SectionLayer layer, Thing thing, float extraRotation)
        {
            if (thing == null || !thing.Spawned || !thing.def.mineable || EdgeRock.SurfaceFor(thing.Map)?.Domain.OnEdge(thing.Position) != true)
            {
                return true;
            }
            Material mat = __instance.MatSingleFor(thing);
            Printer_Plane.PrintPlane(layer, thing.TrueCenter(), new Vector2(1f, 1f), mat, extraRotation);
            IntVec3 p = thing.Position;
            bool W = __instance.ShouldLinkWith(p + IntVec3.West, thing), E = __instance.ShouldLinkWith(p + IntVec3.East, thing);
            bool N = __instance.ShouldLinkWith(p + IntVec3.North, thing), S = __instance.ShouldLinkWith(p + IntVec3.South, thing);
            for (int i = 0; i < 4; i++)
            {
                IntVec3 c = p + GenAdj.DiagonalDirectionsAround[i];
                bool ok = __instance.ShouldLinkWith(c, thing) && (i == 0 ? W && S : i == 1 ? W && N : i == 2 ? E && N : E && S);
                if (!ok) continue;
                Vector3 center = thing.DrawPos + GenAdj.DiagonalDirectionsAround[i].ToVector3().normalized * CoverOffsetDist + Altitudes.AltIncVect + new Vector3(0f, 0f, 0.09f);
                Printer_Plane.PrintPlane(layer, center, new Vector2(0.5f, 0.5f), mat, extraRotation, false, CornerFillUVs);
            }
            return false;
        }
    }
}
