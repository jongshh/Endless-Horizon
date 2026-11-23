#if EH_DEV
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class PlantPrintProbe
    {
        [DebugAction("Endless Horizon", "Plants: compare realtime print sizes", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Compare()
        {
            Map map = Find.CurrentMap;
            var b = new StringBuilder(HorizonLog.Prefix + "EH_PLANTPRINT");
            foreach (IGrouping<ThingDef, Thing> group in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant)
                         .Where(t => t.def.drawerType == DrawerType.RealtimeOnly).GroupBy(t => t.def).Take(12))
            {
                Thing t = group.First();
                var layer = new SectionLayer_ThingsGeneral(map.mapDrawer.SectionAt(t.Position));
                float plant = Width(layer, () => t.Print(layer));
                float graphic = Width(layer, () => t.Graphic.Print(layer, t, 0f));
                b.Append($" {t.def.defName}: count={group.Count()} drawSize={t.def.graphicData?.drawSize} visualSize={t.def.plant?.visualSizeRange} plantPrint={plant:0.0} graphicPrint={graphic:0.0};");
            }
            Log.Message(b.ToString());
        }

        [DebugAction("Endless Horizon", "Test map: biome DankPyon_DarkForest", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DarkForest() => Biome("DankPyon_DarkForest");

        private static void Biome(string defName)
        {
            string tag = HorizonLog.Prefix + "EH_PROBE biome-case " + defName + ": ";
            BiomeDef biome = DefDatabase<BiomeDef>.GetNamedSilentFail(defName);
            PlanetLayer surface = Find.WorldGrid.Surface;
            PlanetTile best = PlanetTile.Invalid;
            for (int id = 0; id < surface.TilesCount && biome != null; id++)
            {
                var at = new PlanetTile(id, surface);
                if (at.Tile.PrimaryBiome != biome || Find.WorldObjects.AnyWorldObjectAt(at) || !TileFinder.IsValidTileForNewSettlement(at)) continue;
                best = at;
                break;
            }
            if (!best.Valid)
            {
                Log.Message(tag + "not available");
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
                Log.Message($"{tag}generated map={map.Index} tile={best.tileId}");
            }, "GeneratingMap", false, ex => Log.Error(tag + "failed: " + ex));
        }

        private static float Width(SectionLayer layer, System.Action print)
        {
            var before = layer.subMeshes.Select(m => m.verts.Count).ToList();
            print();
            float width = 0f;
            for (int m = 0; m < layer.subMeshes.Count; m++)
            {
                List<Vector3> verts = layer.subMeshes[m].verts;
                int from = m < before.Count ? before[m] : 0;
                if (verts.Count <= from) continue;
                var added = verts.Skip(from).ToList();
                width = Mathf.Max(width, added.Max(v => v.x) - added.Min(v => v.x));
            }
            foreach (LayerSubMesh sub in layer.subMeshes) sub.Clear(MeshParts.All);
            return width;
        }
    }
}
#endif
