using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    [HarmonyPatch]
    internal static class Compat_RealisticRainCaptureTarget
    {
        private const string Prefix = "[Endless Horizon] ";

        internal static Func<bool> Skip = null;

        public static MethodBase TargetMethod() => AccessTools.Method("RealisticRain.ReflectionCapture:Update",
            new[] { typeof(Map), typeof(bool) });

        public static bool Prepare()
        {
            Type type = AccessTools.TypeByName("RealisticRain.ReflectionCapture");
            return type != null && TargetMethod() != null
                && AccessTools.Field(type, "cam")?.FieldType == typeof(Camera)
                && AccessTools.Field(type, "tex")?.FieldType == typeof(RenderTexture)
                && AccessTools.Field(type, "fresh")?.FieldType == typeof(bool);
        }

        public static void Postfix(Camera ___cam, RenderTexture ___tex, ref bool ___fresh)
        {
            if (___cam == null || ___tex == null || ___cam.targetTexture == ___tex || Skip?.Invoke() == true)
            {
                return;
            }
            ___cam.targetTexture = ___tex;
            ___fresh = false;
            Log.Message(Prefix + "Realistic Rain reflection: restored capture camera target after recreation.");
        }
    }
}
