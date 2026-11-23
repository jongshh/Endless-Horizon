using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace EndlessHorizon
{
    internal static class EdgeJoin
    {
        internal const int Band = 24;

        private static readonly Harmony harmony = new Harmony("jongsh.endlesshorizon.extensions");
        private static readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();

        internal static bool Regenerating;

        internal static bool Patch(Type type)
        {
            if (!ExtensionSections.CanJoin) return false;
            MethodInfo regenerate = null;
            for (Type t = type; t != null && regenerate == null; t = t.BaseType)
                regenerate = AccessTools.DeclaredMethod(t, "Regenerate", Type.EmptyTypes);
            if (regenerate == null || regenerate.IsAbstract) return false;
            if (!patched.Add(regenerate)) return true;
            harmony.Patch(regenerate, prefix: new HarmonyMethod(typeof(EdgeJoin), nameof(Prefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(EdgeJoin), nameof(Finalizer)) { priority = Priority.Last });
            return true;
        }

        internal static void Prefix(SectionLayer __instance, out IDisposable __state)
        {
            __state = null;
            if (Regenerating) return;
            try
            {
                LayerRegistration layer = ExtensionRegistry.LayerOf(__instance.GetType());
                if (layer == null || !layer.JoinMapEdge || !ExtensionRegistry.Presents(layer)) return;
                Section section = ExtensionSections.SectionOf(__instance);
                Map map = section?.map;
                HorizonSurface surface = SurfaceTable.Shown(map);
                if (surface == null || !HorizonActivity.Shows(map) || !surface.Domain.AnyInBand(section.CellRect, Band)) return;
                __state = ExtensionScopes.EnterAll(map, layer.Scopes);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(HorizonLog.Prefix + "Joining a map edge section to the outside failed; it is drawn as that mod does alone. " + ex, 0x45484A4E);
            }
        }

        internal static Exception Finalizer(Exception __exception, IDisposable __state)
        {
            __state?.Dispose();
            return __exception;
        }
    }
}
