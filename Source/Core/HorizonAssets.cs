using System;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class HorizonAssets
    {
        internal static Func<Shader> FogShaderLoader;
        private static Shader fog;
        private static bool tried;

        internal static string FogState { get; private set; } = "not asked";

        internal static Shader FogShader
        {
            get
            {
                if (tried) return fog;
                tried = true;
                try
                {
                    fog = FogShaderLoader?.Invoke();
                    FogState = fog != null ? "gpu" : "cpu (no shader in the bundle)";
                }
                catch (Exception ex)
                {
                    fog = null;
                    FogState = "cpu (loading failed)";
                    Log.Error(HorizonLog.Prefix + "Loading the fog shader failed; the fog is computed on the CPU instead. " + ex);
                }
                if (fog == null) Log.Warning(HorizonLog.Prefix + "Fog shader not available (" + FogState + "); the fog is computed on the CPU.");
                return fog;
            }
        }
    }
}
