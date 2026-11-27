using EndlessHorizon.Api;
using HarmonyLib;
using Verse;

namespace EndlessHorizon
{
    internal static class Compat_RealisticRain
    {
        private const string Owner = "realisticrain";
        public static string Enable(Harmony harmony)
        {
            if (!Compat_RealisticRainCaptureTarget.Prepare()) return "ReflectionCapture changed";
            Compat_RealisticRainCaptureTarget.Skip = () => HorizonExtensions.StateOf(Owner) != "active";
            HorizonInfo.Report(Owner, "captureTarget", "repair on");
            Compat_RealisticRainOutside.Enable(harmony);
            HorizonInfo.Report(Owner, "outside", Compat_RealisticRainOutside.Detail);
            Log.Message(IntegrationLog.Prefix + "Realistic Rain outside the map: " + Compat_RealisticRainOutside.Detail);
            return null;
        }
    }
}
