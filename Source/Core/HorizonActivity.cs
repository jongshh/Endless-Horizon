using System.Runtime.CompilerServices;
using Verse;

namespace EndlessHorizon
{
    internal static class HorizonActivity
    {
        private sealed class Off
        {
            internal string Reason;
        }

        private static readonly ConditionalWeakTable<Map, Off> failed = new ConditionalWeakTable<Map, Off>();

        internal static void Failed(Map map, string reason) => failed.AddOrUpdate(map, new Off { Reason = reason });

        internal static string FailureOf(Map map) => map != null && failed.TryGetValue(map, out Off off) ? off.Reason : null;

        internal static bool Eligible(Map map)
        {
            if (map == null || HorizonSettings.Current == null || !HorizonSettings.Current.enabled) return false;
            if (!map.DrawMapClippers || map.IsPocketMap || map.generatorDef == null || map.generatorDef.isUnderground) return false;
            return !(map.TileInfo?.Layer?.Def?.isSpace ?? false);
        }

        internal static bool IsActive(Map map) => Eligible(map) && !failed.TryGetValue(map, out _);

        internal static bool Shows(Map map) => !HorizonDebug.HideRing && IsActive(map);

        internal static int RingWidth => HorizonSettings.Current?.ringWidth ?? 0;

        internal static bool Playable(Map map, IntVec3 c) => HorizonDomain.PlayableNow(map, c, RingWidth);
    }
}
