#if EH_DEV
using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace EndlessHorizon
{
    internal static class RainProbe
    {
        private const string Tag = "EH_RAIN ";
        private const int HoldWeatherTicks = 10 * GenDate.TicksPerDay;
        private static readonly FieldInfo WeatherDuration = AccessTools.Field(typeof(WeatherDecider), "curWeatherDuration");

        private static string N(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        private static void Run(string what, Action<Map> body)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null)
                {
                    Log.Message(HorizonLog.Prefix + Tag + "action=" + what + " failed=no map");
                    return;
                }
                body(map);
            }
            catch (Exception ex)
            {
                Log.Error(HorizonLog.Prefix + Tag + "action=" + what + " failed. " + ex);
            }
        }

        private static string Line(Map map)
        {
            WeatherManager weather = map.weatherManager;
            return $"weather={weather.curWeather?.defName ?? "-"} transition={N(weather.TransitionLerpFactor)} outdoorTemp={N(map.mapTemperature.OutdoorTemp)} "
                + Compat_RealisticRainOutside.Summary(map);
        }

        private static void Weather(string defName) => Run("weather " + defName, map =>
        {
            WeatherDef def = DefDatabase<WeatherDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Log.Message($"{HorizonLog.Prefix}{Tag}action=weather target={defName} failed=no such weather");
                return;
            }
            WeatherManager weather = map.weatherManager;
            weather.TransitionTo(def);
            weather.lastWeather = def;
            weather.curWeatherAge = (int)WeatherManager.TransitionTicks;
            bool held = WeatherDuration != null && map.weatherDecider != null;
            if (held) WeatherDuration.SetValue(map.weatherDecider, HoldWeatherTicks);
            Log.Message($"{HorizonLog.Prefix}{Tag}action=weather target={defName} held={held} | {Line(map)}");
        });

        [DebugAction("Endless Horizon", "RR outside: force rain", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Rain() => Weather("Rain");

        [DebugAction("Endless Horizon", "RR outside: force tox rain", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToxRain() => Weather("ToxRain");

        [DebugAction("Endless Horizon", "RR outside: force clear", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Clear() => Weather("Clear");

        [DebugAction("Endless Horizon", "RR outside: fill puddles (RR debug fill)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Fill() => Run("fill", map =>
        {
            MapComponent comp = Compat_RealisticRainOutside.ComponentOfMap(map);
            MethodInfo fill = Compat_RealisticRainOutside.DebugFill;
            if (comp == null || fill == null)
            {
                Log.Message($"{HorizonLog.Prefix}{Tag}action=fill failed={(comp == null ? "no Realistic Rain component" : "no DebugFill")}");
                return;
            }
            fill.Invoke(comp, new object[] { 0.5f, 1f });
            Compat_RealisticRainOutside.MarkDirty(map);
            Log.Message($"{HorizonLog.Prefix}{Tag}action=fill | {Line(map)}");
        });

        [DebugAction("Endless Horizon", "RR outside: toggle A/B", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Toggle() => Run("toggle", map =>
        {
            Compat_RealisticRainOutside.Hidden = !Compat_RealisticRainOutside.Hidden;
            Log.Message($"{HorizonLog.Prefix}{Tag}action=toggle shown={!Compat_RealisticRainOutside.Hidden} | {Line(map)}");
        });

        [DebugAction("Endless Horizon", "RR outside: log summary", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Summary() => Run("summary", map => Log.Message($"{HorizonLog.Prefix}{Tag}action=summary | {Line(map)}"));
    }
}
#endif
