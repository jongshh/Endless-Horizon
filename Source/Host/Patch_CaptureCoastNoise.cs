using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    internal static class Patch_CaptureCoastNoise
    {
        public static void Postfix(Map __result)
        {
            if (__result == null)
            {
                return;
            }
            try
            {
                CoastNoise.CaptureAfterGeneration(__result);
            }
            catch (Exception ex)
            {
                Log.Error(EndlessHorizonMod.LogPrefix + "Coast noise capture failed: " + ex);
            }
        }
    }
}
