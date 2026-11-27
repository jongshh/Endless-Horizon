using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal static class RealisticRainRules
    {

        internal const int NorthEast = 0, West = 1, South = 2, SouthWest = 3;

        internal static readonly string[] QuadrantNames = { "north-east", "west", "south", "south-west" };

        internal static int QuadrantOf(int x, int z) => (x < 0 ? West : 0) | (z < 0 ? South : 0);

        internal static bool OneQuadrant(CellRect r) => (r.minX >= 0 || r.maxX < 0) && (r.minZ >= 0 || r.maxZ < 0);

        internal static void TextureSize(int quadrant, int west, int south, int east, int north, out int width, out int height)
        {
            width = (quadrant & West) != 0 ? west : east;
            height = (quadrant & South) != 0 ? south : north;
        }

        internal static int TexelOf(int x, int z, int textureWidth) => (z < 0 ? -1 - z : z) * textureWidth + (x < 0 ? -1 - x : x);

        internal static Vector4 MapSizeOf(int quadrant, int width, int height) =>
            new Vector4(width, height, ((quadrant & West) != 0 ? -1f : 1f) / width, ((quadrant & South) != 0 ? -1f : 1f) / height);

        internal static void Coverage(int w, int h, int reach, int block, out int west, out int south, out int east, out int north)
        {
            west = south = -FloorTo(-reach, block);
            east = FloorTo(w + reach - 1, block) + block;
            north = FloorTo(h + reach - 1, block) + block;
        }

        internal static int FloorTo(int v, int step) => (int)Math.Floor(v / (double)step) * step;

        internal static (int x, int z) MaskCell(int x, int z) => (x < 0 ? -1 : x, z < 0 ? -1 : z);

        internal static Color32 Encode(float water, float wet, bool open, float factor) => new Color32(
            (byte)(Mathf.Clamp01(water) * 255f + 0.5f), (byte)(Mathf.Clamp01(wet) * 255f + 0.5f),
            (byte)(open && factor < 0f ? 255 : 0), (byte)(open && factor != 0f ? 255 : 0));

        internal const double BasinFrequency = 0.045, BasinLacunarity = 2.0, BasinPersistence = 0.5;
        internal const int BasinOctaves = 3;
        internal const float BasinBase = 1f, BasinAmplitude = 0.85f, BasinLow = 0.35f, BasinHigh = 1.8f;

        internal static Perlin BasinNoise(int constantRandSeed) =>
            new Perlin(BasinFrequency, BasinLacunarity, BasinPersistence, BasinOctaves, constantRandSeed, QualityMode.Medium);

        internal static float BasinAt(Perlin noise, int x, int z) =>
            Mathf.Clamp(BasinBase + BasinAmplitude * (float)noise.GetValue(x, 0.0, z), BasinLow, BasinHigh);

        internal const int Buckets = 16, SnowLevels = 3;

        internal static float BucketOf(float basin) => Mathf.Clamp((basin - BasinLow) / (BasinHigh - BasinLow), 0f, 1f) * (Buckets - 1);

        internal static int SnowLevel(float depth) => !(depth > 0.03f) ? 0 : depth < 0.15f ? 1 : 2;

        internal static float Fade(float distance, int band, int end)
        {
            if (float.IsNaN(distance) || distance > end) return 0f;
            if (distance <= band) return 1f;
            float t = (distance - band) / Math.Max(1, end - band);
            return 1f - t * t * (3f - 2f * t);
        }

        internal static readonly string[] FragmentLines =
        {
            "vec2 w = vWorld.xz;",
            "vec4 d = texture(_DataTex, vec2(w.x * u._MapSize.z, 1.0 - w.y * u._MapSize.w));",
            "ivec2 size = ivec2(u._MapSize.xy);",
            "ivec2 cell = clamp(ivec2(floor(w)), ivec2(0), size - 1);",
            "if (texelFetch(_DataTex, ivec2(cell.x, size.y - 1 - cell.y), 0).a < 0.5)",
        };

        internal static readonly string[] VertexLines =
        {
            "vec4 world = push.modelMatrix * vec4(inPosition, 1.0);",
            "vWorld = world.xyz;",
        };

        internal static string FragmentProblem(string text)
        {
            if (string.IsNullOrEmpty(text)) return "RR_Puddle.frag.glsl not found";
            var lines = new List<string>();
            foreach (string line in text.Split('\n')) lines.Add(line.Trim());
            foreach (string wanted in FragmentLines)
                if (!lines.Contains(wanted)) return "RR_Puddle.frag.glsl changed: " + wanted;
            if (!text.Contains("r = standing water, g = wetness, b = open natural water, a = open to the sky"))
                return "RR_Puddle.frag.glsl: the data texture means something else";
            foreach (string line in lines)
            {
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                string rest = line.Replace("u._MapSize.xy", "").Replace("vec4 _MapSize;", "");
                if (line == FragmentLines[1]) rest = rest.Replace("u._MapSize.z", "").Replace("u._MapSize.w", "");
                if (rest.Contains("_MapSize")) return "RR_Puddle.frag.glsl uses _MapSize elsewhere: " + line;
                string data = line.Replace("uniform sampler2D _DataTex;", "");
                if (line == FragmentLines[1] || line == FragmentLines[4]) data = "";
                if (data.Contains("_DataTex")) return "RR_Puddle.frag.glsl reads _DataTex elsewhere: " + line;
            }
            return null;
        }

        internal static string VertexProblem(string text)
        {
            if (string.IsNullOrEmpty(text)) return "RR_World.vert.glsl not found";
            var lines = new List<string>();
            foreach (string line in text.Split('\n')) lines.Add(line.Trim());
            foreach (string wanted in VertexLines)
                if (!lines.Contains(wanted)) return "RR_World.vert.glsl changed: " + wanted;
            return null;
        }

        private static bool Calls(CodeInstruction i, MethodBase m) =>
            (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodBase called && called == m;

        internal static string RegenerateProblem(MethodBase regenerate, MethodBase clipInsideMap, MethodBase materialGetter)
        {
            if (regenerate == null || clipInsideMap == null || materialGetter == null) return "SectionLayer_Puddles members missing";
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(regenerate);
            int clips = 0, materials = 0, subMeshes = 0;
            foreach (CodeInstruction i in code)
            {
                if (Calls(i, clipInsideMap)) clips++;
                if (Calls(i, materialGetter)) materials++;
                if (i.operand is MethodBase m && m.Name == "GetSubMesh") subMeshes++;
            }
            if (clips != 1) return "Regenerate clips " + clips + " times";
            if (materials != 1 || subMeshes != 1) return "Regenerate asks its material " + materials + " times";
            return null;
        }

        private static bool InOrder(List<CodeInstruction> code, params Func<CodeInstruction, bool>[] steps)
        {
            int at = 0;
            foreach (CodeInstruction i in code)
            {
                if (at < steps.Length && steps[at](i)) at++;
            }
            return at == steps.Length;
        }

        private static Func<CodeInstruction, bool> R8(double v) => i => i.opcode == OpCodes.Ldc_R8 && i.operand is double d && d == v;
        private static Func<CodeInstruction, bool> R4(float v) => i => i.opcode == OpCodes.Ldc_R4 && i.operand is float f && f == v;
        private static Func<CodeInstruction, bool> I4(long v) => i => i.LoadsConstant(v);
        private static Func<CodeInstruction, bool> Named(string name) => i => i.operand is MethodBase m && m.Name == name;
        private static Func<CodeInstruction, bool> Made(Type type) => i => i.opcode == OpCodes.Newobj && i.operand is ConstructorInfo c && c.DeclaringType == type;

        internal static string BasinProblem(MethodBase init)
        {
            if (init == null) return "MapComponent_Puddles.Init missing";
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(init);
            bool noise = InOrder(code, R8(BasinFrequency), R8(BasinLacunarity), R8(BasinPersistence), I4(BasinOctaves),
                Named("get_ConstantRandSeed"), I4((int)QualityMode.Medium), Made(typeof(Perlin)));
            if (!noise) return "the basin noise changed";
            bool formula = InOrder(code, R4(BasinBase), R4(BasinAmplitude), Named("GetValue"), R4(BasinLow), R4(BasinHigh), Named("Clamp"),
                i => i.opcode == OpCodes.Stelem_R4);
            return formula ? null : "the basin formula changed";
        }

        internal static string EncodingProblem(MethodBase simulate)
        {
            if (simulate == null) return "MapComponent_Puddles.Simulate missing";
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(simulate);
            int colors = 0;
            foreach (CodeInstruction i in code)
                if (i.operand is ConstructorInfo c && c.DeclaringType == typeof(Color32)) colors++;
            if (colors != 1) return "Simulate makes " + colors + " pixels";
            bool encoded = InOrder(code, R4(255f), R4(0.5f), R4(255f), R4(0.5f), I4(255), I4(255),
                i => i.operand is ConstructorInfo c && c.DeclaringType == typeof(Color32), i => i.opcode == OpCodes.Stelem && Equals(i.operand, typeof(Color32)));
            if (!encoded) return "Simulate encodes its pixels differently";
            bool dirty = code.Exists(i => i.opcode == OpCodes.Stfld && i.operand is FieldInfo f && f.Name == "texDirty");
            return dirty ? null : "Simulate no longer marks its texture";
        }
    }

    internal sealed class RealisticRainTable
    {
        private int kinds;
        private double[] waterSum = new double[0], waterWeight = new double[0], wetSum = new double[0], wetCount = new double[0];
        private float[] water = new float[0], wet = new float[0];
        private bool[] filled = new bool[0];
        private int[] use = new int[0];
        private float[] factors = new float[0];

        private const int Slots = RealisticRainRules.SnowLevels + 1;

        internal int Samples { get; private set; }

        internal void Start(IReadOnlyList<float> kindFactors)
        {
            kinds = kindFactors.Count;
            int slots = kinds * Slots, cells = slots * RealisticRainRules.Buckets;
            if (waterSum.Length < cells)
            {
                waterSum = new double[cells];
                waterWeight = new double[cells];
                water = new float[cells];
            }
            else
            {
                Array.Clear(waterSum, 0, cells);
                Array.Clear(waterWeight, 0, cells);
            }
            if (wetSum.Length < slots)
            {
                wetSum = new double[slots];
                wetCount = new double[slots];
                wet = new float[slots];
                filled = new bool[slots];
                use = new int[kinds * RealisticRainRules.SnowLevels];
            }
            else
            {
                Array.Clear(wetSum, 0, slots);
                Array.Clear(wetCount, 0, slots);
            }
            if (use.Length < kinds * RealisticRainRules.SnowLevels) use = new int[kinds * RealisticRainRules.SnowLevels];
            if (factors.Length < kinds) factors = new float[kinds];
            for (int k = 0; k < kinds; k++) factors[k] = kindFactors[k];
            Samples = 0;
        }

        internal void Add(int kind, int snow, float bucket, float waterNow, float wetNow)
        {
            if ((uint)kind >= (uint)kinds || (uint)snow >= RealisticRainRules.SnowLevels) return;
            bucket = !(bucket >= 0f) ? 0f : Math.Min(bucket, RealisticRainRules.Buckets - 1);
            int k0 = Math.Min((int)bucket, RealisticRainRules.Buckets - 2);
            float t = bucket - k0;
            int b =(kind * Slots + snow) * RealisticRainRules.Buckets + k0;
            waterSum[b] += waterNow * (1f - t);
            waterWeight[b] += 1f - t;
            waterSum[b + 1] += waterNow * t;
            waterWeight[b + 1] += t;
            int s = kind * Slots + snow;
            wetSum[s] += wetNow;
            wetCount[s] += 1f;
            Samples++;
        }

        internal void Finish()
        {
            const int n = RealisticRainRules.Buckets;
            for (int kind = 0; kind < kinds; kind++)
            {
                int all = kind * Slots + RealisticRainRules.SnowLevels;
                for (int snow = 0; snow < RealisticRainRules.SnowLevels; snow++)
                {
                    int from = (kind * Slots + snow) * n, to = all * n;
                    for (int k = 0; k < n; k++)
                    {
                        waterSum[to + k] += waterSum[from + k];
                        waterWeight[to + k] += waterWeight[from + k];
                    }
                    wetSum[all] += wetSum[kind * Slots + snow];
                    wetCount[all] += wetCount[kind * Slots + snow];
                }
                for (int s = 0; s < Slots; s++) Settle(kind * Slots + s);
            }
            for (int kind = 0; kind < kinds; kind++)
            {
                for (int snow = 0; snow < RealisticRainRules.SnowLevels; snow++)
                {
                    int found = Pick(kind, snow);
                    if (found < 0)
                    {
                        int best = -1;
                        float gap = float.MaxValue;
                        for (int other = 0; other < kinds; other++)
                        {
                            if (other == kind || Pick(other, snow) < 0) continue;
                            float d = Math.Abs(factors[other] - factors[kind]);
                            if (d < gap) { gap = d; best = other; }
                        }
                        found = best < 0 ? -1 : Pick(best, snow);
                    }
                    use[kind * RealisticRainRules.SnowLevels + snow] = found;
                }
            }
        }

        private int Pick(int kind, int snow)
        {
            int own = kind * Slots + snow, all = kind * Slots + RealisticRainRules.SnowLevels;
            return filled[own] ? own : filled[all] ? all : -1;
        }

        private void Settle(int slot)
        {
            const int n = RealisticRainRules.Buckets;
            int at = slot * n;
            filled[slot] = wetCount[slot] > 0f;
            wet[slot] = filled[slot] ? (float)(wetSum[slot] / wetCount[slot]) : 0f;
            int last = -1;
            for (int k = 0; k < n; k++)
            {
                if (waterWeight[at + k] <= 1e-4) continue;
                water[at + k] = (float)(waterSum[at + k] / waterWeight[at + k]);
                if (last < 0) for (int j = 0; j < k; j++) water[at + j] = water[at + k];
                else
                    for (int j = last + 1; j < k; j++) water[at + j] = water[at + last] + (water[at + k] - water[at + last]) * (j - last) / (k - last);
                last = k;
            }
            if (last < 0) for (int k = 0; k < n; k++) water[at + k] = 0f;
            else for (int k = last + 1; k < n; k++) water[at + k] = water[at + last];
        }

        internal int SlotFor(int kind, int snow) =>
            (uint)kind < (uint)kinds && (uint)snow < RealisticRainRules.SnowLevels ? use[kind * RealisticRainRules.SnowLevels + snow] : -1;

        internal float Water(int slot, float bucket)
        {
            if (slot < 0) return 0f;
            bucket = !(bucket >= 0f) ? 0f : Math.Min(bucket, RealisticRainRules.Buckets - 1);
            int k0 = Math.Min((int)bucket, RealisticRainRules.Buckets - 2), at = slot * RealisticRainRules.Buckets + k0;
            float t = bucket - k0;
            return water[at] + (water[at + 1] - water[at]) * t;
        }

        internal float Wet(int slot) => slot < 0 ? 0f : wet[slot];
    }
}
