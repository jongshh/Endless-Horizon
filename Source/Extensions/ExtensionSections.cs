using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace EndlessHorizon
{
    internal static class ExtensionSections
    {
        private static readonly FieldInfo SectionMap = AccessTools.Field(typeof(Section), "map");
        private static readonly FieldInfo SectionRect = AccessTools.Field(typeof(Section), "calculatedRect");
        private static readonly FieldInfo SectionLayers = AccessTools.Field(typeof(Section), "layers");
        private static readonly FieldInfo SectionDynamic = AccessTools.Field(typeof(Section), "dynamic");
        private static readonly FieldInfo LayerSection = AccessTools.Field(typeof(SectionLayer), "section");
        private static readonly FieldInfo DrawerSections = AccessTools.Field(typeof(MapDrawer), "sections");

        internal static bool Ready => SectionMap != null && SectionRect != null && SectionLayers != null;

        internal static bool CanJoin => Ready && LayerSection != null && DrawerSections != null;

        internal static Section StandIn(Map map, CellRect rect)
        {
            var s = (Section)RuntimeHelpers.GetUninitializedObject(typeof(Section));
            SectionMap.SetValue(s, map);
            s.botLeft = new IntVec3(rect.minX, 0, rect.minZ);
            SectionRect.SetValue(s, rect);
            SectionLayers.SetValue(s, new List<SectionLayer>());
            SectionDynamic?.SetValue(s, Activator.CreateInstance(SectionDynamic.FieldType));
            return s;
        }

        internal static Section SectionOf(SectionLayer layer) => LayerSection?.GetValue(layer) as Section;

        internal static Section[,] SectionsOf(Map map) => map.mapDrawer == null ? null : DrawerSections?.GetValue(map.mapDrawer) as Section[,];

        internal static List<SectionLayer> LayersOf(Section section) => SectionLayers.GetValue(section) as List<SectionLayer>;

        internal static int Gap(CellRect a, CellRect b)
        {
            int dx = Math.Max(0, Math.Max(a.minX - b.maxX, b.minX - a.maxX));
            int dz = Math.Max(0, Math.Max(a.minZ - b.maxZ, b.minZ - a.maxZ));
            return Math.Max(dx, dz);
        }

        private static readonly ConditionalWeakTable<SectionLayer, object> freed = new ConditionalWeakTable<SectionLayer, object>();

        internal static void Free(SectionLayer layer)
        {
            if (layer == null || layer.subMeshes == null) return;
            if (freed.TryGetValue(layer, out _)) return;
            freed.Add(layer, null);
            layer.subMeshes.RemoveAll(sm => sm == null || sm.mesh == null);
            try { layer.Dispose(); }
            catch (Exception ex)
            {
                string owner = ExtensionRegistry.LayerOf(layer.GetType())?.Owner ?? "unknown owner";
                Log.ErrorOnce($"{HorizonLog.Prefix}Disposing {layer.GetType().FullName} ({owner}) failed; its sub-meshes are destroyed directly. {ex}",
                    layer.GetType().GetHashCode() ^ 0x45484446);
                foreach (LayerSubMesh sm in layer.subMeshes)
                    if (sm.mesh != null) UnityEngine.Object.Destroy(sm.mesh);
                layer.subMeshes.Clear();
            }
        }
    }
}
