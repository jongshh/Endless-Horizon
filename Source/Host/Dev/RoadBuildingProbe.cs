#if EH_DEV
using System.Linq;
using System.Text;
using LudeonTK;
using Verse;

namespace EndlessHorizon
{
    internal static class RoadBuildingProbe
    {
        [DebugAction("Endless Horizon", "Roads: log buildings outside", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogBuildings()
        {
            Map map = Find.CurrentMap;
            HorizonState state = HorizonRegistry.Peek(map);
            if (state?.Field == null)
            {
                Log.Message(HorizonLog.Prefix + "EH_ROADBUILDINGS none (no ring)");
                return;
            }
            HorizonDomain domain = state.Domain;
            var b = new StringBuilder(HorizonLog.Prefix + $"EH_ROADBUILDINGS size={domain.W},{domain.H} outside=");
            int count = 0;
            for (int z = -domain.R; z < domain.H + domain.R; z++)
                for (int x = -domain.R; x < domain.W + domain.R; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    ThingDef def = state.Field.RoadBuildingAt(c);
                    if (def == null) continue;
                    if (count++ < 400) b.Append($"{x},{z},{def.defName};");
                }
            b.Append(" inside=");
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def.defName == "AncientConcreteBarrier" || t.def.defName == "AncientLamppost").Take(400))
                b.Append($"{t.Position.x},{t.Position.z},{t.def.defName};");
            Log.Message(b.ToString());
        }
    }
}
#endif
