using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class HorizonDiagnostics
    {
        private const int MaxLogLines = 40;
        private const int MaxLogChars = 1500;
        private static readonly LinkedList<string> recent = new LinkedList<string>();
        private static readonly object gate = new object();
        private static readonly Regex UserPath = new Regex(@"([A-Za-z]:)?[\\/](Users|home)[\\/][^\\/\s""']+", RegexOptions.IgnoreCase);

        public static void Init(Harmony harmony)
        {
            try
            {
                foreach (string level in new[] { "Message", "Warning", "Error" })
                {
                    MethodInfo m = AccessTools.Method(typeof(Log), level, new[] { typeof(string) });
                    if (m == null) continue;
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(HorizonDiagnostics), level == "Message" ? nameof(OnMessage) : level == "Warning" ? nameof(OnWarning) : nameof(OnError)));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(EndlessHorizonMod.LogPrefix + "Diagnostics will not include recent log lines. " + ex);
            }
        }

        public static void OnMessage(string text) => Keep("I", text);
        public static void OnWarning(string text) => Keep("W", text);
        public static void OnError(string text) => Keep("E", text);

        private static void Keep(string level, string text)
        {
            if (text == null || text.StartsWith("Initializing new game with mods", StringComparison.Ordinal)) return;
            bool ours = text.StartsWith(EndlessHorizonMod.LogPrefix, StringComparison.Ordinal)
                || text.IndexOf("jongsh.endlesshorizon", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("EndlessHorizon.", StringComparison.Ordinal) >= 0;
            if (!ours) return;
            if (text.Length > MaxLogChars) text = text.Substring(0, MaxLogChars) + " …";
            string line = $"[{level} {Time.realtimeSinceStartup:0}s] {text}";
            lock (gate)
            {
                recent.AddLast(line);
                while (recent.Count > MaxLogLines) recent.RemoveFirst();
            }
        }

        public static string BuildLine(HorizonState s)
        {
            Map map = s.map;
            string mutators = string.Join(",", map.TileInfo?.Mutators?.Select(m => m.defName) ?? Enumerable.Empty<string>());
            return $"built map={map.uniqueID} size={map.Size.x}x{map.Size.z} biome={map.Biome?.defName} " +
                   $"mutators=[{mutators}] ring={s.RingWidth} fogStart={s.FogStart} chunks={s.ChunkCount} verts={s.VertexCount} buildMs={s.BuildMs} " +
                   $"coast={s.Field.CoastOrigin}/{s.Field.CoastMode} coastCells={s.Field.CoastCells} coastBoundaryCells={s.Field.CoastBoundaryCells} " +
                   $"rivers={s.Field.River?.Crossings.Count ?? -1} riverCells={s.Field.RiverCells} meander=x{RiverExtension.MeanderScale:0.##} bank=x{RiverExtension.BankScale:0.##} " +
                   $"printedThings={s.PrintedThings} fogCells={s.FogCells} rockCells={s.Mountains?.RockCells ?? -1} fogQueue={(HorizonFog.SelfLit ? "selfLit" : "lit")} " +
                   $"outside={s.Field.OutsideMode} riverPaint=[{s.Field.RiverPaintReport}] fogTint={s.Field.FogTint?.Report ?? "none"} " +
                   $"rockPrint={RockPrint.Printed}/{RockPrint.Fallbacks} rockNoDef={s.Mountains?.RockCellsWithoutDef ?? -1} weatherReimagined={(Compat_WeatherReimagined.Installed ? (s.OpenSky != null ? "openSky" : "failed") : "absent")}";
        }

        public static string Collect()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Endless Horizon diagnostics ===");
            Section(sb, "general", General);
            Section(sb, "settings", Settings);
            Section(sb, "compatibility", Compat);
            Map map = Current.ProgramState == ProgramState.Playing ? Find.CurrentMap : null;
            if (map == null)
            {
                sb.AppendLine("[map] none (open the settings from a running game, on the map with the problem)");
            }
            else
            {
                Section(sb, "map", b => MapInfo(b, map));
                Section(sb, "ring", b => Ring(b, map));
                Section(sb, "generator", b => b.AppendLine($"match: {GenReconstruction.For(map).Report}, {GenReconstruction.For(map).Ms} ms"));
                Section(sb, "coast", b => b.AppendLine(HorizonProbe.CoastReport(map, false)));
                Section(sb, "rivers", b => Rivers(b, map));
                Section(sb, "edges", b => Edges(b, map));
                Section(sb, "water and ice", b => Water(b, map));
            }
            Section(sb, "recent log", RecentLog);
            return Mask(sb.ToString());
        }

        private static void Section(StringBuilder sb, string name, Action<StringBuilder> body)
        {
            sb.AppendLine($"[{name}]");
            try
            {
                body(sb);
            }
            catch (Exception ex)
            {
                sb.AppendLine("section failed: " + ex);
            }
        }

        private static string Mask(string text) => UserPath.Replace(text, "<path>");

        private static void General(StringBuilder b)
        {
            b.AppendLine($"endlessHorizon={EndlessHorizonMod.Version} build={(EndlessHorizonMod.IsDevBuild ? "dev" : "release")} game={VersionControl.CurrentVersionStringWithRev}");
            b.AppendLine($"language={LanguageDatabase.activeLanguage?.folderName} programState={Current.ProgramState}");
            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            b.AppendLine($"dlc={string.Join(",", mods.Where(m => m.IsOfficialMod).Select(m => m.PackageId))}");
            List<ModContentPack> others = mods.Where(m => !m.IsOfficialMod).ToList();
            b.AppendLine($"mods({others.Count})={string.Join(",", others.Select(m => m.PackageId))}");
        }

        private static void Settings(StringBuilder b)
        {
            HorizonSettings s = EndlessHorizonMod.Settings;
            var parts = typeof(HorizonSettings).GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name + "=" + f.GetValue(s));
            b.AppendLine(string.Join(" ", parts));
            b.AppendLine($"debug: hideRing={HorizonDebug.HideRing} hideFog={HorizonDebug.HideFog} hideClouds={HorizonDebug.HideClouds} hideLighting={HorizonDebug.HideLighting} forceMirror={HorizonDebug.ForceMirror}");
        }

        private static void Compat(StringBuilder b)
        {
            foreach (CompatEntry e in CompatRegistry.Entries)
            {
                b.AppendLine($"{e.Name} ({e.PackageId}): {e.State}{(e.Reason.Length > 0 ? " — " + e.Reason.Replace('\n', ' ') : "")}");
            }
        }

        private static void MapInfo(StringBuilder b, Map map)
        {
            string mutators = string.Join(",", map.TileInfo?.Mutators?.Select(m => m.defName) ?? Enumerable.Empty<string>());
            b.AppendLine($"id={map.uniqueID} size={map.Size.x}x{map.Size.z} tile={map.Tile} biome={map.Biome?.defName} mutators=[{mutators}] hilliness={map.TileInfo?.hilliness}");
            b.AppendLine($"generatedThisSession={CoastNoise.Captured(map) != null || CoastNoise.CoastWorkerOf(map) == null} ticks={Find.TickManager.TicksGame} season={GenLocalDate.Season(map)} " +
                         $"outdoorTemp={map.mapTemperature.OutdoorTemp:0.0} weather={map.weatherManager.curWeather?.defName} snowTotal={map.snowGrid.TotalDepth:0}");
            b.AppendLine($"pocketMap={map.IsPocketMap} parent={map.Parent?.GetType().Name}");
        }

        private static void Ring(StringBuilder b, Map map)
        {
            bool active = HorizonRegistry.IsActive(map);
            HorizonState s = HorizonRegistry.Peek(map);
            b.AppendLine($"active={active} state={(s == null ? "none" : s.Built ? "built" : "building")}");
            if (s == null || s.Field == null) return;
            b.AppendLine(BuildLine(s));
            b.AppendLine($"outsideGlow: mapLights={s.Glow?.MapLightCount ?? -1} outsideLights={s.Glow?.OutsideLightCount ?? -1}");
        }

        private static void Rivers(StringBuilder b, Map map)
        {
            HorizonState s = HorizonRegistry.Peek(map);
            if (s?.Field?.River == null)
            {
                b.AppendLine("no river extension");
                return;
            }
            foreach (RiverCrossing c in s.Field.River.Crossings) b.AppendLine(c.ToString());
        }

        private static void Edges(StringBuilder b, Map map)
        {
            HorizonState s = HorizonRegistry.Peek(map);
            int w = map.Size.x, h = map.Size.z;
            foreach ((string side, Func<int, int, IntVec3> at, int length) in new (string, Func<int, int, IntVec3>, int)[]
            {
                ("south", (i, d) => new IntVec3(i, 0, -d), w), ("north", (i, d) => new IntVec3(i, 0, h - 1 + d), w),
                ("west", (i, d) => new IntVec3(-d, 0, i), h), ("east", (i, d) => new IntVec3(w - 1 + d, 0, i), h),
            })
            {
                string Row(int d)
                {
                    var count = new Dictionary<string, int>();
                    for (int i = 0; i < length; i++)
                    {
                        IntVec3 c = at(i, d);
                        TerrainDef t = d == 0 ? map.terrainGrid.TerrainAt(c) : s?.Field?.TopAt(c);
                        bool rock = d == 0 ? c.GetEdifice(map)?.def.mineable == true : s?.Mountains?.IsRock(c.x, c.z) == true;
                        string key = rock ? "rock" : t?.defName ?? "null";
                        count[key] = count.TryGetValue(key, out int n) ? n + 1 : 1;
                    }
                    return string.Join(" ", count.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key + ":" + kv.Value));
                }
                b.AppendLine($"{side}: map[{Row(0)}] out1[{Row(1)}] out5[{Row(5)}]");
            }
        }

        private static void Water(StringBuilder b, Map map)
        {
            List<WaterBody> bodies = map.waterBodyTracker?.Bodies;
            int count = bodies?.Count ?? 0, canFreeze = 0, frozen = 0;
            if (bodies != null)
            {
                foreach (WaterBody w in bodies)
                {
                    if (w == null) continue;
                    canFreeze += w.numCellsCanFreeze;
                    frozen += w.numCellsFrozen;
                }
            }
            b.AppendLine($"odyssey={ModsConfig.OdysseyActive} outsideIce={OutsideIce.Enabled} waterBodies={count} canFreeze={canFreeze} frozen={frozen}");
            HorizonState s = HorizonRegistry.Peek(map);
            OutsideIce ice = s?.Field?.Ice;
            if (ice != null)
            {
                float t = ice.TemperatureRatioNow;
                string temperature = t < 0f ? "unused (map water decides)" : t.ToString("0.##");
                b.AppendLine($"outside: groups={ice.GroupCount} frozenCells={s.Field.FrozenCount} temperatureRatio={temperature}");
            }
        }

        private static void RecentLog(StringBuilder b)
        {
            lock (gate)
            {
                if (recent.Count == 0) b.AppendLine("(none)");
                foreach (string line in recent) b.AppendLine(line);
            }
        }
    }
}
