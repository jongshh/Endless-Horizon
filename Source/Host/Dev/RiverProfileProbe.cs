#if EH_DEV
using System.Text;
using EndlessHorizon.Api;
using LudeonTK;
using Verse;

namespace EndlessHorizon
{
    internal static class RiverProfileProbe
    {
        [DebugAction("Endless Horizon", "River: log outside profile", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogProfile()
        {
            Map map = Find.CurrentMap;
            CellRect bounds = HorizonSpace.Bounds(map);
            int ring = HorizonInfo.RingWidth(map);
            var river = new int[ring + 2];
            var water = new int[ring + 2];
            foreach (IntVec3 c in bounds)
            {
                if (!HorizonSpace.IsOutside(map, c)) continue;
                int d = (int)HorizonSpace.DistanceFromPlayable(map, c);
                if (d < 0 || d > ring + 1) continue;
                TerrainDef t = HorizonWorld.TerrainAt(map, c);
                if (t == null) continue;
                if (t.IsRiver) river[d]++;
                if (t.IsWater) water[d]++;
            }
            var b = new StringBuilder(HorizonLog.Prefix + $"EH_RIVERPROFILE ring={ring} river/water by distance:");
            for (int d = 1; d <= ring + 1; d++) b.Append($" {d}:{river[d]}/{water[d]}");
            Log.Message(b.ToString());
        }
    }
}
#endif
