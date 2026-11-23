using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonDomain
    {
        private sealed class Region
        {
            internal string Owner;
            internal bool[] Playable;
            internal (float x, float z)[] Area;
            internal HorizonDomain Built;
        }

        private static readonly ConditionalWeakTable<Map, Region> regions = new ConditionalWeakTable<Map, Region>();
        private static readonly ConditionalWeakTable<Map, HorizonDomain> rectangles = new ConditionalWeakTable<Map, HorizonDomain>();

        internal static HorizonDomain Of(Map map, int ring)
        {
            if (regions.TryGetValue(map, out Region region))
            {
                if (region.Built == null || region.Built.R != ring)
                    region.Built = new HorizonDomain(map.Size.x, map.Size.z, ring, region.Playable, region.Area);
                return region.Built;
            }
            if (rectangles.TryGetValue(map, out HorizonDomain known) && known.R == ring) return known;
            var made = new HorizonDomain(map.Size.x, map.Size.z, ring);
            rectangles.AddOrUpdate(map, made);
            return made;
        }

        private static bool anyRegion;

        internal static bool PlayableNow(Map map, IntVec3 c, int ring)
        {
            if (!c.InBounds(map)) return false;
            return !anyRegion || !regions.TryGetValue(map, out _) || Of(map, ring).Inside(c);
        }

        internal static string RegionOwner(Map map) => regions.TryGetValue(map, out Region region) ? region.Owner : null;

        internal static bool SetRegion(Map map, int ring, string owner, bool[] playable, (float x, float z)[] area)
        {
            HorizonDomain before = Of(map, ring);
            bool same = before.SameShape(playable, area);
            anyRegion = true;
            regions.AddOrUpdate(map, new Region { Owner = owner, Playable = playable, Area = area, Built = same ? before : null });
            return !same;
        }

        internal static bool ClearRegion(Map map, int ring)
        {
            HorizonDomain before = Of(map, ring);
            regions.Remove(map);
            return !before.SameShape(null, null);
        }

        private IReadOnlyList<IntVec3> boundaryList;

        internal IReadOnlyList<IntVec3> BoundaryList
        {
            get
            {
                if (boundaryList != null) return boundaryList;
                var cells = new IntVec3[Boundary.Length];
                for (int i = 0; i < cells.Length; i++) cells[i] = Cell(Boundary[i]);
                return boundaryList = Array.AsReadOnly(cells);
            }
        }

        internal static bool[] ReadMask(Map map, Predicate<IntVec3> playable)
        {
            int w = map.Size.x, h = map.Size.z;
            var mask = new bool[w * h];
            bool any = false;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    any |= mask[z * w + x] = playable(new IntVec3(x, 0, z));
            return any ? mask : null;
        }

        internal CellRect Bounds => new CellRect(-R, -R, Stride, Rows);

        internal bool InMap(IntVec3 c) => InMap(c.x, c.z);

        internal bool InGrid(IntVec3 c) => InGrid(c.x, c.z);

        internal bool Inside(IntVec3 c) => Inside(c.x, c.z);

        internal bool Outside(IntVec3 c) => Outside(c.x, c.z);

        internal bool Drawn(IntVec3 c) => Drawn(c.x, c.z);

        internal bool OnEdge(IntVec3 c) => OnEdge(c.x, c.z);

        internal int Steps(IntVec3 c) => Steps(c.x, c.z);

        internal IntVec3 Cell(int mapIndex) => new IntVec3(mapIndex % W, 0, mapIndex / W);

        internal IntVec3 Nearest(IntVec3 c)
        {
            Nearest(c.x, c.z, out int x, out int z);
            return new IntVec3(x, 0, z);
        }

        internal IntVec3 ClampToGrid(IntVec3 c) =>
            new IntVec3(Mathf.Clamp(c.x, -R, W + R - 1), 0, Mathf.Clamp(c.z, -R, H + R - 1));

        internal CellRect InMapPart(CellRect rect) => CellRect.FromLimits(
            Mathf.Max(rect.minX, 0), Mathf.Max(rect.minZ, 0), Mathf.Min(rect.maxX, W - 1), Mathf.Min(rect.maxZ, H - 1));

        internal bool MayDraw(IntVec3 c) => !Inside(c) && InArea(c.x + 0.5f, c.z + 0.5f);

        internal bool Interior(CellRect rect) => Interior(rect.minX, rect.minZ, rect.maxX, rect.maxZ);

        internal bool AnyInBand(CellRect rect, int cells) => AnyInBand(rect.minX, rect.minZ, rect.maxX, rect.maxZ, cells);

        internal List<CellRect> StripRects(int size)
        {
            var found = new List<CellRect>();
            foreach ((int x0, int z0, int x1, int z1) in Strips(size)) found.Add(CellRect.FromLimits(x0, z0, x1, z1));
            return found;
        }
    }
}
