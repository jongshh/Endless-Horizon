using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EndlessHorizon.Api;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class Compat_Waves
    {
        private static Type fieldType, layerType, levelsType, materialsType;
        private static MethodInfo fieldFor, peek, touchedSince, hasRow;
        private static PropertyInfo glowing;
        private static FieldInfo layerGlow;
        private static PropertyInfo version, localVersion;
        private static Harmony patcher;

        private const string Owner = "waves", Step = Owner + ".field", ShoreScope = Owner + ".shore";
        private static readonly StepResult<OutsideShore> shores = new StepResult<OutsideShore>();

        internal static ShoreApi shoreApi;
        private static bool dropPending;
        private static int epoch;

        public static string Detail { get; private set; } = "";

        internal static OutsideShore Building;
        internal static OutsideShore Inner;
        private static OutsideShore Context => Building ?? Inner;
        private static AccessTools.FieldRef<SectionLayer, Section> layerSection;

        public static string Enable(Harmony harmony)
        {
            fieldType = AccessTools.TypeByName("RimWorldWaves.ShoreField");
            layerType = AccessTools.TypeByName("RimWorldWaves.SectionLayer_Waves");
            levelsType = AccessTools.TypeByName("RimWorldWaves.Levels");
            materialsType = AccessTools.TypeByName("RimWorldWaves.WaveMaterials");
            Type api = AccessTools.TypeByName("RimWorldWaves.WavesApi");
            if (fieldType == null || layerType == null || levelsType == null || materialsType == null || api == null) return "types not found";
            object apiVersion = AccessTools.Property(api, "Version")?.GetValue(null);
            if (!(apiVersion is int v) || v < 1) return "WavesApi.Version missing";
            if (layerType.GetConstructor(new[] { typeof(Section) }) == null) return "SectionLayer_Waves(Section) missing";
            fieldFor = AccessTools.Method(fieldType, "For", new[] { typeof(Map) });
            glowing = AccessTools.Property(materialsType, "Glowing");
            layerGlow = AccessTools.Field(layerType, "glow");
            version = AccessTools.Property(fieldType, "Version");
            localVersion = AccessTools.Property(fieldType, "LocalVersion");
            peek = AccessTools.Method(fieldType, "Peek", new[] { typeof(Map) });
            touchedSince = AccessTools.Method(fieldType, "TouchedSince", new[] { typeof(CellRect), typeof(int) });
            if (fieldFor == null || glowing == null || layerGlow == null || version?.PropertyType != typeof(int)
                || localVersion?.PropertyType != typeof(int)) return "ShoreField/WaveMaterials members missing";

            var two = new[] { typeof(int), typeof(int) };
            MethodInfo regenerate = AccessTools.DeclaredMethod(layerType, "Regenerate", Type.EmptyTypes);
            if (regenerate == null) return "SectionLayer_Waves.Regenerate missing";
            layerSection = AccessTools.FieldRefAccess<SectionLayer, Section>("section");
            var blended = new List<(MethodInfo target, string postfix, string prefix)>
            {
                (AccessTools.Method(fieldType, "Corner", two), nameof(CornerPostfix), nameof(CornerPrefix)),
                (AccessTools.Method(fieldType, "WaveDistance", two), nameof(WaveDistancePostfix), nameof(WaveDistancePrefix)),
                (AccessTools.Method(fieldType, "Strength", two), nameof(StrengthPostfix), nameof(StrengthPrefix)),
                (AccessTools.Method(fieldType, "Reach", two), nameof(ReachPostfix), nameof(ReachPrefix)),
                (AccessTools.Method(fieldType, "Clearance", two), nameof(ClearancePostfix), nameof(ClearancePrefix)),
            };
            foreach ((MethodInfo target, string _, string _) in blended)
            {
                if (target == null || target.ReturnType != typeof(float)) return "a ShoreField method is missing or changed";
            }
            hasRow = AccessTools.Method(fieldType, "HasRow", new[] { typeof(int) });
            var patches = new List<(MethodInfo target, string prefix)>
            {
                (AccessTools.Method(fieldType, "NotFrozen", two), nameof(OnePrefix)),
                (AccessTools.Method(fieldType, "Covered", two), nameof(CoveredPrefix)),
                (AccessTools.Method(fieldType, "Blocked", two), nameof(BlockedPrefix)),
                (hasRow, nameof(HasRowPrefix)),
                (AccessTools.Method(layerType, "WaterNear", new[] { typeof(CellRect) }), nameof(WaterNearPrefix)),
                (AccessTools.Method(levelsType, "IsStandIn", new[] { typeof(Section) }), nameof(IsStandInPrefix)),
            };
            foreach ((MethodInfo target, string _) in patches)
            {
                if (target == null) return "a ShoreField/SectionLayer_Waves method is missing";
            }
            if (AccessTools.Method(fieldType, "Corner", two).ReturnType != typeof(float)
                || AccessTools.Method(fieldType, "Covered", two).ReturnType != typeof(bool)) return "ShoreField signatures changed";
            ShoreApi bound = ShoreApi.Bind(api, out string legacyWhy);
            patcher = harmony;
            foreach ((MethodInfo target, string postfix, string prefix) in blended)
                harmony.Patch(target, prefix: bound != null ? new HarmonyMethod(typeof(Compat_Waves), prefix) : null,
                    postfix: new HarmonyMethod(typeof(Compat_Waves), postfix));
            harmony.Patch(regenerate, prefix: new HarmonyMethod(typeof(Compat_Waves), nameof(RegeneratePrefix)),
                finalizer: new HarmonyMethod(typeof(Compat_Waves), nameof(RegenerateFinalizer)));
            foreach ((MethodInfo target, string prefix) in patches)
            {
                if (target == hasRow)
                {
                    if (bound != null) continue;
                    rowsPatched = true;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(Compat_Waves), prefix));
            }
            string failed = Register(harmony);
            if (failed != null) return failed;
            if (bound != null)
            {
                try
                {
                    bound.Add();
                    shoreApi = bound;
                }
                catch (Exception ex)
                {
                    Log.Error(IntegrationLog.Prefix + "Registering with Waves' own API 4 failed; Waves is followed through patches as before. " + ex);
                    legacyWhy = "adding the provider failed";
                    PatchRows();
                }
            }
            SetMode(shoreApi != null
                ? "provider mode (WavesApi " + shoreApi.Version + "): Waves asks EH about the sea past the map edge"
                : "legacy mode (" + legacyWhy + ")");
            return null;
        }

        private static void SetMode(string detail)
        {
            Detail = detail;
            HorizonInfo.Report(Owner, "mode", detail);
#if EH_DEV
            Log.Message(IntegrationLog.Prefix + "Waves: " + detail);
#endif
        }

        private static bool rowsPatched;

        private static void PatchRows()
        {
            if (rowsPatched || patcher == null || hasRow == null) return;
            rowsPatched = true;
            patcher.Patch(hasRow, prefix: new HarmonyMethod(typeof(Compat_Waves), nameof(HasRowPrefix)));
        }

        private static string Register(Harmony harmony)
        {
            if (!HorizonExtensions.RegisterBuildStep(Owner, Step, BuildShoreSteps, new[] { "surface.frozen" })
                || !HorizonExtensions.RegisterScope(Owner, ShoreScope, EnterShore)
                || !HorizonExtensions.RegisterLayer(Owner, layerType, new[] { ShoreScope }, Wanted, joinMapEdge: true, callDrawLayer: false)
                || !HorizonExtensions.RegisterDraw(Owner, "before", BeforeDraw)) return "Section layout changed";
            RegisterValue("kind", (o, i) => o.Kind[i]);
            RegisterValue("shoreDistance", (o, i) => o.Signed[i]);
            RegisterValue("calm", (o, i) => o.Calm[i]);
            RegisterValue("reach", (o, i) => o.Reach[i]);
            RegisterValue("clearance", (o, i) => o.Clearance[i]);
            HorizonInfo.Observe(Owner, Changed);
            OptionRedraws.Follow(harmony, Owner);
            return null;
        }

        private static void RegisterValue(string name, Func<OutsideShore, int, float> value) =>
            HorizonExtensions.RegisterChannel(Owner, Owner + "." + name, (map, c) =>
            {
                OutsideShore o = shores.Building(map);
                return o != null && o.InGrid(c.x, c.z) ? value(o, o.Idx(c.x, c.z)) : float.NaN;
            });

        private sealed class ShoreEntry : IDisposable
        {
            internal OutsideShore Before;

            public void Dispose() => Building = Before;
        }

        private static IDisposable EnterShore(Map map)
        {
            OutsideShore o = shores.Building(map);
            if (o == null) return null;
            var entry = new ShoreEntry { Before = Building };
            Building = o;
            return entry;
        }

        internal sealed class OutsideShore
        {
            public Map Map;
            public int R, GW, GH, W, H;
            internal FieldShape Shape;
            public byte[] Kind;
            public float[] Signed, Calm, Reach, Clearance;
            public bool[] Rock;
            public bool[] Frozen;
            public bool[] Covered;
            public int Version;

            public const int InnerBand = 24;

            public bool IsEdge(int x, int z) => Shape != null ? Shape.IsEdge(x, z) : OnMap(x, z) && (x == 0 || z == 0 || x == W || z == H);

            internal bool InGrid(int x, int z) => x >= -R && z >= -R && x < W + R && z < H + R;

            public float Share(int x, int z)
            {
                if (!OnMap(x, z)) return 1f;
                int inside = Shape?.Inward(x, z) ?? Math.Min(Math.Min(x, W - x), Math.Min(z, H - z));
                if (inside >= InnerBand) return 0f;
                float t = inside / (float)InnerBand;
                return 1f - t * t * (3f - 2f * t);
            }

            internal OutsideShore TrimToMap()
            {
                const int margin = 8;
                var o = new OutsideShore { Map = Map, Shape = Shape, R = margin, W = W, H = H, GW = W + 2 * margin, GH = H + 2 * margin, Version = Version };
                int shift = R - margin;
                T[] Cut<T>(T[] from)
                {
                    var to = new T[o.GW * o.GH];
                    for (int z = 0; z < o.GH; z++) Array.Copy(from, (z + shift) * GW + shift, to, z * o.GW, o.GW);
                    return to;
                }
                o.Kind = Cut(Kind);
                o.Rock = Cut(Rock);
                if (Covered != null) o.Covered = Cut(Covered);
                o.Frozen = Cut(Frozen);
                o.Signed = Cut(Signed);
                o.Calm = Cut(Calm);
                o.Reach = Cut(Reach);
                o.Clearance = Cut(Clearance);
                return o;
            }

            public bool InMap(int x, int z) => Shape != null ? Shape.InMap(x, z) : x >= 0 && z >= 0 && x < W && z < H;

            public bool OnMap(int x, int z) => Shape != null ? Shape.OnMap(x, z) : x >= 0 && z >= 0 && x <= W && z <= H;

            internal int Idx(int x, int z)
            {
                x = Mathf.Clamp(x, -R, W + R - 1);
                z = Mathf.Clamp(z, -R, H + R - 1);
                return (z + R) * GW + (x + R);
            }

            public float SignedAt(int x, int z) => Signed[Idx(x, z)];

            public byte KindAt(int x, int z) => Kind[Idx(x, z)];

            public bool RockAt(int x, int z) => Rock[Idx(x, z)];
            public bool CoveredAt(int x, int z) => Covered != null && Covered[Idx(x, z)];

            public float Corner(int x, int z) => (SignedAt(x - 1, z - 1) + SignedAt(x, z - 1) + SignedAt(x - 1, z) + SignedAt(x, z)) * 0.25f;
            public float WaveDistance(int x, int z) => WaterFieldMath.WaveDistance(Corner(x, z), Calm[Idx(x, z)]);
            public float ReachAt(int x, int z) => Reach[Idx(x, z)];
            public float ClearanceAt(int x, int z) => Clearance[Idx(x, z)];
            public float NotFrozenAt(int x, int z)
            {
                for (int dz = -1; dz <= 0; dz++)
                    for (int dx = -1; dx <= 0; dx++) if (Frozen[Idx(x + dx, z + dz)]) return 0f;
                return 1f;
            }
            public float StrengthAt(int x, int z)
            {
                byte kind = 0;
                for (int dz = -2; dz <= 1; dz++)
                    for (int dx = -2; dx <= 1; dx++) kind = Math.Max(kind, KindAt(x + dx, z + dz));
                return kind == 2 ? 1f : kind == 1 ? 0.5f : 0f;
            }

            internal bool PastRect(int x, int z) => x < 0 || z < 0 || x > W || z > H;

            internal bool WindowPastRect(int x, int z) => x < -1 || z < -1 || x > W + 1 || z > H + 1;

            internal const int AskedBand = 4;

            private bool bandKnown;
            private ulong band;

            internal ulong Band
            {
                get
                {
                    if (bandKnown) return band;
                    ulong h = 0x45485741u;
                    void Add(uint v)
                    {
                        h = (h ^ v) * 0x9E3779B97F4A7C15UL;
                        h ^= h >> 29;
                    }
                    Add((uint)W);
                    Add((uint)H);
                    for (int z = -AskedBand; z < H + AskedBand; z++)
                        for (int x = -AskedBand; x < W + AskedBand; x++)
                        {
                            if (x == 0 && z >= 0 && z < H) x = W;
                            int i = Idx(x, z);
                            Add((uint)BitConverter.SingleToInt32Bits(Signed[i]));
                            Add((uint)BitConverter.SingleToInt32Bits(Calm[i]));
                            Add((uint)BitConverter.SingleToInt32Bits(Reach[i]));
                            Add((uint)BitConverter.SingleToInt32Bits(Clearance[i]));
                            Add(Kind[i] | (Rock[i] ? 0x100u : 0u) | (Frozen[i] ? 0x200u : 0u) | (CoveredAt(x, z) ? 0x400u : 0u));
                        }
                    band = h;
                    bandKnown = true;
                    return h;
                }
            }
        }

        private static IEnumerable<bool> BuildShoreSteps(Map map)
        {
            FieldShape shape = FieldShape.Of(map);
            foreach (bool step in shape.ReadSteps(map)) yield return step;
            int w = shape.W, h = shape.H, r = shape.Ring + Section.Size + 24;
            var o = new OutsideShore { Map = map, Shape = shape.Rectangle ? null : shape, R = r, W = w, H = h, GW = w + 2 * r, GH = h + 2 * r };
            int n = o.GW * o.GH;
            o.Kind = new byte[n];
            o.Rock = new bool[n];
            o.Frozen = new bool[n];
            var inland = new bool[n];
            var covered = new bool[n];
            o.Covered = covered;
            bool any = false;
            bool floods = ModsConfig.OdysseyActive;
            bool[] flooded = null;
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    int i = (z + r) * o.GW + (x + r);
                    var c = new IntVec3(x, 0, z);
                    TerrainDef t;
                    bool rock;
                    if (o.InMap(x, z))
                    {
                        t = map.terrainGrid.BaseTerrainAt(c);
                        Building b = c.GetEdifice(map);
                        rock = b != null && b.def.mineable;
                        covered[i] = map.terrainGrid.TerrainAt(c) != t && t != null && t.IsWater;
                        TerrainDef temp = map.terrainGrid.TempTerrainAt(c);
                        if (temp != null && !temp.IsWater && t != null && t.IsWater) t = null;
                        if (floods && !rock && t != null && !t.IsWater && temp != null && temp.IsFlood) (flooded ??= new bool[n])[i] = true;
                    }
                    else
                    {
                        IntVec3 read = shape.ClampToBounds(x, z);
                        t = HorizonWorld.BaseTerrainAt(map, read);
                        rock = HorizonWorld.RockAt(map, read) != null;
                        covered[i] = t?.IsWater == true && HorizonWorld.Value(map, "water.covered", c) == 1f;
                        if (floods && !rock && t != null && !t.IsWater && HorizonWorld.TerrainAt(map, read)?.IsFlood == true) (flooded ??= new bool[n])[i] = true;
                    }
                    o.Rock[i] = rock;
                    o.Frozen[i] = o.InMap(x, z) ? RimWorld.TerrainDefOf.ThinIce != null && map.terrainGrid.TempTerrainAt(c) == RimWorld.TerrainDefOf.ThinIce : t?.canFreeze == true && HorizonWorld.Value(map, "surface.frozen", c) == 1f;
                    bool sea = !rock && t != null && (t.defName == "WaterOceanDeep" || t.defName == "WaterOceanShallow");
                    o.Kind[i] = sea ? (byte)2 : (byte)0;
                    inland[i] = !rock && !sea && t != null && t.IsWater;
                    if (sea && !o.InMap(x, z)) any = true;
                }
                if (((z + r) & 3) == 3) yield return false;
            }
            if (!any)
            {
                shores.Made(map, null);
                yield break;
            }
            BuildReach(o, inland);
            yield return false;
            CalmPockets(o);
            yield return false;
            float[] toLand = IntegrationWater.Distance(o.GW, o.GH, i => o.Kind[i] == 0, 64f);
            float[] toSea = IntegrationWater.Distance(o.GW, o.GH, i => o.Kind[i] != 0, 8f);
            o.Signed = new float[n];
            for (int i = 0; i < n; i++)
            {
                o.Signed[i] = o.Kind[i] != 0 ? toLand[i] - 0.5f : 0.5f - toSea[i];
            }
            yield return false;
            WaterFieldMath.Blur(o.Signed, o.GW, o.GH);
            WaterFieldMath.Blur(o.Signed, o.GW, o.GH);
            if (flooded != null) CoverFlood(covered, flooded, o.Signed);
            yield return false;
            o.Calm = (float[])o.Signed.Clone();
            WaterFieldMath.WideBlur(o.Calm, o.GW, o.GH);
            yield return false;
            WaterFieldMath.WideBlur(o.Calm, o.GW, o.GH);
            yield return false;
            o.Clearance = IntegrationWater.Distance(o.GW, o.GH, i => covered[i], 64f);
            yield return false;
            o.Version = ReadShore(map, true, out int now, out _, out _) ? now : -1;
            shores.Made(map, o);
        }

        internal static void CoverFlood(bool[] covered, bool[] flooded, float[] signed)
        {
            for (int i = 0; i < covered.Length; i++)
                if (flooded[i] && signed[i] > -7f) covered[i] = true;
        }

        private static void BuildReach(OutsideShore o, bool[] inland)
        {
            int n = o.Kind.Length, w = o.GW;
            o.Reach = new float[n];
            var distance = new int[n];
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                o.Reach[i] = inland[i] ? 0f : 1f;
                distance[i] = o.Kind[i] == 2 ? 0 : -1;
                if (distance[i] == 0) queue.Enqueue(i);
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                foreach (int j in Neighbors(i, w, n))
                    if (distance[j] < 0 && inland[j] && distance[i] < 16)
                    {
                        distance[j] = distance[i] + 1;
                        o.Reach[j] = 1f - distance[j] / 16f;
                        o.Kind[j] = 2;
                        queue.Enqueue(j);
                    }
            }
            for (int pass = 0; pass < 3; pass++)
            {
                var prev = (float[])o.Reach.Clone();
                for (int i = 0; i < n; i++)
                    if (o.Kind[i] == 0 && !inland[i])
                        foreach (int j in Neighbors(i, w, n)) o.Reach[i] = Math.Min(o.Reach[i], prev[j]);
            }
            WaterFieldMath.Blur(o.Reach, w, o.GH);
            WaterFieldMath.Blur(o.Reach, w, o.GH);
        }

        private static IEnumerable<int> Neighbors(int i, int w, int n)
        {
            if (i % w > 0) yield return i - 1;
            if (i % w < w - 1) yield return i + 1;
            if (i >= w) yield return i - w;
            if (i < n - w) yield return i + w;
        }

        private static void CalmPockets(OutsideShore o)
        {
            var seen = new bool[o.Kind.Length];
            var cells = new List<int>();
            var stack = new Stack<int>();
            for (int i = 0; i < seen.Length; i++)
            {
                if (seen[i] || o.Kind[i] != 2) continue;
                cells.Clear();
                seen[i] = true;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int j = stack.Pop();
                    cells.Add(j);
                    foreach (int k in Neighbors(j, o.GW, seen.Length))
                        if (!seen[k] && o.Kind[k] == 2) { seen[k] = true; stack.Push(k); }
                }
                if (cells.Count < 900) foreach (int j in cells) o.Kind[j] = 1;
            }
        }

        private static float Mix(float own, float composite, float share) => share >= 1f ? composite : own + (composite - own) * share;

        public static bool CornerPrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || !Asked(o) || !o.PastRect(__0, __1)) return true;
            __result = o.Corner(__0, __1);
            return false;
        }

        public static bool WaveDistancePrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || !Asked(o) || !o.PastRect(__0, __1)) return true;
            __result = o.WaveDistance(__0, __1);
            return false;
        }

        public static bool StrengthPrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || !Asked(o) || !o.WindowPastRect(__0, __1)) return true;
            __result = o.StrengthAt(__0, __1);
            return false;
        }

        public static bool ReachPrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || !Asked(o) || !o.PastRect(__0, __1)) return true;
            __result = o.ReachAt(__0, __1);
            return false;
        }

        public static bool ClearancePrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || !Asked(o) || !o.PastRect(__0, __1)) return true;
            __result = o.ClearanceAt(__0, __1);
            return false;
        }

        public static void CornerPostfix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || Asked(o) && o.PastRect(__0, __1)) return;
            float share = o.Share(__0, __1);
            if (share > 0f) __result = Mix(__result, o.Corner(__0, __1), share);
        }

        public static void WaveDistancePostfix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || Asked(o) && o.PastRect(__0, __1)) return;
            float share = o.Share(__0, __1);
            if (share > 0f) __result = Mix(__result, o.WaveDistance(__0, __1), share);
        }

        public static void StrengthPostfix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || Asked(o) && o.WindowPastRect(__0, __1)) return;
            float share = o.Share(__0, __1);
            if (share > 0f) __result = Mathf.Clamp01(Mix(__result, o.StrengthAt(__0, __1), share));
        }

        public static void ReachPostfix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || Asked(o) && o.PastRect(__0, __1)) return;
            float share = o.Share(__0, __1);
            if (share > 0f) __result = Mathf.Clamp01(Mix(__result, o.ReachAt(__0, __1), share));
        }

        public static void ClearancePostfix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || Asked(o) && o.PastRect(__0, __1)) return;
            float share = o.Share(__0, __1);
            if (share > 0f) __result = Math.Max(0f, Mix(__result, o.ClearanceAt(__0, __1), share));
        }

        public static bool OnePrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null || (o.OnMap(__0, __1) && !o.IsEdge(__0, __1))) return true;
            __result = o.NotFrozenAt(__0, __1);
            return false;
        }

        public static bool CoveredPrefix(int __0, int __1, ref bool __result)
        {
            OutsideShore o = Context;
            if (o == null || o.OnMap(__0, __1)) return true;
            __result = o.CoveredAt(__0, __1);
            return false;
        }

        public static bool BlockedPrefix(int __0, int __1, ref bool __result)
        {
            OutsideShore o = Context;
            if (o == null || o.OnMap(__0, __1)) return true;
            __result = o.RockAt(__0, __1) || o.RockAt(__0 - 1, __1) || o.RockAt(__0, __1 - 1) || o.RockAt(__0 - 1, __1 - 1);
            return false;
        }

        public static bool HasRowPrefix(int __0, ref bool __result)
        {
            if (Building == null) return true;
            __result = true;
            return false;
        }

        public static bool WaterNearPrefix(CellRect __0, ref bool __result)
        {
            if (Building != null)
            {
                __result = true;
                return false;
            }
            if (Inner == null || !SeaNear(Inner, __0)) return true;
            __result = true;
            return false;
        }

        public static bool IsStandInPrefix(ref bool __result)
        {
            if (Building == null) return true;
            __result = false;
            return false;
        }

        internal sealed class ShoreApi
        {
            internal const int Needed = 4;

            private static readonly Type[] CellArguments =
                { typeof(float), typeof(float), typeof(float), typeof(float), typeof(bool), typeof(bool), typeof(bool), typeof(float) };

            internal static readonly string[] CellNames = { "distance", "strength", "reach", "calmDistance", "covered", "frozen", "blocked", "clearance" };

            internal int Version;
            internal Type DelegateType, CellType, LayoutType;
            internal Delegate Provider;
            private MethodInfo add, remove, shore, touchedSince;
            private Action<Map> changed;
            private PropertyInfo isValid, layoutVersion, layoutLocal;

            internal static ShoreApi Bind(Type api, out string why)
            {
                try { return Look(api, out why); }
                catch (Exception ex)
                {
                    why = "WavesApi 4 could not be bound: " + ex.GetType().Name;
                    Log.Error(IntegrationLog.Prefix + "Waves' own API 4 could not be bound; Waves is followed through patches as before. " + ex);
                    return null;
                }
            }

            private static ShoreApi Look(Type api, out string why)
            {
                const BindingFlags Static = BindingFlags.Public | BindingFlags.Static, Instance = BindingFlags.Public | BindingFlags.Instance;
                object number = api?.GetProperty("Version", Static)?.GetValue(null);
                if (!(number is int v) || v < Needed)
                {
                    why = "WavesApi.Version " + (number ?? "missing") + ", the provider needs " + Needed;
                    return null;
                }
                MethodInfo add = api.GetMethod("AddShoreBeyondMap", Static);
                ParameterInfo[] added = add?.GetParameters();
                Type provider = added != null && added.Length == 1 ? added[0].ParameterType : null;
                if (provider == null || add.ReturnType != typeof(void) || provider.FullName != "RimWorldWaves.ShoreBeyondMap"
                    || !typeof(MulticastDelegate).IsAssignableFrom(provider))
                {
                    why = "WavesApi.AddShoreBeyondMap(ShoreBeyondMap) missing";
                    return null;
                }
                MethodInfo invoke = provider.GetMethod("Invoke");
                ParameterInfo[] parameters = invoke?.GetParameters();
                if (parameters == null || invoke.ReturnType != typeof(bool) || parameters.Length != 4 || parameters[0].ParameterType != typeof(Map)
                    || parameters[1].ParameterType != typeof(int) || parameters[2].ParameterType != typeof(int) || !parameters[3].ParameterType.IsByRef || !parameters[3].IsOut)
                {
                    why = "ShoreBeyondMap is not bool (Map, int, int, out ShoreCell)";
                    return null;
                }
                Type cell = parameters[3].ParameterType.GetElementType();
                ConstructorInfo make = cell?.GetConstructor(CellArguments);
                if (make == null || !cell.IsValueType || cell.FullName != "RimWorldWaves.ShoreCell")
                {
                    why = "ShoreCell(float, float, float, float, bool, bool, bool, float) missing";
                    return null;
                }
                ParameterInfo[] names = make.GetParameters();
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i].Name == CellNames[i]) continue;
                    why = "ShoreCell's constructor changed (" + names[i].Name + " where " + CellNames[i] + " was)";
                    return null;
                }
                MethodInfo remove = api.GetMethod("RemoveShoreBeyondMap", Static, null, new[] { provider }, null);
                MethodInfo changedAt = api.GetMethod("ShoreBeyondMapChanged", Static, null, new[] { typeof(Map) }, null);
                MethodInfo shore = api.GetMethod("Shore", Static, null, new[] { typeof(Map) }, null);
                if (remove == null || changedAt == null || shore == null || changedAt.ReturnType != typeof(void))
                {
                    why = "WavesApi.RemoveShoreBeyondMap, ShoreBeyondMapChanged or Shore missing";
                    return null;
                }
                Type layout = shore.ReturnType;
                PropertyInfo isValid = layout.GetProperty("IsValid", Instance), layoutVersion = layout.GetProperty("Version", Instance),
                    layoutLocal = layout.GetProperty("LocalVersion", Instance);
                MethodInfo touchedSince = layout.GetMethod("TouchedSince", Instance, null, new[] { typeof(CellRect), typeof(int) }, null);
                if (!layout.IsValueType || layout.FullName != "RimWorldWaves.ShoreLayout" || isValid?.PropertyType != typeof(bool)
                    || layoutVersion?.PropertyType != typeof(int) || layoutLocal?.PropertyType != typeof(int) || touchedSince?.ReturnType != typeof(bool))
                {
                    why = "ShoreLayout.IsValid, Version, LocalVersion or TouchedSince missing";
                    return null;
                }

                var method = new DynamicMethod("EndlessHorizon_NewShoreCell", cell, CellArguments, typeof(Compat_Waves).Module, true);
                ILGenerator il = method.GetILGenerator();
                for (int i = 0; i < CellArguments.Length; i++) il.Emit(OpCodes.Ldarg_S, (byte)i);
                il.Emit(OpCodes.Newobj, make);
                il.Emit(OpCodes.Ret);
                var makerTypes = new Type[CellArguments.Length + 1];
                Array.Copy(CellArguments, makerTypes, CellArguments.Length);
                makerTypes[CellArguments.Length] = cell;
                Delegate maker = method.CreateDelegate(typeof(Func<,,,,,,,,>).MakeGenericType(makerTypes));
                typeof(ShoreCells<>).MakeGenericType(cell).GetMethod(nameof(ShoreCells<int>.Use), BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { maker });
                MethodInfo provide = typeof(Compat_Waves).GetMethod(nameof(Provide), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(cell);

                why = null;
                return new ShoreApi
                {
                    Version = v, DelegateType = provider, CellType = cell, LayoutType = layout,
                    Provider = Delegate.CreateDelegate(provider, provide),
                    add = add, remove = remove, shore = shore, touchedSince = touchedSince,
                    changed = (Action<Map>)Delegate.CreateDelegate(typeof(Action<Map>), changedAt),
                    isValid = isValid, layoutVersion = layoutVersion, layoutLocal = layoutLocal,
                };
            }

            internal void Add() => add.Invoke(null, new object[] { Provider });

            internal void Remove() => remove.Invoke(null, new object[] { Provider });

            internal void Changed(Map map) => changed(map);

            internal bool Read(Map map, out int v, out int local, out object layout)
            {
                layout = shore.Invoke(null, new object[] { map });
                v = local = 0;
                if (layout == null || !(bool)isValid.GetValue(layout)) return false;
                v = (int)layoutVersion.GetValue(layout);
                local = (int)layoutLocal.GetValue(layout);
                return true;
            }

            internal bool TouchedSince(object layout, CellRect section, int since) => (bool)touchedSince.Invoke(layout, new object[] { section, since });
        }

        private static class ShoreCells<T> where T : struct
        {
            internal static Func<float, float, float, float, bool, bool, bool, float, T> Make;

            internal static void Use(Delegate maker) => Make = (Func<float, float, float, float, bool, bool, bool, float, T>)maker;
        }

        internal static bool Provide<T>(Map map, int x, int z, out T cell) where T : struct
        {
            cell = default;
            if (map == null) return false;
            try
            {
                OutsideShore o = Answering(map);
                if (o == null) return false;
                int i = o.Idx(x, z);
                byte kind = o.Kind[i];
                cell = ShoreCells<T>.Make(o.Signed[i], kind == 2 ? 1f : kind == 1 ? 0.5f : 0f, o.Reach[i], o.Calm[i],
                    o.Covered != null && o.Covered[i], o.Frozen[i], o.Rock[i], o.Clearance[i]);
                return true;
            }
            catch (Exception ex)
            {
                cell = default;
                Log.ErrorOnce(IntegrationLog.Prefix + "Answering Waves about the sea past the map edge failed; Waves takes that cell for dry land. " + ex, 0x45485750);
                return false;
            }
        }

        private static OutsideShore Answering(Map map)
        {
            OutsideShore o = Building ?? Inner;
            if (o != null && o.Map == map) return o;
            WavesWatch watch = watches.GetOrCreateValue(map);
            int frame = Time.frameCount;
            if (!watch.Asked || watch.AskedFrame != frame || watch.AskedEpoch != epoch)
            {
                watch.Asked = true;
                watch.AskedFrame = frame;
                watch.AskedEpoch = epoch;
                watch.Answer = Serves(map) ? shores.Current(map) : null;
            }
            return watch.Answer;
        }

        private static bool Serves(Map map)
        {
            if (shoreApi == null) return false;
            string state = HorizonExtensions.StateOf(Owner);
            if (state == "failed") dropPending = true;
            return (state == "active" || state == "restored") && Drawn(map);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Drawn(Map map) => HorizonInfo.IsActive(map);

        private static bool Asked(OutsideShore o) => shoreApi != null && o.Map != null;

        private static void Follow(Map map, WavesWatch watch)
        {
            if (shoreApi == null) return;
            try
            {
                OutsideShore now = Serves(map) ? shores.Current(map) : null;
                bool serving = now != null;
                ulong band = serving ? now.Band : 0;
                if (serving == watch.Serving && band == watch.Band) return;
                watch.Serving = serving;
                watch.Band = band;
                Announce(map, watch);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(IntegrationLog.Prefix + "Telling Waves that the sea past the map edge changed failed; its sections at the edge keep what they had. " + ex, 0x45485743);
            }
        }

        private static void Announce(Map map, WavesWatch watch)
        {
            bool had = ReadShore(map, false, out int before, out _, out _);
            shoreApi.Changed(map);
            if (!had || !ReadShore(map, false, out int after, out _, out _) || after == before) return;
            if (watch.OwnTop != before) watch.OwnBase = before;
            watch.OwnTop = after;
        }

        private static bool Ours(WavesWatch watch, int from, int to) =>
            from == to || to == watch.OwnTop && from >= watch.OwnBase && from < watch.OwnTop;

        private static void DropProvider()
        {
            dropPending = false;
            ShoreApi api = shoreApi;
            if (api == null) return;
            shoreApi = null;
            epoch++;
            try
            {
                api.Remove();
                PatchRows();
            }
            catch (Exception ex)
            {
                Log.Error(IntegrationLog.Prefix + "Taking EH's provider out of Waves failed; it answers nothing from now on. " + ex);
            }
            SetMode("legacy mode (provider removed: the integration failed)");
        }

        private static bool ReadShore(Map map, bool create, out int v, out int local, out object shore)
        {
            ShoreApi api = shoreApi;
            if (api != null)
            {
                if (create || peek == null || peek.Invoke(null, new object[] { map }) != null) return api.Read(map, out v, out local, out shore);
                v = local = 0;
                shore = null;
                return false;
            }
            shore = (create ? fieldFor : peek ?? fieldFor).Invoke(null, new object[] { map });
            v = local = 0;
            if (shore == null) return false;
            v = (int)version.GetValue(shore);
            local = (int)localVersion.GetValue(shore);
            return true;
        }

        private static bool CanTellSections => shoreApi != null || touchedSince != null;

        private static bool TouchedSince(object shore, CellRect section, int since) => shoreApi != null
            ? shoreApi.TouchedSince(shore, section, since)
            : (bool)touchedSince.Invoke(shore, new object[] { section, since });

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AskRefresh(Map map, CellRect area) => HorizonInfo.RequestRefresh(map, Owner, area);

        internal readonly struct Entered
        {
            internal readonly OutsideShore Outer, Inner;
            internal readonly bool Set;

            internal Entered(OutsideShore outer, OutsideShore inner)
            {
                Outer = outer;
                Inner = inner;
                Set = true;
            }
        }

        [HarmonyPriority(Priority.Last)]
        internal static void RegeneratePrefix(SectionLayer __instance, out Entered __state)
        {
            __state = default;
            if (Building == null && Inner == null) return;
            try
            {
                Section section = layerSection(__instance);
                Map map = section?.map;
                if (map == null || !IntegrationWater.AnyPlayable(map, section.CellRect)) return;
                __state = new Entered(Building, Inner);
                bool joined = Building != null;
                Building = null;
                Inner = null;
                if (!joined) return;
                OutsideShore o = shores.Current(map);
                if (o != null && ReadShore(map, true, out int now, out _, out _) && Ours(watches.GetOrCreateValue(map), o.Version, now)) Inner = o;
            }
            catch (Exception ex)
            {
                Inner = null;
                Log.ErrorOnce(IntegrationLog.Prefix + "Joining Waves' edge section to the outside failed; it is drawn as Waves does alone. " + ex, 0x45485757);
            }
        }

        internal static Exception RegenerateFinalizer(SectionLayer __instance, Exception __exception, Entered __state)
        {
            if (__state.Set)
            {
                Building = __state.Outer;
                Inner = __state.Inner;
            }
            else if (Building != null && __exception == null) RememberGlow(__instance);
            return __exception;
        }

        private sealed class GlowOf
        {
            internal List<LayerSubMesh> Meshes;
        }

        private static readonly ConditionalWeakTable<Map, ConditionalWeakTable<SectionLayer, GlowOf>> glowLayers =
            new ConditionalWeakTable<Map, ConditionalWeakTable<SectionLayer, GlowOf>>();

        private static void RememberGlow(SectionLayer layer)
        {
            if (!(layerGlow.GetValue(layer) is List<LayerSubMesh> meshes)) return;
            Map map = layerSection(layer)?.map;
            if (map != null) glowLayers.GetOrCreateValue(map).AddOrUpdate(layer, new GlowOf { Meshes = meshes });
        }

        private const int PollFrames = 15;

        private sealed class WavesWatch
        {
            internal bool Started;
            internal int LastVersion, LastLocal, NextPoll;
            internal readonly List<CellRect> SeaSections = new List<CellRect>();

            internal bool Asked;
            internal int AskedFrame, AskedEpoch;
            internal OutsideShore Answer;
            internal bool Serving;
            internal ulong Band;
            internal int OwnBase = int.MinValue, OwnTop = int.MinValue;
        }

        private static readonly ConditionalWeakTable<Map, WavesWatch> watches = new ConditionalWeakTable<Map, WavesWatch>();

        private static void Changed(Map map, string id, CellRect area)
        {
            epoch++;
            if (dropPending) DropProvider();
            if (id == "disposed")
            {
                shores.Disposed(map);
                if (watches.TryGetValue(map, out WavesWatch gone) && gone.Serving)
                {
                    try { shoreApi?.Changed(map); }
                    catch (Exception ex) { Log.ErrorOnce(IntegrationLog.Prefix + "Telling Waves that the sea past the map edge is gone failed. " + ex, 0x45485744); }
                }
                watches.Remove(map);
                glowLayers.Remove(map);
            }
            else if (id == Step)
            {
                shores.Shown(map);
                OutsideShore o = shores.Current(map);
                WavesWatch watch = watches.GetOrCreateValue(map);
                Learn(map, watch, o);
                if (o != null && ReadShore(map, true, out int now, out _, out _) && !Ours(watch, o.Version, now))
                    AskRefresh(map, HorizonSpace.Bounds(map));
                Follow(map, watch);
            }
            else if (id == "settings")
            {
                Follow(map, watches.GetOrCreateValue(map));
            }
            else if (id == "terrain.base" && IntegrationWater.Within(map, area, 8, IsSea))
            {
                AskRefresh(map, area);
            }
            else if (id == "terrain.top" && ModsConfig.OdysseyActive && IntegrationWater.Within(map, area, 8, IsSea))
            {
                AskRefresh(map, area);
            }
        }

        private static bool IsSea(TerrainDef t) => t.defName == "WaterOceanDeep" || t.defName == "WaterOceanShallow";

        private static void Learn(Map map, WavesWatch watch, OutsideShore o)
        {
            watch.SeaSections.Clear();
            if (o == null) return;
            int size = Section.Size;
            for (int x = 0; x < o.W; x += size)
            {
                for (int z = 0; z < o.H; z += size)
                {
                    if (o.Shape == null && x > 0 && z > 0 && x + size < o.W && z + size < o.H) continue;
                    var section = new CellRect(x, z, size, size);
                    foreach (IntVec3 c in section.ExpandedBy(8))
                    {
                        if (o.KindAt(c.x, c.z) != 0)
                        {
                            watch.SeaSections.Add(section);
                            break;
                        }
                    }
                }
            }
        }

        private static void BeforeDraw(Map map, CellRect view)
        {
            if (glowLayers.TryGetValue(map, out ConditionalWeakTable<SectionLayer, GlowOf> glows))
            {
                bool on = (bool)glowing.GetValue(null);
                foreach (KeyValuePair<SectionLayer, GlowOf> layer in glows)
                    foreach (LayerSubMesh sm in layer.Value.Meshes) sm.disabled = !on;
            }
            WavesWatch watch = watches.GetOrCreateValue(map);
            if (Time.frameCount < watch.NextPoll) return;
            watch.NextPoll = Time.frameCount + PollFrames;
            if (dropPending) DropProvider();
            Poll(map, watch);
            Follow(map, watch);
        }

        private static void Poll(Map map, WavesWatch watch)
        {
            if (!ReadShore(map, false, out int v, out int lv, out object shore)) return;
            if (watch.Started)
            {
                if (!Ours(watch, watch.LastVersion, v)) AskRefresh(map, HorizonSpace.Bounds(map));
                else if (lv != watch.LastLocal)
                {
                    if (!CanTellSections) AskRefresh(map, HorizonSpace.Bounds(map));
                    else
                    {
                        foreach (CellRect section in watch.SeaSections)
                        {
                            if (TouchedSince(shore, section, watch.LastLocal)) AskRefresh(map, section);
                        }
                    }
                }
            }
            watch.Started = true;
            watch.LastVersion = v;
            watch.LastLocal = lv;
        }

        private static bool Wanted(Map map, CellRect rect)
        {
            OutsideShore o = IntegrationWater.AnyPlayable(map, rect) ? shores.Current(map) : shores.Building(map);
            return o != null && SeaNear(o, rect);
        }

        internal static OutsideShore ShownShore(Map map) => shores.Current(map);

        private static bool SeaNear(OutsideShore o, CellRect strip)
        {
            foreach (IntVec3 c in strip.ExpandedBy(4))
            {
                if (o.KindAt(c.x, c.z) != 0) return true;
            }
            return false;
        }
    }
}
