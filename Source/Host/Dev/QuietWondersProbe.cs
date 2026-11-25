#if EH_DEV
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class QuietWondersProbe
    {
        private const string CountTag = "EH_QWCOUNT ", FleckTag = "EH_QWFLECKS ", BoxTag = "EH_QWFLECKBOX ", ForceTag = "EH_QWFORCE ";
        private const string TimeTag = "EH_QWTIME ";
        private const string QuietWondersId = "choco.quietwonders", DefPrefix = "QW_";
        private const float NearCells = 3f;
        private const int HoldWeatherTicks = 10 * GenDate.TicksPerDay;

        private sealed class Tally
        {
            internal int Inside, Outside, Near;
            internal int MinX = int.MaxValue, MinZ = int.MaxValue, MaxX = int.MinValue, MaxZ = int.MinValue;
        }

        private sealed class Cost
        {
            internal string Name;
            internal long Total, Max;
            internal int Calls, Slow;
        }

        private static readonly string[][] Timed =
        {
            new[] { "QWPlaces", "GroundDrift" }, new[] { "QWEffects", "Butterflies" }, new[] { "QWClassics", "Fireflies" },
            new[] { "QWPond", "Dragonflies" }, new[] { "QWPond", "Frogs" }, new[] { "QWPond", "Fish" },
        };

        private static readonly Dictionary<FleckDef, Tally> tallies = new Dictionary<FleckDef, Tally>();
        private static readonly Dictionary<MethodBase, Cost> costs = new Dictionary<MethodBase, Cost>();
        private static readonly string[] PondMethods = { "Frogs", "FindBanks", "Fish", "FindWater" };
        private const string EhHarmonyId = "jongsh.endlesshorizon";
        private static int startFrame;
        private static readonly FieldInfo WeatherDuration = AccessTools.Field(typeof(WeatherDecider), "curWeatherDuration");
        private static readonly MethodInfo BaseWind = AccessTools.Method(typeof(WindManager), "BaseWindSpeedAt", new[] { typeof(int) });
        private static Harmony harmony;
        private static bool counting, everyDef;
        private static Map countMap;
        private static HorizonDomain mapOnly;
        private static float startTime;
        private static int startTick;
        private static int calls, callsOnMap;

        private static string N(float value, string format = "0.0") => value.ToString(format, CultureInfo.InvariantCulture);

        private static void Run(string tag, string what, Action<Map> body)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null)
                {
                    Log.Message(HorizonLog.Prefix + tag + what + ": no map");
                    return;
                }
                body(map);
            }
            catch (Exception ex)
            {
                Log.Error(HorizonLog.Prefix + tag + what + " failed. " + ex);
            }
        }

        private static bool Patched()
        {
            if (harmony != null) return true;
            MethodInfo create = AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck), new[] { typeof(FleckCreationData) });
            if (create == null)
            {
                Log.Error(HorizonLog.Prefix + CountTag + "FleckManager.CreateFleck(FleckCreationData) was not found; flecks cannot be counted.");
                return false;
            }
            var made = new Harmony("jongsh.endlesshorizon.dev.quietwonders");
            made.Patch(create, postfix: new HarmonyMethod(typeof(QuietWondersProbe), nameof(Created)));
            harmony = made;
            var before = new HarmonyMethod(typeof(QuietWondersProbe), nameof(TimeBefore)) { priority = Priority.First };
            var after = new HarmonyMethod(typeof(QuietWondersProbe), nameof(TimeAfter)) { priority = Priority.Last };
            foreach (string[] timed in Timed)
            {
                Type type = AccessTools.TypeByName("QuietWonders." + timed[0]) ?? AccessTools.TypeByName(timed[0]);
                if (type == null) continue;
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method.Name != timed[1] || method.IsAbstract || method.IsGenericMethodDefinition) continue;
                    try
                    {
                        made.Patch(method, prefix: before, postfix: after);
                        costs[method] = new Cost { Name = timed[1] };
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"{HorizonLog.Prefix}{TimeTag}{timed[0]}.{timed[1]} cannot be timed. {ex}");
                    }
                }
            }
            return true;
        }

        private static void TimeBefore(out long __state) => __state = counting ? Stopwatch.GetTimestamp() : 0L;

        private static void TimeAfter(MethodBase __originalMethod, long __state)
        {
            if (__state == 0L || !counting || !costs.TryGetValue(__originalMethod, out Cost cost)) return;
            long took = Stopwatch.GetTimestamp() - __state;
            cost.Total += took;
            cost.Calls++;
            if (took > cost.Max) cost.Max = took;
            if (took >= Stopwatch.Frequency / 1000) cost.Slow++;
        }

        private static HorizonDomain DomainOf(Map map) =>
            HorizonRegistry.Peek(map)?.Domain ?? (mapOnly ??= new HorizonDomain(map.Size.x, map.Size.z, 0));

        private static void Created(FleckManager __instance, ref FleckCreationData __0)
        {
            if (!counting) return;
            try
            {
                calls++;
                FleckDef def = __0.def;
                if (def == null || __instance.parent != countMap) return;
                callsOnMap++;
                if (!everyDef && !def.defName.StartsWith(DefPrefix, StringComparison.Ordinal)) return;
                if (!tallies.TryGetValue(def, out Tally tally)) tallies.Add(def, tally = new Tally());
                int x = Mathf.FloorToInt(__0.spawnPosition.x), z = Mathf.FloorToInt(__0.spawnPosition.z);
                HorizonDomain domain = DomainOf(countMap);
                if (domain.Inside(x, z))
                {
                    tally.Inside++;
                    return;
                }
                tally.Outside++;
                if (domain.Distance(x, z) <= NearCells) tally.Near++;
                if (x < tally.MinX) tally.MinX = x;
                if (z < tally.MinZ) tally.MinZ = z;
                if (x > tally.MaxX) tally.MaxX = x;
                if (z > tally.MaxZ) tally.MaxZ = z;
            }
            catch (Exception ex)
            {
                counting = false;
                Log.Error(HorizonLog.Prefix + CountTag + "counting a fleck failed; the count stopped. " + ex);
            }
        }

        [DebugAction("Endless Horizon", "QW flecks: start count", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void StartCount() => Run(CountTag, "start", map =>
        {
            if (!Patched()) return;
            counting = false;
            tallies.Clear();
            mapOnly = null;
            calls = callsOnMap = 0;
            foreach (Cost cost in costs.Values)
            {
                cost.Total = cost.Max = 0L;
                cost.Calls = cost.Slow = 0;
            }
            startFrame = Time.frameCount;
            countMap = map;
            startTime = Time.realtimeSinceStartup;
            startTick = Find.TickManager.TicksGame;
            counting = true;
            Log.Message($"{HorizonLog.Prefix}{CountTag}started everyDef={everyDef} ring={HorizonRegistry.Peek(map)?.Domain != null} "
                + $"quietWonders={ModsConfig.IsActive(QuietWondersId)}");
        });

        [DebugAction("Endless Horizon", "QW time: frogs and fish without EH (until restart)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PondBaseline() => Run(TimeTag, "baseline", map =>
        {
            if (!Patched()) return;
            Type pond = AccessTools.TypeByName("QuietWonders.QWPond") ?? AccessTools.TypeByName("QWPond");
            var removed = new List<string>();
            if (pond != null)
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(pond))
                {
                    if (Array.IndexOf(PondMethods, method.Name) < 0) continue;
                    Patches patches = Harmony.GetPatchInfo(method);
                    if (patches == null || !patches.Owners.Contains(EhHarmonyId)) continue;
                    harmony.Unpatch(method, HarmonyPatchType.All, EhHarmonyId);
                    removed.Add(method.Name);
                }
            Log.Message($"{HorizonLog.Prefix}{TimeTag}baseline removed=[{string.Join(",", removed)}]");
        });

        [DebugAction("Endless Horizon", "QW time: API reads (ns per call)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ApiReadCost() => Run(TimeTag, "api", map =>
        {
            var outside = new List<IntVec3>();
            var inside = new List<IntVec3>();
            foreach (IntVec3 c in Api.HorizonSpace.Bounds(map))
            {
                if (Api.HorizonSpace.IsPlayable(map, c)) inside.Add(c);
                else if (Api.HorizonSpace.IsOutside(map, c) && Api.HorizonSpace.DistanceFromPlayable(map, c) <= 46f) outside.Add(c);
            }
            var reads = new (string Name, Action<IntVec3> Read)[]
            {
                ("IsPlayable", c => Api.HorizonSpace.IsPlayable(map, c)),
                ("IsOutside", c => Api.HorizonSpace.IsOutside(map, c)),
                ("NearestPlayable", c => Api.HorizonSpace.NearestPlayable(map, c)),
                ("TerrainAt", c => Api.HorizonWorld.TerrainAt(map, c)),
                ("BaseTerrainAt", c => Api.HorizonWorld.BaseTerrainAt(map, c)),
                ("RockAt", c => Api.HorizonWorld.RockAt(map, c)),
                ("RoofAt", c => Api.HorizonWorld.RoofAt(map, c)),
                ("FoggedAt", c => Api.HorizonWorld.FoggedAt(map, c)),
                ("PlantAt", c => Api.HorizonWorld.PlantAt(map, c)),
                ("Value(surface.snow)", c => Api.HorizonWorld.Value(map, "surface.snow", c)),
                ("Value(water.kind)", c => Api.HorizonWorld.Value(map, "water.kind", c)),
            };
            var line = new StringBuilder($"{HorizonLog.Prefix}{TimeTag}api outsideCells={outside.Count} insideCells={inside.Count} | name:outside/inside ns");
            double toNs = 1e9 / Stopwatch.Frequency;
            foreach ((string name, Action<IntVec3> read) in reads)
            {
                line.Append($" {name}:");
                foreach (List<IntVec3> cells in new[] { outside, inside })
                {
                    if (cells.Count == 0)
                    {
                        line.Append("-/");
                        continue;
                    }
                    long best = long.MaxValue;
                    for (int pass = 0; pass < 3; pass++)
                    {
                        long start = Stopwatch.GetTimestamp();
                        for (int i = 0; i < cells.Count; i++) read(cells[i]);
                        best = Math.Min(best, Stopwatch.GetTimestamp() - start);
                    }
                    line.Append(N((float)(best * toNs / cells.Count), "0")).Append('/');
                }
                line.Length--;
            }
            Log.Message(line.ToString());
        });

        [DebugAction("Endless Horizon", "QW flecks: count every def", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CountEveryDef()
        {
            everyDef = true;
            Log.Message($"{HorizonLog.Prefix}{CountTag}every def counting={counting}");
        }

        [DebugAction("Endless Horizon", "QW flecks: log and stop", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogAndStop()
        {
            try
            {
                counting = false;
                Map map = countMap;
                if (map == null)
                {
                    Log.Message(HorizonLog.Prefix + FleckTag + "none (no count was started)");
                    return;
                }
                float seconds = Time.realtimeSinceStartup - startTime;
                int ticks = Find.TickManager.TicksGame - startTick;
                HorizonDomain domain = DomainOf(map);
                CellRect view = ExtensionMaps.ViewOf(map);
                int inArea = 0, outArea = 0;
                if (view.Width > 0 && view.Height > 0)
                {
                    for (int z = view.minZ; z <= view.maxZ; z++)
                        for (int x = view.minX; x <= view.maxX; x++)
                        {
                            if (domain.Inside(x, z)) inArea++;
                            else if (domain.Drawn(x, z)) outArea++;
                        }
                }
                var defs = new List<FleckDef>(tallies.Keys);
                defs.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));
                var line = new StringBuilder($"{HorizonLog.Prefix}{FleckTag}seconds={N(seconds, "0.00")} ticks={ticks} inArea={inArea} outArea={outArea} |");
                var boxes = new StringBuilder(HorizonLog.Prefix + BoxTag);
                bool anyBox = false;
                foreach (FleckDef def in defs)
                {
                    Tally tally = tallies[def];
                    line.Append($" {def.defName}:{tally.Inside}/{tally.Outside}/{tally.Near}");
                    if (tally.Outside <= 0) continue;
                    boxes.Append(anyBox ? "; " : "").Append($"{def.defName}={tally.MinX},{tally.MinZ},{tally.MaxX},{tally.MaxZ}");
                    anyBox = true;
                }
                if (defs.Count == 0) line.Append(" none");
                if (!anyBox) boxes.Append("none");
                Log.Message($"{HorizonLog.Prefix}{CountTag}stopped calls={calls} onMap={callsOnMap} defs={defs.Count} everyDef={everyDef}");
                Log.Message(line.ToString());
                Log.Message(boxes.ToString());
                int frames = Mathf.Max(1, Time.frameCount - startFrame);
                double toMs = 1000.0 / Stopwatch.Frequency, all = 0.0;
                var time = new StringBuilder();
                foreach (Cost cost in costs.Values)
                {
                    all += cost.Total * toMs;
                    time.Append($" {cost.Name}:{cost.Calls}/{N((float)(cost.Total * toMs / frames), "0.0000")}/{N((float)(cost.Max * toMs), "0.000")}/{cost.Slow}");
                }
                Log.Message($"{HorizonLog.Prefix}{TimeTag}frames={frames} msPerFrame={N((float)(all / frames), "0.0000")} "
                    + $"view={view.Width}x{view.Height} |{(costs.Count == 0 ? " none" : time.ToString())}");
            }
            catch (Exception ex)
            {
                Log.Error(HorizonLog.Prefix + FleckTag + "logging the fleck count failed. " + ex);
            }
            finally
            {
                counting = false;
                everyDef = false;
                countMap = null;
                mapOnly = null;
                tallies.Clear();
            }
        }

        private static string Conditions(Map map)
        {
            WeatherManager weather = map.weatherManager;
            bool both = weather.curWeather != null && weather.lastWeather != null;
            return $"weather={weather.curWeather?.defName ?? "-"} lastWeather={weather.lastWeather?.defName ?? "-"} "
                + $"transition={N(weather.TransitionLerpFactor, "0.00")} rainRate={(both ? N(weather.RainRate, "0.00") : "-")} "
                + $"snowRate={(both ? N(weather.SnowRate, "0.00") : "-")} wind={N(map.windManager.WindSpeed, "0.00")} "
                + $"outdoorTemp={N(map.mapTemperature.OutdoorTemp)} hour={N(GenLocalDate.HourFloat(map))} skyGlow={N(map.skyManager.CurSkyGlow, "0.00")} "
                + $"season={GenLocalDate.Season(map)} "
                + $"dayOfYear={GenLocalDate.DayOfYear(map)} snowCentre={N(map.snowGrid.GetDepth(map.Center), "0.00")} "
                + $"snowMean={N(map.snowGrid.TotalDepth / Mathf.Max(1, map.Area), "0.00")} ticksGame={Find.TickManager.TicksGame} "
                + $"quietWonders={ModsConfig.IsActive(QuietWondersId)}";
        }

        private static void ForceWeather(string defName) => Run(ForceTag, "weather " + defName, map =>
        {
            WeatherDef def = DefDatabase<WeatherDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Log.Message($"{HorizonLog.Prefix}{ForceTag}action=weather target={defName} failed=no such weather");
                return;
            }
            WeatherManager weather = map.weatherManager;
            weather.TransitionTo(def);
            weather.lastWeather = def;
            weather.curWeatherAge = (int)WeatherManager.TransitionTicks;
            bool held = WeatherDuration != null && map.weatherDecider != null;
            if (held) WeatherDuration.SetValue(map.weatherDecider, HoldWeatherTicks);
            Log.Message($"{HorizonLog.Prefix}{ForceTag}action=weather target={defName} held={held} | {Conditions(map)}");
        });

        [DebugAction("Endless Horizon", "Force: weather SnowHard", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WeatherSnowHard() => ForceWeather("SnowHard");

        [DebugAction("Endless Horizon", "Force: weather Clear", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WeatherClear() => ForceWeather("Clear");

        [DebugAction("Endless Horizon", "Force: weather Rain", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WeatherRain() => ForceWeather("Rain");

        [DebugAction("Endless Horizon", "Force: snow depth 0.6", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SnowDepth() => Run(ForceTag, "snow", map =>
        {
            const float depth = 0.6f;
            int covered = 0;
            foreach (IntVec3 c in map.AllCells)
            {
                if (c.Roofed(map)) continue;
                TerrainDef terrain = c.GetTerrain(map);
                if (terrain == null || terrain.IsWater) continue;
                map.snowGrid.SetDepth(c, depth);
                if (map.snowGrid.GetDepth(c) > 0f) covered++;
            }
            HorizonState state = HorizonRegistry.Peek(map);
            bool refresh = state != null && state.Built;
            if (refresh) state.RequestRefresh();
            Log.Message($"{HorizonLog.Prefix}{ForceTag}action=snow depth={N(depth, "0.00")} cells={covered} outsideRefresh={refresh} | {Conditions(map)}");
        });

        private static int NameColony()
        {
            int named = 0;
            Faction player = Faction.OfPlayer;
            if (!player.HasName)
            {
                player.Name = "Endless Horizon test";
                named++;
            }
            foreach (Settlement settlement in Find.WorldObjects.Settlements)
                if (settlement.Faction == player && !settlement.namedByPlayer)
                {
                    settlement.namedByPlayer = true;
                    named++;
                }
            return named;
        }

        private static void ForceDate(Season season, int hour, string name) => Run(ForceTag, "date " + name, map =>
        {
            Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
            Twelfth twelfth = season.GetMiddleTwelfth(longLat.y);
            if (twelfth == Twelfth.Undefined)
            {
                Log.Message($"{HorizonLog.Prefix}{ForceTag}action=date target={name} failed=no twelfth for {season}");
                return;
            }
            long target = (long)(int)twelfth * GenDate.TicksPerTwelfth + (long)(GenDate.DaysPerTwelfth / 2) * GenDate.TicksPerDay
                + (long)hour * GenDate.TicksPerHour;
            long local = GenTicks.TicksAbs + GenDate.LocalTicksOffsetFromLongitude(longLat.x);
            int ahead = (int)GenMath.PositiveMod(target - local, (long)GenDate.TicksPerYear);
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ahead);
            Find.World.tileTemperatures.ClearCaches();
            int named = NameColony();
            Log.Message($"{HorizonLog.Prefix}{ForceTag}action=date target={name} twelfth={twelfth} latitude={N(longLat.y)} "
                + $"advancedTicks={ahead} named={named} | {Conditions(map)}");
        });

        [DebugAction("Endless Horizon", "Force: date midsummer 13h", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MidsummerDay() => ForceDate(Season.Summer, 13, "midsummer13h");

        [DebugAction("Endless Horizon", "Force: date midsummer 1h", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MidsummerNight() => ForceDate(Season.Summer, 1, "midsummer1h");

        [DebugAction("Endless Horizon", "Force: date midwinter 13h", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MidwinterDay() => ForceDate(Season.Winter, 13, "midwinter13h");

        [DebugAction("Endless Horizon", "Force: date midsummer 20h", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MidsummerEvening() => ForceDate(Season.Summer, 20, "midsummer20h");

        [DebugAction("Endless Horizon", "Force: time to strong wind", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void StrongWind() => Run(ForceTag, "wind", map =>
        {
            if (BaseWind == null)
            {
                Log.Message($"{HorizonLog.Prefix}{ForceTag}action=wind failed=no WindManager.BaseWindSpeedAt");
                return;
            }
            const int step = 250, window = 8000, search = 6 * GenDate.TicksPerDay;
            float factor = map.weatherManager.CurWindSpeedFactor, longitude = Find.WorldGrid.LongLatOf(map.Tile).x;
            int now = Find.TickManager.TicksAbs, span = window / step;
            var wind = new float[(search + window) / step + 1];
            var at = new object[1];
            for (int i = 0; i < wind.Length; i++)
            {
                at[0] = now + i * step;
                wind[i] = (float)BaseWind.Invoke(map.windManager, at) * factor;
            }
            int best = -1;
            float weakest = 0f;
            for (int i = 0; i + span < wind.Length; i++)
            {
                int hour = GenDate.HourOfDay(now + (long)i * step, longitude);
                if (hour < 9 || hour > 14) continue;
                float min = float.MaxValue;
                for (int k = 0; k <= span; k++) min = Mathf.Min(min, wind[i + k]);
                if (min <= weakest) continue;
                weakest = min;
                best = i;
            }
            if (best < 0)
            {
                Log.Message($"{HorizonLog.Prefix}{ForceTag}action=wind failed=no daytime stretch | {Conditions(map)}");
                return;
            }
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + best * step);
            Find.World.tileTemperatures.ClearCaches();
            int named = NameColony();
            Log.Message($"{HorizonLog.Prefix}{ForceTag}action=wind advancedTicks={best * step} weakestOver{window}={N(weakest, "0.00")} "
                + $"factor={N(factor, "0.00")} named={named} | {Conditions(map)}");
        });

        [DebugAction("Endless Horizon", "Force: QW frogs and fish every 2 s", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void FrequentPond() => Run(ForceTag, "pond", map =>
        {
            Type pond = AccessTools.TypeByName("QuietWonders.QWPond") ?? AccessTools.TypeByName("QWPond");
            var set = new List<string>();
            foreach (string name in new[] { "DebugFrogEvery", "DebugFishEvery" })
            {
                MethodInfo method = pond == null ? null : AccessTools.Method(pond, name, new[] { typeof(float) });
                if (method == null || !method.IsStatic) continue;
                method.Invoke(null, new object[] { 2f });
                set.Add(name);
            }
            Log.Message($"{HorizonLog.Prefix}{ForceTag}action=pond set=[{string.Join(",", set)}] | {Conditions(map)}");
        });

        [DebugAction("Endless Horizon", "Force: log conditions", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogConditions() => Run(ForceTag, "conditions", map =>
            Log.Message($"{HorizonLog.Prefix}{ForceTag}action=conditions | {Conditions(map)}"));
    }
}
#endif
