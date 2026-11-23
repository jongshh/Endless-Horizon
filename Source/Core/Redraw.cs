using Verse;

namespace EndlessHorizon
{
    internal static class Redraw
    {
        internal static bool Suppress;
        internal static Map MapBeingRemoved;

        internal static bool Possible(Map map) => map?.mapDrawer != null && !map.Disposed && map != MapBeingRemoved;

        internal static void Mark(Map map, IntVec3 cell, ulong flags, bool adjacent)
        {
            bool before = Suppress;
            Suppress = true;
            try { map.mapDrawer.MapMeshDirty(cell, flags, regenAdjacentCells: adjacent, regenAdjacentSections: false); }
            finally { Suppress = before; }
        }
    }
}
