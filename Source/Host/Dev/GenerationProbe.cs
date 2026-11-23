#if EH_DEV
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace EndlessHorizon
{
    internal static class GenerationProbe
    {
        private const string Category = "Endless Horizon";
        private const string Transitions = "BiomeTransitions";

        [DebugAction(Category, "GL test map: Island", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Island() => Open("Island", "Island", false);

        [DebugAction(Category, "GL test map: Archipelago", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Archipelago() => Open("Archipelago", "Archipelago", false, substitute: "Island");

        [DebugAction(Category, "GL test map: Valley", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Valley() => Open("Valley", "Valley", false);

        [DebugAction(Category, "GL test map: Coast", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Coast() => Open("Coast", "Coast", false);

        [DebugAction(Category, "GL test map: Transitions", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void TransitionsOnly() => Open("Transitions", null, true);

        [DebugAction(Category, "GL test map: Coast+Transitions", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CoastTransitions() => Open("Coast+Transitions", "Coast", true);

        [DebugAction(Category, "GL test map: Valley+Transitions", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ValleyTransitions() => Open("Valley+Transitions", "Valley", true);

        [DebugAction(Category, "GL test map: River", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void River() => Open("River", "River", false);

        [DebugAction(Category, "GL test map: VanillaCoast+Transitions", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void VanillaCoastTransitions() => Open("VanillaCoast+Transitions", null, true, "Coast");

        [DebugAction(Category, "GL: log string hash", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void StringHash() => Log.Message(EndlessHorizonMod.LogPrefix + HorizonProbe.Tag + "string-hash=" + "WaterShallow".GetHashCode());

        private static void Open(string name, string form, bool transitions, string mutator = null, string substitute = null)
        {
            string tag = EndlessHorizonMod.LogPrefix + HorizonProbe.Tag + "gl-case " + name + ": ";
            Type info = AccessTools.TypeByName("GeologicalLandforms.WorldTileInfo");
            if (info == null)
            {
                Log.Message(tag + "not available (Geological Landforms is not active)");
                return;
            }
            var get = AccessTools.Method(info, "Get", new[] { typeof(int), typeof(bool) });
            PlanetLayer surface = Find.WorldGrid.Surface;
            var wanted = new HashSet<string>();
            if (form != null) wanted.Add(form);
            if (transitions) wanted.Add(Transitions);
            PlanetTile best = PlanetTile.Invalid;
            int bestExtra = int.MaxValue, bestScore = int.MaxValue, matched = 0;
            var census = new Dictionary<string, int>();
            for (int id = 0; id < surface.TilesCount; id++)
            {
                object tile = get.Invoke(null, new object[] { id, true });
                if (!(AccessTools.Property(tile.GetType(), "Landforms")?.GetValue(tile) is IEnumerable forms)) continue;
                List<string> ids = forms.Cast<object>().Select(f => (string)AccessTools.Property(f.GetType(), "Id").GetValue(f)).ToList();
                foreach (string found in ids) census[found] = census.TryGetValue(found, out int n) ? n + 1 : 1;
                if (!wanted.All(ids.Contains)) continue;
                matched++;
                if (!transitions && ids.Contains(Transitions)) continue;
                if (mutator != null && !new PlanetTile(id, surface).Tile.Mutators.Any(m => m.defName == mutator)) continue;
                int extra = ids.Count - wanted.Count;
                var at = new PlanetTile(id, surface);
                int score = extra * 100 + at.Tile.Mutators.Count;
                if (score >= bestScore) continue;
                if (Find.WorldObjects.AnyWorldObjectAt(at) || !TileFinder.IsValidTileForNewSettlement(at)) continue;
                best = at;
                bestExtra = extra;
                bestScore = score;
                if (score == wanted.Count) break;
            }
            if (!best.Valid && substitute != null)
            {
                for (int id = 0; id < surface.TilesCount && !best.Valid; id++)
                {
                    var at = new PlanetTile(id, surface);
                    TileMutatorDef old = at.Tile.Mutators.FirstOrDefault(m => m.defName == "GL_" + substitute);
                    if (old == null || Find.WorldObjects.AnyWorldObjectAt(at) || !TileFinder.IsValidTileForNewSettlement(at)) continue;
                    at.Tile.RemoveMutator(old);
                    at.Tile.AddMutator(DefDatabase<TileMutatorDef>.GetNamed("GL_" + form));
                    AccessTools.Method(info, "InvalidateCache")?.Invoke(null, null);
                    best = at;
                    bestExtra = 0;
                    tag += "forced ";
                }
            }
            if (!best.Valid)
            {
                Log.Message($"{tag}not available in this world tiles={surface.TilesCount} matched={matched} census=[{string.Join(",", census.OrderByDescending(p => p.Value).Select(p => p.Key + " " + p.Value))}]");
                return;
            }
            LongEventHandler.QueueLongEvent(() =>
            {
                var parent = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
                parent.SetFaction(Faction.OfPlayer);
                parent.Tile = best;
                Find.WorldObjects.Add(parent);
                Map map = MapGenerator.GenerateMap(new IntVec3(250, 1, 250), parent, parent.MapGeneratorDef, parent.ExtraGenStepDefs);
                Current.Game.CurrentMap = map;
                Log.Message($"{tag}generated map={map.Index} tile={best.tileId} extraLandforms={bestExtra} mutators=[{string.Join(",", map.TileInfo.Mutators.Select(m => m.defName))}]");
            }, "GeneratingMap", false, ex => Log.Error(tag + "failed: " + ex));
        }
    }
}
#endif
