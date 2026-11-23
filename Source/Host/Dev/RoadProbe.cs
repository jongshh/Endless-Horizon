#if EH_DEV
using System;
using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace EndlessHorizon
{
    internal static class RoadProbe
    {
        private const string Category = "Endless Horizon";

        [DebugAction(Category, "Road test map: DirtPath", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DirtPath() => Only("DirtPath");

        [DebugAction(Category, "Road test map: DirtRoad", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DirtRoad() => Only("DirtRoad");

        [DebugAction(Category, "Road test map: StoneRoad", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void StoneRoad() => Only("StoneRoad");

        [DebugAction(Category, "Road test map: AncientAsphaltRoad", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void AsphaltRoad() => Only("AncientAsphaltRoad");

        [DebugAction(Category, "Road test map: AncientAsphaltHighway", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Highway() => Only("AncientAsphaltHighway");

        [DebugAction(Category, "Road test map: RiverCrossing", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RiverCrossing() => Open("RiverCrossing",
            (t, roads) => roads.Count >= 2 && t.Rivers?.Count > 0 && !Coastal(t),
            (t, roads) => (t.hilliness > Hilliness.SmallHills ? 20 : 0) + roads.Count + Apart(t, roads));

        [DebugAction(Category, "Road test map: Mountain", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Mountain() => Open("Mountain",
            (t, roads) => t.hilliness == Hilliness.Mountainous && !Coastal(t),
            (t, roads) => (roads.Any(r => r.road.defName.StartsWith("AncientAsphalt")) ? 0 : 20) + (t.Rivers?.Count ?? 0) * 5);

        [DebugAction(Category, "Road test map: Coast", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Coast() => Open("Coast", (t, roads) => t.Mutators.Any(m => m.defName == "Coast"),
            (t, roads) => (int)t.hilliness + (t.Rivers?.Count ?? 0) * 5);

        [DebugAction(Category, "Road test map: Crossroads", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Crossroads() => Open("Crossroads", (t, roads) => roads.Count >= 3 && !Coastal(t),
            (t, roads) => (int)t.hilliness * 2 + (t.Rivers?.Count ?? 0) * 5 - roads.Count);

        [DebugAction(Category, "Road test map: Rectangle", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Rectangle() => Open("Rectangle", (t, roads) => roads.Count >= 2 && !Coastal(t),
            (t, roads) => (int)t.hilliness + (t.Rivers?.Count ?? 0) * 5, size: new IntVec3(300, 1, 200));

        [DebugAction(Category, "Road test map: Landform", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Landform() => Open("Landform",
            (t, roads) => t.Mutators.Any(m => m.defName.StartsWith("GL_") && m.defName != "GL_BiomeTransitions" && !m.defName.StartsWith("GL_River")),
            (t, roads) => t.Mutators.Count);

        private static bool Coastal(SurfaceTile t) => t.Mutators.Any(m => m.categories.Contains("Coast"));

        private static int Apart(SurfaceTile t, List<SurfaceTile.RoadLink> roads) =>
            t.Rivers.Any(river => roads.Any(road => road.neighbor == river.neighbor)) ? 0 : 30;

        private static void Only(string def)
        {
            Open(def, (t, roads) => roads.All(r => r.road.defName == def),
                (t, roads) => (Coastal(t) ? 50 : 0) + (t.Rivers?.Count ?? 0) * 20 + (int)t.hilliness * 2 + Math.Abs(roads.Count - 2) * 5,
                fallback: DefDatabase<RoadDef>.GetNamedSilentFail(def));
        }

        private static void Open(string name, Func<SurfaceTile, List<SurfaceTile.RoadLink>, bool> match,
            Func<SurfaceTile, List<SurfaceTile.RoadLink>, int> score, RoadDef fallback = null, IntVec3? size = null)
        {
            string tag = EndlessHorizonMod.LogPrefix + HorizonProbe.Tag + "road-case " + name + ": ";
            PlanetLayer layer = Find.WorldGrid.Surface;
            PlanetTile best = PlanetTile.Invalid;
            int bestScore = int.MaxValue;
            bool Free(PlanetTile at) => !Find.WorldObjects.AnyWorldObjectAt(at) && TileFinder.IsValidTileForNewSettlement(at);
            for (int id = 0; id < layer.TilesCount; id++)
            {
                var at = new PlanetTile(id, layer);
                if (!(at.Tile is SurfaceTile tile) || tile.Roads == null || tile.Roads.Count == 0 || !match(tile, tile.Roads)) continue;
                int value = score(tile, tile.Roads);
                if (value >= bestScore || !Free(at)) continue;
                best = at; bestScore = value;
            }
            if (!best.Valid && fallback != null)
            {
                var neighbours = new List<PlanetTile>();
                for (int id = 0; id < layer.TilesCount && !best.Valid; id++)
                {
                    var at = new PlanetTile(id, layer);
                    if (!(at.Tile is SurfaceTile tile) || !tile.PrimaryBiome.allowRoads || tile.potentialRoads?.Count > 0 || tile.Rivers?.Count > 0
                        || tile.hilliness > Hilliness.SmallHills || Coastal(tile) || !Free(at)) continue;
                    neighbours.Clear();
                    Find.WorldGrid.GetTileNeighbors(at, neighbours);
                    if (neighbours.Count < 6 || neighbours.Any(n => !n.Tile.PrimaryBiome.allowRoads)) continue;
                    Find.WorldGrid.OverlayRoad(at, neighbours[0], fallback);
                    Find.WorldGrid.OverlayRoad(at, neighbours[neighbours.Count / 2], fallback);
                    best = at;
                    tag += "forced ";
                }
            }
            if (!best.Valid)
            {
                Log.Message(tag + "not available in this world");
                return;
            }
            LongEventHandler.QueueLongEvent(() =>
            {
                var parent = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
                parent.SetFaction(Faction.OfPlayer);
                parent.Tile = best;
                Find.WorldObjects.Add(parent);
                Map map = MapGenerator.GenerateMap(size ?? new IntVec3(250, 1, 250), parent, parent.MapGeneratorDef, parent.ExtraGenStepDefs);
                Current.Game.CurrentMap = map;
                var surface = (SurfaceTile)map.TileInfo;
                Log.Message($"{tag}generated map={map.Index} tile={best.tileId} hilliness={surface.hilliness} rivers={surface.Rivers?.Count ?? 0} "
                            + $"roads=[{string.Join(",", surface.Roads.Select(r => r.road.defName))}] mutators=[{string.Join(",", surface.Mutators.Select(m => m.defName))}]");
            }, "GeneratingMap", false, ex => Log.Error(tag + "failed: " + ex));
        }
    }
}
#endif
