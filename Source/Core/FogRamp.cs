using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class FogRamp
    {
        internal const float FrontSoftness = 8f;
        internal const float OpaqueInset = 6f;
        internal const float WispAmplitude = 9f;
        internal const float SolidFrom = 14f, SolidTo = 3f;
        internal const float HiddenAlpha = 0.97f;
        internal const int MaskMargin = 16;

        internal static float Lo(int fogStart, int boost = 0) => fogStart - FrontSoftness - boost;

        internal static float Hi(int ringWidth) => ringWidth - OpaqueInset;

        internal static float CoveredFrom(int fogStart, int ringWidth, float alpha)
        {
            float lo = Lo(fogStart), hi = Hi(ringWidth);
            float t = InverseSmoothStep(alpha);
            float ramp = hi > lo ? lo + t * (hi - lo) + WispAmplitude : hi + WispAmplitude;
            float solid = ringWidth - SolidFrom + t * (SolidFrom - SolidTo);
            return Mathf.Clamp(Mathf.Min(ramp, solid), 0f, ringWidth);
        }

        private static float InverseSmoothStep(float a)
        {
            a = Mathf.Clamp01(a);
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 20; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (mid * mid * (3f - 2f * mid) < a) lo = mid; else hi = mid;
            }
            return (lo + hi) * 0.5f;
        }

        internal static float SmoothStep(float a, float b, float v)
        {
            float t = Mathf.Clamp01((v - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }

    internal sealed class FogMask
    {
        internal const int CellsPerPx = 2;

        internal readonly int Reach, Width, Height;
        internal readonly float[] BaseAlpha;
        internal readonly float[] Solid;
        internal int Boost { get; private set; }
        private readonly float[] warped;
        private readonly int fogStart, ring;

        internal FogMask(Map map, HorizonDomain domain, int fogStart)
        {
            this.fogStart = fogStart;
            ring = domain.R;
            Reach = ring + FogRamp.MaskMargin;
            int cellsX = domain.W + 2 * Reach, cellsZ = domain.H + 2 * Reach;
            Width = Mathf.CeilToInt(cellsX / (float)CellsPerPx);
            Height = Mathf.CeilToInt(cellsZ / (float)CellsPerPx);
            BaseAlpha = new float[Width * Height];
            Solid = new float[Width * Height];
            warped = new float[Width * Height];
            int seed = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode()) ^ 0x464F47;
            var warp = new Verse.Noise.Perlin(0.035, 2.0, 0.5, 3, seed, Verse.Noise.QualityMode.Medium);
            for (int pz = 0; pz < Height; pz++)
            {
                for (int px = 0; px < Width; px++)
                {
                    float x = -Reach + (px + 0.5f) * CellsPerPx;
                    float z = -Reach + (pz + 0.5f) * CellsPerPx;
                    float d = domain.DistanceFromPoint(x, z);
                    int i = pz * Width + px;
                    if (d <= 0f)
                    {
                        warped[i] = -1000f;
                        continue;
                    }
                    float n = (float)warp.GetValue(x, 0, z);
                    warped[i] = d + n * FogRamp.WispAmplitude;
                    Solid[i] = FogRamp.SmoothStep(ring - FogRamp.SolidFrom, ring - FogRamp.SolidTo, d);
                }
            }
            SetBoost(0);
        }

        internal void SetBoost(int boost)
        {
            Boost = boost;
            float lo = FogRamp.Lo(fogStart, boost), hi = FogRamp.Hi(ring);
            for (int i = 0; i < BaseAlpha.Length; i++)
                BaseAlpha[i] = warped[i] <= -100f ? 0f : FogRamp.SmoothStep(lo, hi, warped[i]);
        }

        internal float Opacity(int x, int z)
        {
            float u = (x + 0.5f + Reach) / CellsPerPx - 0.5f, v = (z + 0.5f + Reach) / CellsPerPx - 0.5f;
            int u0 = Mathf.FloorToInt(u), v0 = Mathf.FloorToInt(v);
            float tu = u - u0, tv = v - v0;
            return Mathf.Lerp(Mathf.Lerp(Alpha(u0, v0), Alpha(u0 + 1, v0), tu), Mathf.Lerp(Alpha(u0, v0 + 1), Alpha(u0 + 1, v0 + 1), tu), tv);
        }

        private float Alpha(int px, int pz)
        {
            int i = Mathf.Clamp(pz, 0, Height - 1) * Width + Mathf.Clamp(px, 0, Width - 1);
            return Mathf.Clamp01(BaseAlpha[i] + Solid[i]);
        }
    }
}
