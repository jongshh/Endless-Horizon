using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class HorizonRegistry
    {
        private static readonly ConditionalWeakTable<Map, HorizonState> states = new ConditionalWeakTable<Map, HorizonState>();
        private static int generation;

        public static bool IsActive(Map map)
        {
            return HorizonActivity.IsActive(map);
        }

        public static HorizonState Get(Map map)
        {
            if (!states.TryGetValue(map, out HorizonState state) || state.Generation != generation)
            {
                state?.Dispose();
                state = new HorizonState(map, generation);
                states.AddOrUpdate(map, state);
            }
            HorizonState ready = state.AdvanceReplacement();
            if (ready != null)
            {
                states.AddOrUpdate(map, ready);
                ready.retiredWork = state.DisposeSteps().GetEnumerator();
                SurfaceTable.Show(map, ready.Surface);
                ready.Extensions.Show(state.Extensions);
                if (state.Domain != null && state.Domain != ready.Domain) EdgeRock.RedrawEdgeRock(map, state.Domain);
                EdgeRock.RedrawEdgeRock(map, ready.Domain);
                state = ready;
            }
            return state;
        }

        public static HorizonState Peek(Map map)
        {
            return map != null && states.TryGetValue(map, out HorizonState state) ? state : null;
        }

        public static void Invalidate(Map map)
        {
            if (map != null && states.TryGetValue(map, out HorizonState state))
            {
                state.Dispose();
                states.Remove(map);
            }
        }

        public static void InvalidateAll()
        {
            generation++;
        }

        internal static void ConnectApi()
        {
            ExtensionMaps.RegionChanged = map => Peek(map)?.RegionChanged();
            ExtensionMaps.DrawMap = Patch_DrawClippers.DrawExternally;
        }
    }
}
