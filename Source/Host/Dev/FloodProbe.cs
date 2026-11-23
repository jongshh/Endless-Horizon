#if EH_DEV
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace EndlessHorizon
{
    internal static class FloodProbe
    {
        private const string Tag = "EH_FLOOD ";
        private static readonly MethodInfo Spread = AccessTools.Method(typeof(Flood), "SpreadFlood", Type.EmptyTypes);
        private static readonly FieldInfo NoCell = AccessTools.Field(typeof(Flood), "noPossibleCell");
        private static readonly FieldInfo Removals = AccessTools.Field(typeof(TempTerrainManager), "terrainToRemove");

        [DebugAction("Endless Horizon", "Flood: start seasonal", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void StartSeasonal()
        {
            Map map = Find.CurrentMap;
            if (!ModsConfig.OdysseyActive || ThingDefOf.SeasonalFlood == null) { Log.Message(HorizonLog.Prefix + Tag + "no Odyssey"); return; }
            Thing thing = GenSpawn.Spawn(ThingDefOf.SeasonalFlood, map.Center, map);
            Log.Message(HorizonLog.Prefix + Tag + $"seasonal started={thing is { Spawned: true }} (needs a river; destroys itself without open cells)");
            Follow(map);
        }

        [DebugAction("Endless Horizon", "Flood: start torrential", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void StartTorrential()
        {
            Map map = Find.CurrentMap;
            if (!ModsConfig.OdysseyActive || ThingDefOf.TorrentialRainFlood == null) { Log.Message(HorizonLog.Prefix + Tag + "no Odyssey"); return; }
            if (map.listerThings.ThingsOfDef(ThingDefOf.TorrentialRainFlood).Count == 0) GenSpawn.Spawn(ThingDefOf.TorrentialRainFlood, map.Center, map);
            Log.Message(HorizonLog.Prefix + Tag + $"torrential floods={map.listerThings.ThingsOfDef(ThingDefOf.TorrentialRainFlood).Count} (its own tick waits 4000 ticks; spread works at once)");
            Follow(map);
        }

        [DebugAction("Endless Horizon", "Flood: spread 1000 cells", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Spread1000() => SpreadCells(1000);

        [DebugAction("Endless Horizon", "Flood: spread 5000 cells", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Spread5000() => SpreadCells(5000);

        private static void SpreadCells(int cells)
        {
            Map map = Find.CurrentMap;
            int spread = 0;
            foreach (Flood flood in Floods(map))
            {
                for (int i = 0; i < cells && flood.Spawned; i++)
                {
                    if (NoCell != null && (bool)NoCell.GetValue(flood)) break;
                    Spread.Invoke(flood, null);
                    spread++;
                }
            }
            Log.Message(HorizonLog.Prefix + Tag + $"spread calls={spread} floods={Floods(map).Count()}");
            Follow(map);
        }

        [DebugAction("Endless Horizon", "Flood: end (destroy floods)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void End()
        {
            Map map = Find.CurrentMap;
            int n = 0;
            foreach (Flood flood in Floods(map).ToList())
            {
                flood.Destroy();
                n++;
            }
            Log.Message(HorizonLog.Prefix + Tag + $"ended floods={n} queuedRemovals={QueuedRemovals(map)}");
            Follow(map);
        }

        [DebugAction("Endless Horizon", "Flood: recede 1000 cells", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Recede1000() => Recede(1000);

        [DebugAction("Endless Horizon", "Flood: recede all", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RecedeAll() => Recede(int.MaxValue);

        private static void Recede(int cells)
        {
            Map map = Find.CurrentMap;
            object queue = Removals?.GetValue(map.tempTerrain);
            if (queue == null) { Log.Message(HorizonLog.Prefix + Tag + "no removal queue"); return; }
            PropertyInfo count = queue.GetType().GetProperty("Count");
            MethodInfo dequeue = queue.GetType().GetMethod("Dequeue", Type.EmptyTypes);
            int removed = 0;
            while (removed < cells && (int)count.GetValue(queue) > 0)
            {
                object item = dequeue.Invoke(queue, null);
                var cell = (IntVec3)AccessTools.Field(item.GetType(), "cell").GetValue(item);
                map.terrainGrid.RemoveTempTerrain(cell);
                removed++;
            }
            Log.Message(HorizonLog.Prefix + Tag + $"receded={removed} left={(int)count.GetValue(queue)}");
            Follow(map);
        }

        [DebugAction("Endless Horizon", "Flood: log profile", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogProfile()
        {
            Map map = Find.CurrentMap;
            HorizonState state = HorizonRegistry.Peek(map);
            if (state?.Field == null) { Log.Message(HorizonLog.Prefix + Tag + "none (no ring)"); return; }
            HorizonDomain domain = state.Domain;
            HorizonSurface surface = state.Surface;
            int insideFlooded = 0, outsideFlooded = 0;
            for (int z = -domain.R; z < domain.H + domain.R; z++)
                for (int x = -domain.R; x < domain.W + domain.R; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (surface.Flooded(c) == null) continue;
                    if (domain.Inside(c)) insideFlooded++;
                    else outsideFlooded++;
                }
            int pairs = 0, both = 0, outOnly = 0, outPairs = 0;
            var seen = new HashSet<(int, int, int, int)>();
            foreach (int i in domain.Edge)
            {
                var c = new IntVec3(i % domain.W, 0, i / domain.W);
                bool wetIn = surface.Flooded(c) != null;
                foreach (IntVec3 o in GenAdj.CardinalDirections)
                {
                    IntVec3 n = c + o;
                    if (!domain.Drawn(n) || !seen.Add((c.x, c.z, n.x, n.z))) continue;
                    bool wetOut = surface.Flooded(n) != null;
                    if (wetIn) { pairs++; if (wetOut) both++; }
                    else if (wetOut) outOnly++;
                    if (wetOut) outPairs++;
                }
            }
            string kinds = string.Join(",", Floods(map).Select(f => f.def.defName));
            Log.Message(HorizonLog.Prefix + Tag + $"profile floods=[{kinds}] mapFlooded={insideFlooded} outsideFlooded={outsideFlooded} "
                + $"edge wetMap={pairs} continued={(pairs > 0 ? both / (float)pairs : 1f):P0} wetOutsideNextToDryMap={outOnly}/{outPairs} "
                + $"queuedRemovals={QueuedRemovals(map)} | {state.Field.Flood.Profile()}");
        }

        private static IEnumerable<Flood> Floods(Map map)
        {
            foreach (ThingDef def in new[] { ThingDefOf.SeasonalFlood, ThingDefOf.TorrentialRainFlood })
                if (def != null)
                    foreach (Thing t in map.listerThings.ThingsOfDef(def).ToList())
                        if (t is Flood f && f.Spawned) yield return f;
        }

        private static int QueuedRemovals(Map map)
        {
            object queue = Removals?.GetValue(map.tempTerrain);
            return queue == null ? -1 : (int)queue.GetType().GetProperty("Count").GetValue(queue);
        }

        private static void Follow(Map map) => HorizonRegistry.Peek(map)?.Field?.Flood.Request();
    }
}
#endif
