using System;
using System.Runtime.CompilerServices;
using Verse;

namespace EndlessHorizon
{
    internal static class SurfaceTable
    {
        private static readonly ConditionalWeakTable<Map, HorizonSurface> shown = new ConditionalWeakTable<Map, HorizonSurface>();
        private static HorizonSurface building;

        internal static void Show(Map map, HorizonSurface surface) => shown.AddOrUpdate(map, surface);

        internal static void Hide(Map map, HorizonSurface surface)
        {
            if (shown.TryGetValue(map, out HorizonSurface now) && now == surface) shown.Remove(map);
        }

        internal static HorizonSurface Shown(Map map) => map != null && shown.TryGetValue(map, out HorizonSurface s) ? s : null;

        internal static HorizonSurface Current(Map map)
        {
            HorizonSurface made = building;
            return made != null && made.Map == map ? made : Shown(map);
        }

        internal static BuildScope Building(HorizonSurface surface) => new BuildScope(surface);

        internal static bool IsBuilding(Map map) => building != null && building.Map == map;

        internal readonly struct BuildScope : IDisposable
        {
            private readonly HorizonSurface before;

            internal BuildScope(HorizonSurface surface)
            {
                before = building;
                building = surface;
            }

            public void Dispose() => building = before;
        }
    }
}
