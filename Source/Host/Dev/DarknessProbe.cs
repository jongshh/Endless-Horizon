#if EH_DEV
using System;
using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class DarknessProbe
    {
        private const string Tag = "EH_DARKNESS ";

        private static bool Ready(out Map map)
        {
            map = Find.CurrentMap;
            if (map == null) return false;
            if (!ModsConfig.AnomalyActive || GameConditionDefOf.UnnaturalDarkness == null)
            {
                Log.Message(HorizonLog.Prefix + Tag + "Anomaly is not active; nothing to do.");
                return false;
            }
            return true;
        }

        [DebugAction("Endless Horizon", "Darkness: start (unnatural darkness, brightness 0)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Start()
        {
            if (!Ready(out Map map)) return;
            if (!map.gameConditionManager.ConditionIsActive(GameConditionDefOf.UnnaturalDarkness))
                map.gameConditionManager.RegisterCondition(GameConditionMaker.MakeConditionPermanent(GameConditionDefOf.UnnaturalDarkness));
            map.gameConditionManager.SetTargetBrightness(0f, 5f);
            Log.Message(HorizonLog.Prefix + Tag + "started: brightness → 0 over 5 s");
        }

        [DebugAction("Endless Horizon", "Darkness: end", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void End()
        {
            if (!Ready(out Map map)) return;
            map.gameConditionManager.SetTargetBrightness(1f, 5f);
            GameCondition cond = map.gameConditionManager.GetActiveCondition(GameConditionDefOf.UnnaturalDarkness);
            if (cond != null)
            {
                cond.Permanent = false;
                cond.TicksLeft = 301;
            }
            Log.Message(HorizonLog.Prefix + Tag + "ended: brightness → 1 over 5 s" + (cond != null ? ", condition ends in 301 ticks" : ""));
        }

        [DebugAction("Endless Horizon", "Darkness: log", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Report()
        {
            if (!Ready(out Map map)) return;
            GameConditionManager gcm = map.gameConditionManager;
            HorizonState s = HorizonRegistry.Peek(map);
            RingDarkness dark = s?.Darkness;
            var b = new StringBuilder();
            b.Append($"{HorizonLog.Prefix}{Tag}brightness={gcm.MapBrightness:0.000} visible={gcm.DarknessVisible} sky={map.skyManager.CurSkyGlow:0.000} "
                + $"colorTwo.a={Mathf.Clamp01(1f - Mathf.Max(gcm.MapBrightness, map.skyManager.CurSkyGlow)):0.000} "
                + $"ring={(s == null ? "none" : s.Built ? "built" : "building")} on={dark?.On} chunks={dark?.ChunkCount} pending={dark?.Pending} "
                + $"fogDarkensItself={HorizonFog.DarkensItself} darknessQueue={MatBases.Darkness?.renderQueue}");
            if (s?.Domain != null && dark != null)
            {
                HorizonDomain d = s.Domain;
                var probes = new List<(string side, IntVec3 cell, IntVec3 corner, IntVec3 outCorner)>();
                foreach (int z in new[] { d.H / 4, d.H / 2, 3 * d.H / 4 })
                {
                    probes.Add(("W", new IntVec3(0, 0, z), new IntVec3(0, 0, z), new IntVec3(-1, 0, z)));
                    probes.Add(("E", new IntVec3(d.W - 1, 0, z), new IntVec3(d.W, 0, z), new IntVec3(d.W + 1, 0, z)));
                }
                foreach (int x in new[] { d.W / 4, d.W / 2, 3 * d.W / 4 })
                {
                    probes.Add(("S", new IntVec3(x, 0, 0), new IntVec3(x, 0, 0), new IntVec3(x, 0, -1)));
                    probes.Add(("N", new IntVec3(x, 0, d.H - 1), new IntVec3(x, 0, d.H), new IntVec3(x, 0, d.H + 1)));
                }
                foreach ((string side, IntVec3 cell, IntVec3 corner, IntVec3 outCorner) in probes)
                {
                    int vanilla = VanillaCornerAlpha(map, cell, corner);
                    b.Append($"\n  {side} cell {cell.x},{cell.z}: vanilla={vanilla} eh={dark.AlphaAt(corner.x, corner.z)} ehOut1={dark.AlphaAt(outCorner.x, outCorner.z)} "
                        + $"lightIn={s.Surface.DarknessLight(cell.x, cell.z):0.000} lightOut={s.Surface.DarknessLight(cell.x + (side == "W" ? -1 : side == "E" ? 1 : 0), cell.z + (side == "S" ? -1 : side == "N" ? 1 : 0)):0.000}");
                }
            }
            Log.Message(b.ToString());
        }

        private static int VanillaCornerAlpha(Map map, IntVec3 cell, IntVec3 corner)
        {
            try
            {
                Section section = map.mapDrawer.SectionAt(cell);
                if (!(section?.GetLayer(typeof(SectionLayer_Darkness)) is SectionLayer layer) || layer.subMeshes.Count == 0) return -1;
                Mesh mesh = layer.subMeshes[0].mesh;
                var colors = new List<Color32>();
                mesh.GetColors(colors);
                CellRect r = section.CellRect;
                int k = corner.x == cell.x ? (corner.z == cell.z ? 0 : 2) : (corner.z == cell.z ? 6 : 4);
                int index = ((cell.x - r.minX) * r.Height + (cell.z - r.minZ)) * 9 + k;
                return index < colors.Count ? colors[index].a : -1;
            }
            catch (Exception ex)
            {
                Log.Error(HorizonLog.Prefix + Tag + "reading the map's darkness layer failed: " + ex);
                return -1;
            }
        }
    }
}
#endif
