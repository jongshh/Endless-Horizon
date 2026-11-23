#if EH_DEV
using LudeonTK;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class FogCostProbe
    {
        private static int frame;
        private static float time;

        [DebugAction("Endless Horizon", "Fog cost: mark", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Mark()
        {
            float seconds = Time.realtimeSinceStartup - time;
            CellRect view = ExtensionMaps.ViewOf(Find.CurrentMap);
            Log.Message($"{HorizonLog.Prefix}EH_FOGCOST always={HorizonDebug.FogAlways} seconds={seconds:0.00} frames={Time.frameCount - frame} updates={HorizonDebug.FogUpdates} "
                + $"updateMs={HorizonDebug.FogUpdateMs:0.00} uploadMs={HorizonDebug.FogUploadMs:0.00} draws={HorizonDebug.FogDraws} staleDraws={HorizonDebug.FogStaleDraws} view={view.minX},{view.minZ},{view.Width}x{view.Height}");
            frame = Time.frameCount;
            time = Time.realtimeSinceStartup;
            HorizonDebug.FogUpdates = HorizonDebug.FogDraws = HorizonDebug.FogStaleDraws = 0;
            HorizonDebug.FogUpdateMs = HorizonDebug.FogUploadMs = 0;
        }

        [DebugAction("Endless Horizon", "Toggle: fog on the CPU (PERF-02 compare)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleCpu()
        {
            HorizonDebug.FogCpu = !HorizonDebug.FogCpu;
            Log.Message($"{HorizonLog.Prefix}EH_FOGPATH cpu={HorizonDebug.FogCpu} shader={HorizonAssets.FogState}");
        }

        [DebugAction("Endless Horizon", "Toggle: fog updated whatever the view", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Toggle()
        {
            HorizonDebug.FogAlways = !HorizonDebug.FogAlways;
        }
    }
}
#endif
