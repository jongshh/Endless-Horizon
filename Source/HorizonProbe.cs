using System;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class HorizonProbe
    {
        public const string Tag = "EH_PROBE ";

        private static Map probeMap;
        private static float t0;
        private static int step;
        private static float dtSum;
        private static int dtCount;

        public static void OnBuilt(HorizonState s)
        {
            Map map = s.map;
            string mutators = string.Join(",", map.TileInfo?.Mutators?.Select(m => m.defName) ?? Enumerable.Empty<string>());
            Log.Message($"{EndlessHorizonMod.LogPrefix}{Tag}built map={map.uniqueID} size={map.Size.x}x{map.Size.z} biome={map.Biome?.defName} " +
                        $"mutators=[{mutators}] ring={s.RingWidth} fogStart={s.FogStart} chunks={s.ChunkCount} verts={s.VertexCount} buildMs={s.BuildMs} " +
                        $"coast={s.Field.CoastOrigin}/{s.Field.CoastMode} coastCells={s.Field.CoastCells} " +
                        $"rivers={s.Field.River?.Crossings.Count ?? -1} riverCells={s.Field.RiverCells} meander=x{RiverExtension.MeanderScale:0.##} bank=x{RiverExtension.BankScale:0.##} " +
                        $"printedThings={s.PrintedThings} fogCells={s.FogCells} rockCells={s.Mountains?.RockCells ?? -1} fogQueue={(HorizonFog.SelfLit ? "selfLit" : "lit")} " +
                        $"outside={s.Field.OutsideMode} riverPaint=[{s.Field.RiverPaintReport}] fogTint={s.Field.FogTint?.Report ?? "none"} " +
                        $"rockPrint={RockPrint.Printed}/{RockPrint.Fallbacks} rockNoDef={s.Mountains?.RockCellsWithoutDef ?? -1} weatherReimagined={(Compat_WeatherReimagined.Installed ? (s.OpenSky != null ? "openSky" : "failed") : "absent")}");
            GenReconstruction recon = GenReconstruction.For(map);
            Log.Message($"{EndlessHorizonMod.LogPrefix}generator match (6a test): {recon.Report}, {recon.Ms} ms.");
#if EH_DEV
            if (s.Field.River != null)
            {
                foreach (RiverCrossing c in s.Field.River.Crossings)
                {
                    Log.Message($"{EndlessHorizonMod.LogPrefix}{Tag}river {c}");
                }
            }
#endif
            if (EndlessHorizonMod.ProbeMode)
            {
                Log.Message(EndlessHorizonMod.LogPrefix + Tag + CoastReport(map, true));
            }
        }

        public static string CoastReport(Map map, bool withReplay)
        {
            CoastNoise.Source src = CoastNoise.Captured(map);
            if (CoastNoise.CoastWorkerOf(map) == null)
            {
                return "coast: no coast mutator on this tile";
            }
            var sb = new StringBuilder();
            if (src == null)
            {
                sb.Append("coast: no capture (map was loaded, not generated this session)");
                if (withReplay)
                {
                    src = CoastNoise.Replay(map);
                    sb.Append(src == null ? "; replay failed" : "; using replay");
                }
                if (src == null)
                {
                    return sb.ToString();
                }
            }
            sb.Append($"coast[{src.origin}] {src.worker.GetType().Name}: ");
            int tp = 0, fp = 0, fn = 0;
            double edgeStep = 0, innerStep = 0;
            int edgeN = 0;
            using (new CoastNoise.Scope(src))
            {
                foreach (IntVec3 c in map.AllCells)
                {
                    bool predicted = CoastNoise.Value(src, c) < 0.5f;
                    TerrainDef t = c.GetTerrain(map);
                    bool actual = t != null && t.IsOcean;
                    if (predicted && actual) tp++;
                    else if (predicted) fp++;
                    else if (actual) fn++;
                }
                foreach (IntVec3 b in CellRect.WholeMap(map).EdgeCells)
                {
                    IntVec3 outward = Outward(map, b);
                    float vb = CoastNoise.Value(src, b);
                    edgeStep += Math.Abs(CoastNoise.Value(src, b + outward) - vb);
                    innerStep += Math.Abs(vb - CoastNoise.Value(src, b - outward));
                    edgeN++;
                }
            }
            sb.Append($"waterAgreement tp={tp} fp={fp} fn={fn} iou={(tp + fp + fn == 0 ? 0f : tp / (float)(tp + fp + fn)):F3}; ");
            sb.Append($"continuity meanStep edge→out={edgeStep / Math.Max(1, edgeN):F4} in→edge={innerStep / Math.Max(1, edgeN):F4}");

            if (withReplay && src.origin == "capture")
            {
                CoastNoise.Source rep = CoastNoise.Replay(map);
                if (rep == null)
                {
                    sb.Append("; replay=null");
                }
                else
                {
                    float maxDiff = 0f;
                    int n = 0;
                    for (int x = -40; x < map.Size.x + 40; x += 7)
                    {
                        for (int z = -40; z < map.Size.z + 40; z += 7)
                        {
                            var c = new IntVec3(x, 0, z);
                            float a, b;
                            using (new CoastNoise.Scope(src)) a = CoastNoise.Value(src, c);
                            using (new CoastNoise.Scope(rep)) b = CoastNoise.Value(rep, c);
                            maxDiff = Math.Max(maxDiff, Math.Abs(a - b));
                            n++;
                        }
                    }
                    sb.Append($"; replay vs capture maxDiff={maxDiff:F6} over {n} samples");
                }
            }
            return sb.ToString();
        }

        private static IntVec3 Outward(Map map, IntVec3 b)
        {
            if (b.x == 0) return IntVec3.West;
            if (b.x == map.Size.x - 1) return IntVec3.East;
            if (b.z == 0) return IntVec3.South;
            return IntVec3.North;
        }

        public static IntVec3? WaterEdgeMidpoint(Map map)
        {
            int best = 10;
            IntVec3? result = null;
            IntVec3 size = map.Size;
            (Rot4 rot, IntVec3 mid)[] edges =
            {
                (Rot4.West, new IntVec3(0, 0, size.z / 2)), (Rot4.East, new IntVec3(size.x - 1, 0, size.z / 2)),
                (Rot4.South, new IntVec3(size.x / 2, 0, 0)), (Rot4.North, new IntVec3(size.x / 2, 0, size.z - 1)),
            };
            foreach ((Rot4 rot, IntVec3 mid) in edges)
            {
                int water = CellRect.WholeMap(map).GetEdgeCells(rot).Count(c => c.GetTerrain(map)?.IsWater ?? false);
                if (water > best)
                {
                    best = water;
                    result = mid;
                }
            }
            return result;
        }

        private static Vector3? pinPos;
        private static float pinSize;

        public static void JumpTo(IntVec3 cell, float size = 30f)
        {
            Find.CameraDriver.SetRootPosAndSize(new Vector3(cell.x, 0f, cell.z), size);
            if (EndlessHorizonMod.ProbeMode)
            {
                pinPos = new Vector3(cell.x, 0f, cell.z);
                pinSize = size;
            }
        }

        private static void Shot(string name, HorizonState s)
        {
            float ms = dtCount == 0 ? 0f : dtSum / dtCount * 1000f;
            Log.Message($"{EndlessHorizonMod.LogPrefix}{Tag}shot={name} hideRing={HorizonDebug.HideRing} drawnChunks={s.LastDrawnChunks} drawCalls={s.LastDrawCalls} avgFrameMs={ms:F2} frames={dtCount} cam={Find.CameraDriver.MapPosition}");
            ScreenshotTaker.TakeNonSteamShot(name);
        }

        private static void ResetFrameStats()
        {
            dtSum = 0f;
            dtCount = 0;
        }

        public static void OnFrame(Map map, HorizonState s)
        {
            if (!EndlessHorizonMod.ProbeMode || step < 0)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (probeMap != map)
            {
                probeMap = map;
                t0 = now;
                step = 0;
                Log.Message(EndlessHorizonMod.LogPrefix + Tag + "start");
            }
            dtSum += Time.deltaTime;
            dtCount++;
            if (pinPos.HasValue)
            {
                Find.CameraDriver.SetRootPosAndSize(pinPos.Value, pinSize);
            }
            float t = now - t0;
            switch (step)
            {
                case 0 when t > 3f:
                    JumpTo(new IntVec3(0, 0, 0));
                    ResetFrameStats();
                    step++;
                    break;
                case 1 when t > 7f:
                    Shot("eh_corner_on", s);
                    step++;
                    break;
                case 2 when t > 9f:
                    HorizonDebug.HideRing = true;
                    ResetFrameStats();
                    step++;
                    break;
                case 3 when t > 13f:
                    Shot("eh_corner_off", s);
                    step++;
                    break;
                case 4 when t > 15f:
                    HorizonDebug.HideRing = false;
                    IntVec3? water = WaterEdgeMidpoint(map);
                    if (water.HasValue)
                    {
                        JumpTo(water.Value, 40f);
                    }
                    else
                    {
                        JumpTo(new IntVec3(map.Size.x / 2, 0, 0), 40f);
                    }
                    Log.Message($"{EndlessHorizonMod.LogPrefix}{Tag}edge view target={(water.HasValue ? "water edge " + water.Value : "south edge (no water edge)")}");
                    ResetFrameStats();
                    step++;
                    break;
                case 5 when t > 19f:
                    Shot("eh_edge_on", s);
                    step++;
                    break;
                case 6 when t > 22f:
                    Log.Message(EndlessHorizonMod.LogPrefix + Tag + "done");
                    pinPos = null;
                    step = -1;
                    break;
            }
        }
    }

#if EH_DEV
    public static class HorizonDebugActions
    {
        [DebugAction("Endless Horizon", "Rebuild ring", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Rebuild()
        {
            HorizonRegistry.Invalidate(Find.CurrentMap);
        }

        [DebugAction("Endless Horizon", "Rebuild ring (coast from seed replay)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RebuildWithReplay()
        {
            Map map = Find.CurrentMap;
            HorizonRegistry.Invalidate(map);
            CoastNoise.Source rep = CoastNoise.Replay(map);
            HorizonState s = HorizonRegistry.Get(map);
            s.CoastSourceOverride = rep;
            Messages.Message(rep == null ? "Endless Horizon: no coast to replay." : "Endless Horizon: rebuilt with replayed coast noise.", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Endless Horizon", "Inspect cell (click on the map)", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void InspectCell()
        {
            Map map = Find.CurrentMap;
            IntVec3 c = UI.MouseCell();
            HorizonState s = HorizonRegistry.Peek(map);
            HorizonField f = s?.Field;
            MountainMask m = s?.Mountains;
            string text;
            if (c.InBounds(map))
            {
                Building b = c.GetEdifice(map);
                text = $"{c} IN MAP fogged={map.fogGrid.IsFogged(c)} edifice={b?.def.defName ?? "-"} roof={c.GetRoof(map)?.defName ?? "-"} terrain={map.terrainGrid.TerrainAt(c)?.defName}";
            }
            else if (f == null || !f.Covers(c))
            {
                text = $"{c} outside the ring";
            }
            else
            {
                string Links()
                {
                    if (m == null) return "-";
                    string r = "";
                    foreach (IntVec3 d in GenAdj.CardinalDirections)
                    {
                        IntVec3 n = c + d;
                        r += m.IsRock(n.x, n.z) ? (m.RockDefAt(n.x, n.z)?.defName ?? (n.InBounds(map) ? "map" : "NODEF")) : "open";
                        r += " ";
                    }
                    return r.Trim();
                }
                ThingDef rd = m?.RockDefAt(c.x, c.z);
                Thing src = rd != null ? m.SourceFor(rd, c) : null;
                text = $"{c} OUTSIDE rock={m?.IsRock(c.x, c.z)} def={rd?.defName ?? "-"} source={(src != null ? src.Position.ToString() : "none (atlas)")} " +
                       $"N/E/S/W=[{Links()}] fog={HorizonThings.OutsideFogged(map, f, c)} thickRoof={f.OutsideThickRoof(c)} terrain={f.At(c)?.defName} genRock={f.GenRock(c)} sample={f.SampleAt(c)}";
            }
            var grid = new System.Text.StringBuilder();
            for (int dz = 4; dz >= -4; dz--)
            {
                grid.Append($"\n  z={c.z + dz,4} ");
                for (int dx = -4; dx <= 4; dx++)
                {
                    var n = new IntVec3(c.x + dx, 0, c.z + dz);
                    char ch;
                    if (n.InBounds(map))
                    {
                        Building b = n.GetEdifice(map);
                        bool rock = b != null && b.def.mineable;
                        bool fog = map.fogGrid.IsFogged(n);
                        TerrainDef t = map.terrainGrid.TerrainAt(n);
                        ch = rock ? (fog ? '@' : '#') : (t != null && t.IsWater ? '~' : (fog ? ',' : '.'));
                    }
                    else if (f != null && f.Covers(n))
                    {
                        bool rock = m != null && m.IsRock(n.x, n.z);
                        bool fog = HorizonThings.OutsideFogged(map, f, n);
                        TerrainDef t = f.At(n);
                        ch = rock ? (fog ? 'r' : 'R') : (t != null && t.IsWater ? '~' : 'o');
                    }
                    else
                    {
                        ch = ' ';
                    }
                    grid.Append(dx == 0 && dz == 0 ? "[" + ch + "]" : " " + ch + " ");
                }
            }
            grid.Append($"\n  x from {c.x - 4} to {c.x + 4}; map is x 0..{map.Size.x - 1}, z 0..{map.Size.z - 1}");
            Log.Message(EndlessHorizonMod.LogPrefix + "inspect: " + text + grid);
            Messages.Message("Endless Horizon: " + text, MessageTypeDefOf.SilentInput, false);
        }

        [DebugAction("Endless Horizon", "6a: generator match report (re-run)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenReport()
        {
            Map map = Find.CurrentMap;
            GenReconstruction.Forget(map);
            GenReconstruction r = GenReconstruction.For(map);
            Log.Message($"{EndlessHorizonMod.LogPrefix}generator match (6a test): {r.Report}, {r.Ms} ms.");
            Messages.Message($"Endless Horizon 6a: rock/open {r.RockMatch:P0}, terrain {r.TerrainMatch:P0}, outside evaluator {r.EvaluatorMatch:P0}, usable {r.Usable} ({r.Ms} ms). Details in the log.", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Endless Horizon", "Toggle: generator outside / 0.6 mirror (rebuild)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleForceMirror()
        {
            HorizonDebug.ForceMirror = !HorizonDebug.ForceMirror;
            HorizonRegistry.InvalidateAll();
            Say(HorizonDebug.ForceMirror ? "outside = 0.6 mirror (forced)" : "outside = generator when it matches");
        }

        private static void Say(string text) => Messages.Message("Endless Horizon: " + text, MessageTypeDefOf.SilentInput, false);

        [DebugAction("Endless Horizon", "Toggle: whole ring (vanilla edge)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleRing()
        {
            HorizonDebug.HideRing = !HorizonDebug.HideRing;
            Say(HorizonDebug.HideRing ? "ring OFF (vanilla edge)" : "ring ON");
        }

        [DebugAction("Endless Horizon", "Toggle: edge fog (haze)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleFog()
        {
            HorizonDebug.HideFog = !HorizonDebug.HideFog;
            Say(HorizonDebug.HideFog ? "edge fog OFF" : "edge fog ON");
        }

        [DebugAction("Endless Horizon", "Toggle: drifting clouds", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleClouds()
        {
            HorizonDebug.HideClouds = !HorizonDebug.HideClouds;
            Say(HorizonDebug.HideClouds ? "clouds OFF" : "clouds ON");
        }

        [DebugAction("Endless Horizon", "Toggle: plants, rocks and fog outside (rebuild)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleThings()
        {
            HorizonThings.Enabled = !HorizonThings.Enabled;
            HorizonRegistry.InvalidateAll();
            Say(HorizonThings.Enabled ? "plants/rocks outside ON" : "plants/rocks outside OFF");
        }

        [DebugAction("Endless Horizon", "Toggle: seam blend at the map edge (rebuild)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleSeam()
        {
            HorizonChunk.SeamBlend = !HorizonChunk.SeamBlend;
            HorizonRegistry.InvalidateAll();
            Say(HorizonChunk.SeamBlend ? "seam blend ON" : "seam blend OFF");
        }

        [DebugAction("Endless Horizon", "Season: refresh outside plants and snow now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RefreshNow()
        {
            HorizonState s = HorizonRegistry.Get(Find.CurrentMap);
            s.EnsureBuilt();
            s.RequestRefresh();
            Say("rolling refresh queued (a few chunks per frame)");
        }

        [DebugAction("Endless Horizon", "Season: add snow 0.5 on the whole map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void AddSnow()
        {
            Map m = Find.CurrentMap;
            foreach (IntVec3 c in m.AllCells)
            {
                if (!c.Roofed(m))
                {
                    m.snowGrid.AddDepth(c, 0.5f);
                }
            }
            Say("snow +0.5 (outside follows on the next hourly check, or use Season: refresh)");
        }

        [DebugAction("Endless Horizon", "Toggle: outside lighting", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleLighting()
        {
            HorizonDebug.HideLighting = !HorizonDebug.HideLighting;
            Say(HorizonDebug.HideLighting ? "outside lighting OFF" : "outside lighting ON");
        }

        private static void Cam(int x, int z) => HorizonProbe.JumpTo(new IntVec3(x, 0, z), 40f);

        [DebugAction("Endless Horizon", "Camera: north edge", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CamN() { Map m = Find.CurrentMap; Cam(m.Size.x / 2, m.Size.z); }

        [DebugAction("Endless Horizon", "Camera: east edge", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CamE() { Map m = Find.CurrentMap; Cam(m.Size.x, m.Size.z / 2); }

        [DebugAction("Endless Horizon", "Camera: south edge", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CamS() { Map m = Find.CurrentMap; Cam(m.Size.x / 2, 0); }

        [DebugAction("Endless Horizon", "Camera: west edge", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CamW() { Map m = Find.CurrentMap; Cam(0, m.Size.z / 2); }

        [DebugAction("Endless Horizon", "Camera: SW corner", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CamSW() => Cam(0, 0);

        [DebugAction("Endless Horizon", "Camera: NE corner", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CamNE() { Map m = Find.CurrentMap; Cam(m.Size.x, m.Size.z); }

        [DebugAction("Endless Horizon", "Camera: edge with most water", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CamWater()
        {
            Map m = Find.CurrentMap;
            IntVec3? c = HorizonProbe.WaterEdgeMidpoint(m);
            if (c.HasValue) Cam(c.Value.x, c.Value.z);
            else Say("no edge with water");
        }

        private static int riverCursor;

        [DebugAction("Endless Horizon", "River: log crossings", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogRivers()
        {
            HorizonState s = HorizonRegistry.Get(Find.CurrentMap);
            s.EnsureBuilt();
            var list = s.Field.River?.Crossings;
            if (list == null || list.Count == 0)
            {
                Say("no river crossings on the map edge");
                return;
            }
            foreach (RiverCrossing c in list)
            {
                Log.Message($"{EndlessHorizonMod.LogPrefix}{HorizonProbe.Tag}river {c}");
            }
            Say($"{list.Count} river crossing(s) written to the log");
        }

        [DebugAction("Endless Horizon", "River: camera to next crossing", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CamRiver()
        {
            HorizonState s = HorizonRegistry.Get(Find.CurrentMap);
            s.EnsureBuilt();
            var list = s.Field.River?.Crossings;
            if (list == null || list.Count == 0)
            {
                Say("no river crossings on the map edge");
                return;
            }
            RiverCrossing c = list[riverCursor++ % list.Count];
            Cam(Mathf.RoundToInt(c.start.x), Mathf.RoundToInt(c.start.y));
            Say($"river crossing {(riverCursor - 1) % list.Count + 1}/{list.Count}: {c.side.ToStringHuman()}, width {c.width:F0}, link {c.link}");
        }

        private static void RiverStyle(float meander, float bank, string label)
        {
            if (meander > 0f) RiverExtension.MeanderScale = meander;
            if (bank > 0f) RiverExtension.BankScale = bank;
            HorizonRegistry.InvalidateAll();
            Say($"river {label}: meander x{RiverExtension.MeanderScale:0.##}, bank x{RiverExtension.BankScale:0.##} (test only, not saved)");
        }

        [DebugAction("Endless Horizon", "River meander: calm x0.5", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RM05() => RiverStyle(0.5f, 0f, "calm");

        [DebugAction("Endless Horizon", "River meander: medium x1", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RM1() => RiverStyle(1f, 0f, "medium");

        [DebugAction("Endless Horizon", "River meander: default x1.6", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RM16() => RiverStyle(1.6f, 0f, "default");

        [DebugAction("Endless Horizon", "River bank: narrow x0.5", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RB05() => RiverStyle(0f, 0.5f, "narrow bank");

        [DebugAction("Endless Horizon", "River bank: default x1", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RB1() => RiverStyle(0f, 1f, "default bank");

        [DebugAction("Endless Horizon", "River bank: wide x1.6", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RB16() => RiverStyle(0f, 1.6f, "wide bank");

        [DebugAction("Endless Horizon", "Coast: forget capture and recover (as after loading)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CoastRecover()
        {
            Map m = Find.CurrentMap;
            CoastNoise.Forget(m);
            CoastSearch.Forget(m);
            HorizonRegistry.Invalidate(m);
            Say("coast capture dropped; recovery search starts (see log: coast search …)");
        }

        private static void SkipToHour(float hour)
        {
            Map m = Find.CurrentMap;
            float now = GenLocalDate.HourFloat(m);
            float delta = (hour - now + 24f) % 24f;
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + Mathf.RoundToInt(delta * GenDate.TicksPerHour));
            Say($"time → {hour:0}h");
        }

        [DebugAction("Endless Horizon", "Time: dawn 6h", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void T6() => SkipToHour(6f);

        [DebugAction("Endless Horizon", "Time: noon 12h", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void T12() => SkipToHour(12f);

        [DebugAction("Endless Horizon", "Time: dusk 18h", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void T18() => SkipToHour(18f);

        [DebugAction("Endless Horizon", "Time: midnight 0h", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void T0() => SkipToHour(0f);

        private static void SetWeather(string defName)
        {
            WeatherDef def = DefDatabase<WeatherDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Say("no weather " + defName);
                return;
            }
            Map m = Find.CurrentMap;
            m.weatherManager.TransitionTo(def);
            var tr = HarmonyLib.Traverse.Create(m.weatherManager);
            tr.Field("lastWeather").SetValue(def);
            tr.Field("curWeatherAge").SetValue(10000);
            Say("weather → " + def.label);
        }

        [DebugAction("Endless Horizon", "Weather: clear", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WClear() => SetWeather("Clear");

        [DebugAction("Endless Horizon", "Weather: fog", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WFog() => SetWeather("Fog");

        [DebugAction("Endless Horizon", "Weather: rain", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WRain() => SetWeather("Rain");

        [DebugAction("Endless Horizon", "Weather: hard snow", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WSnow() => SetWeather("SnowHard");

        private static void FogStart(int cells)
        {
            EndlessHorizonMod.Settings.fogStart = cells;
            EndlessHorizonMod.Settings.Sanitize();
            HorizonRegistry.InvalidateAll();
            Say($"fog starts at {EndlessHorizonMod.Settings.fogStart} cells (not saved; use mod settings to keep)");
        }

        [DebugAction("Endless Horizon", "Fog start: 20 cells", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void F20() => FogStart(20);

        [DebugAction("Endless Horizon", "Fog start: 32 cells (default)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void F32() => FogStart(32);

        [DebugAction("Endless Horizon", "Fog start: 40 cells", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void F40() => FogStart(40);

        [DebugAction("Endless Horizon", "Perf: measure 12 s (ring on / off)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Perf()
        {
            if (!PerfMeter.Running)
            {
                PerfMeter.Start();
            }
        }

        [DebugAction("Endless Horizon", "Log probe report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogReport()
        {
            Map map = Find.CurrentMap;
            HorizonState s = HorizonRegistry.Get(map);
            s.EnsureBuilt();
            Log.Message($"{EndlessHorizonMod.LogPrefix}{HorizonProbe.Tag}chunks={s.ChunkCount} verts={s.VertexCount} buildMs={s.BuildMs} drawnChunks={s.LastDrawnChunks} drawCalls={s.LastDrawCalls} coast={s.Field.CoastOrigin}/{s.Field.CoastMode}");
            Log.Message(EndlessHorizonMod.LogPrefix + HorizonProbe.Tag + HorizonProbe.CoastReport(map, true));
        }
    }
#endif
}
