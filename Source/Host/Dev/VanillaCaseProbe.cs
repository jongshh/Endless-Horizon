#if EH_DEV
using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace EndlessHorizon
{
    internal static class VanillaCaseProbe
    {
        private const string Category = "Endless Horizon";

        private const string Lakes = "Lake|LakeWithIsland|LakeWithIslands|Pond|Lakeshore|CaveLakes|Oasis";
        private const string Coasts = "Coast|Bay|Cove|Peninsula|Fjord|CoastalIsland|CoastalAtoll|Archipelago|Iceberg|Lakeshore";

        [DebugAction(Category, "Vanilla test map: MixedBiome+Coast", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MixedCoast() => Open("MixedBiome+Coast", "MixedBiome", Coasts);

        [DebugAction(Category, "Vanilla test map: MixedBiome+Lake", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MixedLake() => Open("MixedBiome+Lake", "MixedBiome", Lakes);

        [DebugAction(Category, "Vanilla test map: MixedBiome+River", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MixedRiver() => Open("MixedBiome+River", "MixedBiome", "River|RiverDelta|RiverConfluence|RiverIsland|Headwater");

        [DebugAction(Category, "Vanilla test map: two coast workers", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void TwoCoasts() => Open("two coast workers", Coasts, Coasts);

        [DebugAction(Category, "Vanilla test map: coast and lake", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CoastLake() => Open("coast and lake", Coasts, Lakes);

        [DebugAction(Category, "Vanilla test map: Mountain+Caves+River", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MountainCavesRiver() => Open("Mountain+Caves+River", "Mountain", "Caves", "River");

        private static bool Matches(List<string> have, string[] wanted)
        {
            var used = new HashSet<string>();
            foreach (string slot in wanted)
            {
                string found = slot.Split('|').FirstOrDefault(w => have.Contains(w) && !used.Contains(w));
                if (found == null) return false;
                used.Add(found);
            }
            return true;
        }

        private static void Open(string name, params string[] wanted)
        {
            string tag = EndlessHorizonMod.LogPrefix + HorizonProbe.Tag + "vanilla-case " + name + ": ";
            PlanetLayer surface = Find.WorldGrid.Surface;
            PlanetTile best = PlanetTile.Invalid;
            int bestScore = int.MaxValue;
            for (int id = 0; id < surface.TilesCount; id++)
            {
                var at = new PlanetTile(id, surface);
                List<string> have = at.Tile.Mutators.Select(m => m.defName).ToList();
                if (!Matches(have, wanted)) continue;
                int score = have.Count;
                if (score >= bestScore || Find.WorldObjects.AnyWorldObjectAt(at) || !TileFinder.IsValidTileForNewSettlement(at)) continue;
                best = at;
                bestScore = score;
            }
            if (!best.Valid)
            {
                Log.Message($"{tag}not available in this world tiles={surface.TilesCount}");
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
                Log.Message($"{tag}generated map={map.Index} tile={best.tileId} mutators=[{string.Join(",", map.TileInfo.Mutators.Select(m => m.defName))}]");
            }, "GeneratingMap", false, ex => Log.Error(tag + "failed: " + ex));
        }
    }
}
#endif
