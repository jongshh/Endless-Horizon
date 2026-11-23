using System;
using System.Collections.Generic;
using System.Reflection;

namespace EndlessHorizon
{
    internal sealed class WaterSeam
    {
        private readonly int w, h;
        private readonly FieldShape shape;
        private readonly List<(int x, int z)> corners;
        private readonly Dictionary<(int, int), int> cornerIndex;
        private readonly Dictionary<string, float[]> values = new Dictionary<string, float[]>();

        public WaterSeam(int width, int height) { w = width; h = height; }

        public WaterSeam(FieldShape shape) : this(shape.W, shape.H)
        {
            if (shape.Rectangle) return;
            this.shape = shape;
            corners = new List<(int x, int z)>();
            cornerIndex = new Dictionary<(int, int), int>();
            for (int z = 0; z <= h; z++)
                for (int x = 0; x <= w; x++)
                {
                    if (!shape.IsEdge(x, z)) continue;
                    cornerIndex[(x, z)] = corners.Count;
                    corners.Add((x, z));
                }
        }

        public bool IsEdge(int x, int z) => shape != null ? shape.IsEdge(x, z)
            : x >= 0 && x <= w && z >= 0 && z <= h && (x == 0 || x == w || z == 0 || z == h);

        private int Index(int x, int z)
        {
            if (z == 0) return x;
            if (z == h) return w + 1 + x;
            return 2 * (w + 1) + (x == 0 ? 0 : h - 1) + z - 1;
        }

        public void Capture(object field, params string[] names)
        {
            foreach (string name in names)
            {
                MethodInfo method = field.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(int), typeof(int) }, null);
                if (method == null || (method.ReturnType != typeof(float) && method.ReturnType != typeof(bool) && method.ReturnType != typeof(byte)))
                    throw new MissingMethodException(field.GetType().FullName, name);
                if (shape != null)
                {
                    var found = new float[corners.Count];
                    for (int i = 0; i < found.Length; i++) found[i] = Convert.ToSingle(method.Invoke(field, new object[] { corners[i].x, corners[i].z }));
                    values.Add(name, found);
                    continue;
                }
                var data = new float[2 * (w + 1) + 2 * (h - 1)];
                for (int x = 0; x <= w; x++) { Read(x, 0); Read(x, h); }
                for (int z = 1; z < h; z++) { Read(0, z); Read(w, z); }
                values.Add(name, data);
                void Read(int x, int z) => data[Index(x, z)] = Convert.ToSingle(method.Invoke(field, new object[] { x, z }));
            }
        }

        public float Value(string name, int x, int z)
        {
            if (shape == null) return values[name][Index(Math.Clamp(x, 0, w), Math.Clamp(z, 0, h))];
            return cornerIndex.TryGetValue((x, z), out int i) ? values[name][i] : 0f;
        }

        public float Connect(string name, int x, int z, Func<int, int, float> raw)
        {
            if (IsEdge(x, z)) return Value(name, x, z);
            int bx = Math.Clamp(x, 0, w), bz = Math.Clamp(z, 0, h);
            if (shape != null && !shape.TryNearestEdge(x, z, 8, out bx, out bz)) return raw(x, z);
            float t = Math.Clamp(Math.Max(Math.Abs(x - bx), Math.Abs(z - bz)) / 8f, 0f, 1f);
            float result = raw(x, z);
            if (t >= 1f) return result;
            return result + (Value(name, bx, bz) - raw(bx, bz)) * (1f - t * t * (3f - 2f * t));
        }
    }

    internal static class WaterFieldMath
    {
        public static void Blur(float[] data, int w, int h)
        {
            var copy = (float[])data.Clone();
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int zz = Math.Max(0, z - 1); zz <= Math.Min(h - 1, z + 1); zz++)
                        for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++)
                        { sum += copy[zz * w + xx]; count++; }
                    data[z * w + x] = sum / count;
                }
        }

        public static void WideBlur(float[] data, int w, int h)
        {
            var copy = (float[])data.Clone();
            for (int z = 0; z < h; z++)
            {
                float sum = 0f;
                for (int x = -9; x <= 9; x++) sum += copy[z * w + Math.Clamp(x, 0, w - 1)];
                for (int x = 0; x < w; x++)
                {
                    data[z * w + x] = sum / 19f;
                    sum += copy[z * w + Math.Min(x + 10, w - 1)] - copy[z * w + Math.Max(x - 9, 0)];
                }
            }
            copy = (float[])data.Clone();
            for (int x = 0; x < w; x++)
            {
                float sum = 0f;
                for (int z = -9; z <= 9; z++) sum += copy[Math.Clamp(z, 0, h - 1) * w + x];
                for (int z = 0; z < h; z++)
                {
                    data[z * w + x] = sum / 19f;
                    sum += copy[Math.Min(z + 10, h - 1) * w + x] - copy[Math.Max(z - 9, 0) * w + x];
                }
            }
        }

        public static float WaveDistance(float corner, float calm)
        {
            float t = Math.Clamp((corner - 1f) / 9f, 0f, 1f);
            return corner + (Math.Max(calm, 0f) - corner) * (t * t * (3f - 2f * t));
        }
    }
}
