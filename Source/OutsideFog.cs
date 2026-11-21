using Verse;

namespace EndlessHorizon
{
    public sealed partial class HorizonField
    {
        private bool[] outsideFog;
        private OutsideFogRegions outsideFogRegions;

        public void InvalidateOutsideFog() => outsideFog = null;

        public bool OutsideFogAt(IntVec3 c)
        {
            if (!Covers(c) || c.InBounds(map)) return false;
            if (outsideFog == null)
            {
                outsideFogRegions ??= new OutsideFogRegions(map.Size.x, map.Size.z, r,
                    (x, z) => Mountains != null && Mountains.IsRock(x, z));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                outsideFog = outsideFogRegions.Compute((x, z) =>
                {
                    var edge = new IntVec3(x, 0, z);
                    Building b = edge.GetEdifice(map);
                    return (byte)((map.fogGrid.IsFogged(edge) ? 1 : 0) | (b != null && b.def.mineable ? 2 : 0));
                });
                HorizonPerf.Record("outside fog", sw.Elapsed.TotalMilliseconds);
            }
            return outsideFog[Index(c.x, c.z)];
        }
    }
}
