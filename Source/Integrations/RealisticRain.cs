using EndlessHorizon.Api;

namespace EndlessHorizon
{
    internal static class Compat_RealisticRain
    {
        private const string Owner = "realisticrain";

        public static string Enable()
        {
            if (!Compat_RealisticRainCaptureTarget.Prepare()) return "ReflectionCapture changed";
            Compat_RealisticRainCaptureTarget.Skip = () => HorizonExtensions.StateOf(Owner) != "active";
            HorizonInfo.Report(Owner, "captureTarget", "repair on");
            return null;
        }
    }
}
