using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class EdgeRock
    {
        public static MountainMask MaskFor(Map map)
        {
            if (map == null || HorizonDebug.HideRing || !HorizonRegistry.IsActive(map)) return null;
            HorizonState s = HorizonRegistry.Peek(map);
            return s != null && s.Built ? s.Mountains : null;
        }

        public static bool OnEdge(IntVec3 p, Map map) => p.x == 0 || p.z == 0 || p.x == map.Size.x - 1 || p.z == map.Size.z - 1;

        public static Map MapBeingRemoved;

        public static void RedrawEdgeRock(Map map)
        {
            if (map?.mapDrawer == null || map == MapBeingRemoved) return;
            try
            {
                Patch_MapMeshDirty.Suppress = true;
                int w = map.Size.x, h = map.Size.z;
                void Mark(int x, int z)
                {
                    var c = new IntVec3(x, 0, z);
                    Building b = c.GetEdifice(map);
                    if (b != null && b.def.mineable)
                    {
                        map.mapDrawer.MapMeshDirty(c, (ulong)MapMeshFlagDefOf.Things | (ulong)MapMeshFlagDefOf.Buildings);
                    }
                }
                for (int x = 0; x < w; x++) { Mark(x, 0); Mark(x, h - 1); }
                for (int z = 1; z < h - 1; z++) { Mark(0, z); Mark(w - 1, z); }
            }
            catch (Exception ex)
            {
                Log.WarningOnce(EndlessHorizonMod.LogPrefix + "Redrawing the map's edge rock failed: " + ex, 0x45484552);
            }
            finally
            {
                Patch_MapMeshDirty.Suppress = false;
            }
        }
    }

    [HarmonyPatch(typeof(Graphic_LinkedCornerFiller), nameof(Graphic_LinkedCornerFiller.Print))]
    public static class Patch_CornerFillerPrint
    {
        private static readonly float CoverOffsetDist = new Vector2(0.5f, 0.5f).magnitude - new Vector2(0.5f, 0.5f).magnitude * 0.5f;
        private static readonly Vector2[] CornerFillUVs =
        {
            new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f),
        };

        public static bool Prefix(Graphic_LinkedCornerFiller __instance, SectionLayer layer, Thing thing, float extraRotation)
        {
            if (thing == null || !thing.Spawned || !thing.def.mineable || !EdgeRock.OnEdge(thing.Position, thing.Map) || EdgeRock.MaskFor(thing.Map) == null)
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
