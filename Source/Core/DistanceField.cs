using System;
using UnityEngine;

namespace EndlessHorizon
{
    internal static class DistanceField
    {
        internal static float[] Chamfer(int w, int h, Func<int, bool> source, float cap)
        {
            var d = new float[w * h];
            for (int i = 0; i < d.Length; i++) d[i] = source(i) ? 0f : cap;
            const float D1 = 1f, D2 = 1.4142f;
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    float v = d[i];
                    if (x > 0) v = Mathf.Min(v, d[i - 1] + D1);
                    if (z > 0)
                    {
                        v = Mathf.Min(v, d[i - w] + D1);
                        if (x > 0) v = Mathf.Min(v, d[i - w - 1] + D2);
                        if (x < w - 1) v = Mathf.Min(v, d[i - w + 1] + D2);
                    }
                    d[i] = v;
                }
            }
            for (int z = h - 1; z >= 0; z--)
            {
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = z * w + x;
                    float v = d[i];
                    if (x < w - 1) v = Mathf.Min(v, d[i + 1] + D1);
                    if (z < h - 1)
                    {
                        v = Mathf.Min(v, d[i + w] + D1);
                        if (x < w - 1) v = Mathf.Min(v, d[i + w + 1] + D2);
                        if (x > 0) v = Mathf.Min(v, d[i + w - 1] + D2);
                    }
                    d[i] = v;
                }
            }
            return d;
        }
    }
}
