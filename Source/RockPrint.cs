using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class RockPrint
    {
        public static bool Enabled = true;

        private static Thing activeSource;
        private static IntVec3 activeTarget;
        private static MountainMask activeMask;
        private static readonly HashSet<ThingDef> failedDefs = new HashSet<ThingDef>();
        private static readonly List<int> before = new List<int>();
        public static int Printed, Fallbacks;

        public static bool TryPrint(SectionLayer layer, MountainMask mask, Thing source, IntVec3 target)
        {
            if (!Enabled || source == null || failedDefs.Contains(source.def))
            {
                return false;
            }
            before.Clear();
            for (int m = 0; m < layer.subMeshes.Count; m++)
            {
                before.Add(layer.subMeshes[m].verts.Count);
            }
            activeSource = source;
            activeTarget = target;
            activeMask = mask;
            try
            {
                source.Print(layer);
            }
            catch (Exception ex)
            {
                failedDefs.Add(source.def);
                Log.Warning($"{EndlessHorizonMod.LogPrefix}Printing rock {source.def.defName} outside the map failed; using the built-in rock print for it.\n{ex}");
                for (int m = 0; m < layer.subMeshes.Count; m++)
                {
                    Truncate(layer.subMeshes[m], m < before.Count ? before[m] : 0);
                }
                return false;
            }
            finally
            {
                activeSource = null;
                activeMask = null;
            }
            var delta = new Vector3(target.x - source.Position.x, 0f, target.z - source.Position.z);
            for (int m = 0; m < layer.subMeshes.Count; m++)
            {
                List<Vector3> verts = layer.subMeshes[m].verts;
                for (int v = m < before.Count ? before[m] : 0; v < verts.Count; v++)
                {
                    verts[v] += delta;
                }
            }
            Printed++;
            return true;
        }

        internal static bool Redirect(IntVec3 c, Thing parent, ref bool result)
        {
            if (activeSource == null || parent != activeSource)
            {
                if (parent != null && parent.Spawned && parent.def.mineable && !c.InBounds(parent.Map))
                {
                    MountainMask mask = EdgeRock.MaskFor(parent.Map);
                    if (mask != null)
                    {
                        result = mask.IsRock(c.x, c.z);
                        return false;
                    }
                }
                return true;
            }
            IntVec3 o = c - activeSource.Position + activeTarget;
            result = activeMask.IsRock(o.x, o.z);
            return false;
        }

        private static void Truncate(LayerSubMesh sm, int vertCount)
        {
            void Cut<T>(List<T> list)
            {
                if (list.Count > vertCount) list.RemoveRange(vertCount, list.Count - vertCount);
            }
            Cut(sm.verts);
            Cut(sm.colors);
            Cut(sm.uvs);
            sm.tris.RemoveAll(i => i >= vertCount);
        }
    }

    [HarmonyPatch]
    public static class Patch_ShouldLinkWith
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var types = new List<Type> { typeof(Graphic_Linked) };
            types.AddRange(GenTypes.AllSubclasses(typeof(Graphic_Linked)));
            foreach (Type t in types.Distinct())
            {
                MethodInfo m = AccessTools.DeclaredMethod(t, "ShouldLinkWith", new[] { typeof(IntVec3), typeof(Thing) });
                if (m != null && !m.IsAbstract)
                {
                    yield return m;
                }
            }
        }

        public static bool Prefix(IntVec3 __0, Thing __1, ref bool __result) => RockPrint.Redirect(__0, __1, ref __result);
    }
}
