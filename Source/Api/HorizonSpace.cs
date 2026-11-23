using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace EndlessHorizon.Api
{
    public static class HorizonSpace
    {
        public static CellRect Bounds(Map map)
        {
            try { return ApiSupport.DomainOf(map)?.Bounds ?? CellRect.Empty; }
            catch (Exception ex) { ApiSupport.Failed(nameof(Bounds), ex); return CellRect.Empty; }
        }

        public static int DrawExtent(Map map)
        {
            try
            {
                if (map == null || !HorizonActivity.IsActive(map)) return 0;
                HorizonDomain domain = ApiSupport.DomainOf(map);
                return domain.R + FogRamp.MaskMargin + domain.FurthestInMap();
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(DrawExtent), ex); return 0; }
        }

        public static bool IsPlayable(Map map, IntVec3 c)
        {
            try { return ApiSupport.DomainOf(map)?.Inside(c) == true; }
            catch (Exception ex) { ApiSupport.Failed(nameof(IsPlayable), ex); return false; }
        }

        public static bool IsOutside(Map map, IntVec3 c)
        {
            try { return ApiSupport.DomainOf(map)?.Drawn(c) == true; }
            catch (Exception ex) { ApiSupport.Failed(nameof(IsOutside), ex); return false; }
        }

        public static IntVec3 NearestPlayable(Map map, IntVec3 c)
        {
            try { return ApiSupport.DomainOf(map)?.Nearest(c) ?? IntVec3.Invalid; }
            catch (Exception ex) { ApiSupport.Failed(nameof(NearestPlayable), ex); return IntVec3.Invalid; }
        }

        public static float DistanceFromPlayable(Map map, IntVec3 c)
        {
            try
            {
                HorizonDomain domain = ApiSupport.DomainOf(map);
                if (domain == null) return float.NaN;
                return domain.Inside(c) ? 0f : domain.Distance(c.x, c.z);
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(DistanceFromPlayable), ex); return float.NaN; }
        }

        public static IReadOnlyList<IntVec3> BoundaryCells(Map map)
        {
            try { return ApiSupport.DomainOf(map)?.BoundaryList ?? Array.Empty<IntVec3>(); }
            catch (Exception ex) { ApiSupport.Failed(nameof(BoundaryCells), ex); return Array.Empty<IntVec3>(); }
        }

        public static bool SetRegion(Map map, string owner, Predicate<IntVec3> playable, IReadOnlyList<Vector2> drawArea)
        {
            try
            {
                if (map == null || playable == null || !ExtensionRegistry.OwnerAllowed(owner)) return false;
                string set = HorizonDomain.RegionOwner(map);
                if (set != null && set != owner) return false;
                (float x, float z)[] area = null;
                if (drawArea != null)
                {
                    area = new (float x, float z)[drawArea.Count];
                    for (int i = 0; i < area.Length; i++)
                    {
                        Vector2 p = drawArea[i];
                        if (!float.IsFinite(p.x) || !float.IsFinite(p.y)) return false;
                        area[i] = (p.x, p.y);
                    }
                    if (area.Length > 0 && !HorizonDomain.Convex(area)) return false;
                }
                bool[] mask = HorizonDomain.ReadMask(map, playable);
                if (mask == null) return false;
                if (HorizonDomain.SetRegion(map, HorizonActivity.RingWidth, owner, mask, area)) ExtensionMaps.RegionChanged?.Invoke(map);
                return true;
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(SetRegion), ex); return false; }
        }

        public static bool ClearRegion(Map map, string owner)
        {
            try
            {
                if (map == null || owner == null || HorizonDomain.RegionOwner(map) != owner) return false;
                if (HorizonDomain.ClearRegion(map, HorizonActivity.RingWidth)) ExtensionMaps.RegionChanged?.Invoke(map);
                return true;
            }
            catch (Exception ex) { ApiSupport.Failed(nameof(ClearRegion), ex); return false; }
        }
    }
}
