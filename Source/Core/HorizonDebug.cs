using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class HorizonDebug
    {
        public static bool HideRing;
        public static bool HideFog;
        public static bool HideClouds;
        public static bool HideLighting;
        public static bool Things = true;
#if EH_DEV
        public static bool FogAlways;
        public static int FogUpdates, FogDraws, FogStaleDraws;
        public static double FogUpdateMs, FogUploadMs;
        public static bool FogCpu;
#endif
        public static bool ForceMirror;
    }
}
