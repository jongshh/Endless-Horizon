using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    [HarmonyPatch(typeof(MapEdgeClipDrawer), nameof(MapEdgeClipDrawer.DrawClippers))]
    internal static class Patch_DrawClippers
    {
        public static bool Prefix(Map map)
        {
            if (!HorizonRegistry.IsActive(map))
            {
                return true;
            }
            if (ExtensionMaps.ExternalOwner(map) != null) return false;
            try
            {
                DrawCost.Begin();
                HorizonState state = HorizonRegistry.Get(map);
                HorizonProbe.OnFrame(map, state);
                DrawCost.Lap(DrawCost.Get);
                if (HorizonDebug.HideRing)
                {
                    PerfMeter.Frame(0);
                    return true;
                }
                PerfMeter.Watch.Restart();
                state.Draw(Find.CameraDriver.CurrentViewRect);
                PerfMeter.Watch.Stop();
                PerfMeter.Frame(PerfMeter.Watch.Elapsed.TotalMilliseconds);
                if (!(EndlessHorizonMod.Settings.fog && !HorizonDebug.HideFog))
                {
                    ShiftedClippers.Draw(map, state.Domain);
                }
                DrawCost.End();
                return false;
            }
            catch (Exception ex)
            {
                Fail(map, ex);
                return true;
            }
        }

        private static void Fail(Map map, Exception ex)
        {
            HorizonActivity.Failed(map, ex.GetType().Name);
            Log.Error(EndlessHorizonMod.LogPrefix + $"Drawing failed on map {map.uniqueID}; its ring is off for this session.\n" + ex);
        }

        internal static bool DrawExternally(Map map, CellRect view)
        {
            if (!HorizonActivity.Shows(map)) return false;
            try
            {
                HorizonState state = HorizonRegistry.Get(map);
                HorizonProbe.OnFrame(map, state);
                state.Draw(view);
                return true;
            }
            catch (Exception ex)
            {
                Fail(map, ex);
                return false;
            }
        }
    }
}
