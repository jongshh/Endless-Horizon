using System;
using System.Collections.Generic;
using System.Reflection;
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
        private static MethodInfo fieldFor, peek, touchedSince;
        private static PropertyInfo glowing;
        private static FieldInfo layerGlow;
        private static PropertyInfo version, localVersion;

        private const string Owner = "waves", Step = Owner + ".field", ShoreScope = Owner + ".shore";
        private static readonly StepResult<OutsideShore> shores = new StepResult<OutsideShore>();

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
            var blended = new List<(MethodInfo target, string postfix)>
            {
                (AccessTools.Method(fieldType, "Corner", two), nameof(CornerPostfix)),
                (AccessTools.Method(fieldType, "WaveDistance", two), nameof(WaveDistancePostfix)),
                (AccessTools.Method(fieldType, "Strength", two), nameof(StrengthPostfix)),
                (AccessTools.Method(fieldType, "Reach", two), nameof(ReachPostfix)),
                (AccessTools.Method(fieldType, "Clearance", two), nameof(ClearancePostfix)),
            };
            foreach ((MethodInfo target, string _) in blended)
            {
                if (target == null || target.ReturnType != typeof(float)) return "a ShoreField method is missing or changed";
            }
            var patches = new List<(MethodInfo target, string prefix)>
            {
                (AccessTools.Method(fieldType, "NotFrozen", two), nameof(OnePrefix)),
                (AccessTools.Method(fieldType, "Covered", two), nameof(CoveredPrefix)),
                (AccessTools.Method(fieldType, "Blocked", two), nameof(BlockedPrefix)),
                (AccessTools.Method(fieldType, "HasRow", new[] { typeof(int) }), nameof(HasRowPrefix)),
                (AccessTools.Method(layerType, "WaterNear", new[] { typeof(CellRect) }), nameof(WaterNearPrefix)),
                (AccessTools.Method(levelsType, "IsStandIn", new[] { typeof(Section) }), nameof(IsStandInPrefix)),
            };
            foreach ((MethodInfo target, string _) in patches)
            {
                if (target == null) return "a ShoreField/SectionLayer_Waves method is missing";
            }
            if (AccessTools.Method(fieldType, "Corner", two).ReturnType != typeof(float)
                || AccessTools.Method(fieldType, "Covered", two).ReturnType != typeof(bool)) return "ShoreField signatures changed";
            foreach ((MethodInfo target, string postfix) in blended)
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(Compat_Waves), postfix));
            harmony.Patch(regenerate, prefix: new HarmonyMethod(typeof(Compat_Waves), nameof(RegeneratePrefix)),
                finalizer: new HarmonyMethod(typeof(Compat_Waves), nameof(RegenerateFinalizer)));
            foreach ((MethodInfo target, string prefix) in patches)
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(Compat_Waves), prefix));
            }
            return Register(harmony);
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
            o.Version = (int)version.GetValue(fieldFor.Invoke(null, new object[] { map }));
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

        public static void CornerPostfix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null) return;
            float share = o.Share(__0, __1);
            if (share > 0f) __result = Mix(__result, o.Corner(__0, __1), share);
        }

        public static void WaveDistancePostfix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null) return;
            float share = o.Share(__0, __1);
            if (share > 0f) __result = Mix(__result, o.WaveDistance(__0, __1), share);
        }

        public static void StrengthPostfix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null) return;
            float share = o.Share(__0, __1);
            if (share > 0f) __result = Mathf.Clamp01(Mix(__result, o.StrengthAt(__0, __1), share));
        }

        public static void ReachPostfix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null) return;
            float share = o.Share(__0, __1);
            if (share > 0f) __result = Mathf.Clamp01(Mix(__result, o.ReachAt(__0, __1), share));
        }

        public static void ClearancePostfix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Context;
            if (o == null) return;
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
                if (o != null && (int)version.GetValue(fieldFor.Invoke(null, new object[] { map })) == o.Version) Inner = o;
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
        }

        private static readonly ConditionalWeakTable<Map, WavesWatch> watches = new ConditionalWeakTable<Map, WavesWatch>();

        private static void Changed(Map map, string id, CellRect area)
        {
            if (id == "disposed")
            {
                shores.Disposed(map);
                watches.Remove(map);
                glowLayers.Remove(map);
            }
            else if (id == Step)
            {
                shores.Shown(map);
                OutsideShore o = shores.Current(map);
                WavesWatch watch = watches.GetOrCreateValue(map);
                Learn(map, watch, o);
                if (o != null && (int)version.GetValue(fieldFor.Invoke(null, new object[] { map })) != o.Version)
                    HorizonInfo.RequestRefresh(map, Owner, HorizonSpace.Bounds(map));
            }
            else if (id == "terrain.base" && IntegrationWater.Within(map, area, 8, IsSea))
            {
                HorizonInfo.RequestRefresh(map, Owner, area);
            }
            else if (id == "terrain.top" && ModsConfig.OdysseyActive && IntegrationWater.Within(map, area, 8, IsSea))
            {
                HorizonInfo.RequestRefresh(map, Owner, area);
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
            object field = (peek ?? fieldFor).Invoke(null, new object[] { map });
            if (field == null) return;
            int v = (int)version.GetValue(field), lv = (int)localVersion.GetValue(field);
            if (watch.Started)
            {
                if (v != watch.LastVersion) HorizonInfo.RequestRefresh(map, Owner, HorizonSpace.Bounds(map));
                else if (lv != watch.LastLocal)
                {
                    if (touchedSince == null) HorizonInfo.RequestRefresh(map, Owner, HorizonSpace.Bounds(map));
                    else
                    {
                        foreach (CellRect section in watch.SeaSections)
                        {
                            if ((bool)touchedSince.Invoke(field, new object[] { section, watch.LastLocal })) HorizonInfo.RequestRefresh(map, Owner, section);
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
