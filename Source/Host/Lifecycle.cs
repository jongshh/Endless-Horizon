using System;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    [HarmonyPatch(typeof(Map), nameof(Map.Dispose))]
    internal static class Patch_MapDispose
    {
        public static void Prefix(Map __instance)
        {
            try
            {
                Redraw.MapBeingRemoved = __instance;
                HorizonRegistry.Invalidate(__instance);
                GenReconstruction.Forget(__instance);
            }
            catch (Exception ex)
            {
                Log.Error(EndlessHorizonMod.LogPrefix + "Freeing the ring of a removed map failed: " + ex);
            }
            finally
            {
                Redraw.MapBeingRemoved = null;
            }
        }
    }

    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty), new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) })]
    internal static class Patch_MapMeshDirty
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Map> MapRef = AccessTools.FieldRefAccess<MapDrawer, Map>("map");

        private static bool FloodLaid(Map map, HorizonDomain domain, IntVec3 loc)
        {
            if (!domain.InMap(loc)) return false;
            TerrainDef t = map.terrainGrid.TempTerrainAt(loc);
            return t != null && t.IsFlood;
        }

        public static void Postfix(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags)
        {
            if (Redraw.Suppress)
            {
                return;
            }
            bool edgeChange = (dirtyFlags & ((ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.FogOfWar)) != 0;
            if (!edgeChange && (dirtyFlags & (ulong)MapMeshFlagDefOf.Roofs) == 0)
            {
                return;
            }
            Map map = MapRef(__instance);
            if (map == null)
            {
                return;
            }
            HorizonState state = HorizonRegistry.Peek(map);
            if (state?.Domain == null)
            {
                return;
            }
            if ((dirtyFlags & (ulong)MapMeshFlagDefOf.Terrain) != 0 && !FloodLaid(map, state.Domain, loc))
            {
                state.Surface?.WaterChanged();
            }
            if (!state.Domain.NearEdge(loc.x, loc.z, 3))
            {
                return;
            }
            if ((dirtyFlags & (ulong)MapMeshFlagDefOf.Roofs) != 0)
            {
                state.MarkRoofChanged(loc);
            }
            if (!edgeChange)
            {
                return;
            }
            bool surfaceChanged = (dirtyFlags & ((ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings)) != 0;
            state.MarkSeamDirty(loc, surfaceChanged);
        }
    }

    internal static class PerfMeter
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
