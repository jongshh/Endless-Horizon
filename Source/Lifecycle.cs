using System;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    [HarmonyPatch(typeof(Map), nameof(Map.Dispose))]
    public static class Patch_MapDispose
    {
        public static void Prefix(Map __instance)
        {
            try
            {
                EdgeRock.MapBeingRemoved = __instance;
                HorizonRegistry.Invalidate(__instance);
                GenReconstruction.Forget(__instance);
            }
            catch (Exception ex)
            {
                Log.Warning(EndlessHorizonMod.LogPrefix + "Freeing the ring of a removed map failed: " + ex);
            }
            finally
            {
                EdgeRock.MapBeingRemoved = null;
            }
        }
    }

    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty), new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) })]
    public static class Patch_MapMeshDirty
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Map> MapRef = AccessTools.FieldRefAccess<MapDrawer, Map>("map");

        public static bool Suppress;

        public static void Postfix(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags)
        {
            if (Suppress)
            {
                return;
            }
            if ((dirtyFlags & ((ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.FogOfWar)) == 0)
            {
                return;
            }
            Map map = MapRef(__instance);
            if (map == null)
            {
                return;
            }
            IntVec3 size = map.Size;
            if (loc.x > 2 && loc.z > 2 && loc.x < size.x - 3 && loc.z < size.z - 3)
            {
                return;
            }
            bool surfaceChanged = (dirtyFlags & ((ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings)) != 0;
            HorizonRegistry.Peek(map)?.MarkSeamDirty(loc, surfaceChanged);
        }
    }

    public static class PerfMeter
    {
        private const float HalfSeconds = 6f;
        private static int phase = -1;
        private static float phaseStart;
        private static int ticksAtStart;
        private static double frameSum, frameMax, drawSum, drawMax;
        private static int frames;
        private static string firstHalf;
        private static bool hideBefore;

        public static bool Running => phase >= 0;

        public static void Start()
        {
            hideBefore = HorizonDebug.HideRing;
            HorizonDebug.HideRing = false;
            Begin(0);
            Messages.Message("Endless Horizon: measuring 12 s (6 s ring on, 6 s ring off). Keep the camera still.", RimWorld.MessageTypeDefOf.SilentInput, false);
        }

        private static void Begin(int p)
        {
            phase = p;
            phaseStart = Time.realtimeSinceStartup;
            ticksAtStart = Find.TickManager.TicksGame;
            frameSum = frameMax = drawSum = drawMax = 0;
            frames = 0;
        }

        public static void Frame(double drawMs)
        {
            if (phase < 0)
            {
                return;
            }
            double dt = Time.unscaledDeltaTime * 1000.0;
            frameSum += dt;
            frameMax = Math.Max(frameMax, dt);
            drawSum += drawMs;
            drawMax = Math.Max(drawMax, drawMs);
            frames++;
            float elapsed = Time.realtimeSinceStartup - phaseStart;
            if (elapsed < HalfSeconds)
            {
                return;
            }
            float tps = (Find.TickManager.TicksGame - ticksAtStart) / elapsed;
            string line = $"frame avg {frameSum / Math.Max(1, frames):F2} ms (max {frameMax:F1}), ring draw avg {drawSum / Math.Max(1, frames):F3} ms (max {drawMax:F2}), TPS {tps:F0}, frames {frames}";
            if (phase == 0)
            {
                firstHalf = line;
                HorizonDebug.HideRing = true;
                Begin(1);
                return;
            }
            phase = -1;
            HorizonDebug.HideRing = hideBefore;
            string speed = Find.TickManager.CurTimeSpeed.ToString();
            Log.Message($"{EndlessHorizonMod.LogPrefix}{HorizonProbe.Tag}perf (speed {speed}) ring ON: {firstHalf} | ring OFF: {line}");
            Messages.Message("Endless Horizon: measurement written to the log (EH_PROBE perf).", RimWorld.MessageTypeDefOf.SilentInput, false);
        }

        public static Stopwatch Watch = new Stopwatch();
    }
}
