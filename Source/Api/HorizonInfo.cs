using System;
using RimWorld;
using Verse;

namespace EndlessHorizon.Api
{
    public static class HorizonInfo
    {
        public static int Version => 1;

        public static bool Has(string id)
        {
            try { return ExtensionRegistry.Has(id); }
            catch (Exception ex) { ApiSupport.Failed(nameof(Has), ex); return false; }
        }

        public static bool IsActive(Map map)
        {
            try { return map != null && HorizonActivity.Shows(map); }
            catch (Exception ex) { ApiSupport.Failed(nameof(IsActive), ex); return false; }
        }

        public static bool IsBuilt(Map map)
        {
            try { return SurfaceTable.Shown(map) != null; }
            catch (Exception ex) { ApiSupport.Failed(nameof(IsBuilt), ex); return false; }
        }

        public static bool IsBuilding(Map map)
        {
            try { return map != null && SurfaceTable.IsBuilding(map); }
            catch (Exception ex) { ApiSupport.Failed(nameof(IsBuilding), ex); return false; }
        }

        public static int RingWidth(Map map)
        {
            try
            {
                if (map == null || !HorizonActivity.IsActive(map)) return 0;
                return SurfaceTable.Current(map)?.Ring ?? HorizonActivity.RingWidth;
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(RingWidth), ex); return 0; }
        }

        public static int FogStart(Map map)
        {
            try
            {
                if (map == null || !HorizonActivity.IsActive(map)) return 0;
                return SurfaceTable.Current(map)?.FogStart ?? HorizonSettings.Current.fogStart;
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(FogStart), ex); return 0; }
        }

        public static bool FogShown(Map map)
        {
            try { return map != null && HorizonActivity.Shows(map) && HorizonSettings.Current.fog && !HorizonDebug.HideFog; }
            catch (Exception ex) { ApiSupport.Failed(nameof(FogShown), ex); return false; }
        }

        public static int Revision(Map map, string channel)
        {
            try { return map == null ? -1 : ExtensionMaps.Revision(map, channel); }
            catch (Exception ex) { ApiSupport.Failed(nameof(Revision), ex); return -1; }
        }

        public static IDisposable Observe(string owner, Action<Map, string, CellRect> changed)
        {
            try { return changed != null && !owner.NullOrEmpty() ? ExtensionMaps.Observe(owner, changed) : null; }
            catch (Exception ex) { ApiSupport.Failed(nameof(Observe), ex); return null; }
        }

        public static void RequestRefresh(Map map, string owner, CellRect area)
        {
            try
            {
                if (map != null && owner != null) ExtensionMaps.RingOf(map)?.Request(owner, area);
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(RequestRefresh), ex); }
        }

        public static void RedrawMap(Map map, CellRect area, ulong meshFlags)
        {
            try
            {
                if (!Redraw.Possible(map) || meshFlags == 0) return;
                CellRect clipped = HorizonDomain.Of(map, HorizonActivity.RingWidth).InMapPart(area);
                if (clipped.maxX < clipped.minX || clipped.maxZ < clipped.minZ) return;
                int size = Section.Size;
                for (int x = HorizonDomain.FloorTo(clipped.minX, size); x <= clipped.maxX; x += size)
                    for (int z = HorizonDomain.FloorTo(clipped.minZ, size); z <= clipped.maxZ; z += size)
                        Redraw.Mark(map, new IntVec3(x, 0, z), meshFlags, adjacent: false);
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(RedrawMap), ex); }
        }

        public static void Report(string owner, string key, string value)
        {
            try
            {
                if (!owner.NullOrEmpty() && !key.NullOrEmpty()) ExtensionMaps.Report(owner, key, value);
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(Report), ex); }
        }
    }
}
