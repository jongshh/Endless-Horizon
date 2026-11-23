using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonField
    {
        internal RoadExtension Roads;
        private RoadBuild roadBuild;

        private RoadBuild RoadsBuilt => roadBuild ??= new RoadBuild(map, domain, grid);

        public int RoadCells => RoadsBuilt.RoadCells;
        public int BridgeCells => RoadsBuilt.BridgeCells;
        public int RoadCutCells => RoadsBuilt.RoadCutCells;
        internal IntVec3 FirstBridge => RoadsBuilt.FirstBridge;
        public int TunnelCells => RoadsBuilt.TunnelCells;

        internal TerrainDef RoadTopAt(IntVec3 c) => RoadsBuilt.RoadTopAt(c);

        internal ThingDef RoadBuildingAt(IntVec3 c) => RoadsBuilt.BuildingAt(c);

        public int RoadBuildingCells => RoadsBuilt.BuildingCells;

        internal bool BridgeAt(IntVec3 c) => RoadTopAt(c)?.bridge == true;

        internal byte TunnelRoof(IntVec3 c) => RoadsBuilt.TunnelRoof(c);

        internal bool InTunnel(IntVec3 c) => RoadsBuilt.InTunnel(c);

        internal int TunnelBreaks(RoadCrossing crossing) =>
            crossing.CoveredAhead.Count(c => Mountains?.IsRock(c.x, c.z) != true && !InTunnel(c));

        internal bool RoadCleared(IntVec3 c) => RoadsBuilt.RoadCleared(c);

        private void FindRoads()
        {
            try
            {
                Roads = new RoadExtension(map, domain);
                Roads.Locate();
            }
            catch (Exception ex)
            {
                Roads = null;
                Log.Error(HorizonLog.Prefix + "Reading the map's roads failed; they are not continued. " + ex);
            }
        }

        private IEnumerable<bool> BuildRoads() =>
            RoadsBuilt.BuildSteps(Roads, Gen.RockBeforeRoads, Gen.RoofBeforeRoads, Gen.ReplacedSource, Gen.ForgetReflectedRoofs);
    }
}
