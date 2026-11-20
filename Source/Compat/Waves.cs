using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class Compat_Waves
    {
        private static Type fieldType, layerType, levelsType, materialsType;
        private static MethodInfo fieldFor;
        private static PropertyInfo glowing;
        private static FieldInfo layerGlow;
        private static PropertyInfo version, localVersion;

        internal static OutsideShore Building;

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
            if (!CompatSections.Ready) return "Section layout changed";
            if (layerType.GetConstructor(new[] { typeof(Section) }) == null) return "SectionLayer_Waves(Section) missing";
            fieldFor = AccessTools.Method(fieldType, "For", new[] { typeof(Map) });
            glowing = AccessTools.Property(materialsType, "Glowing");
            layerGlow = AccessTools.Field(layerType, "glow");
            version = AccessTools.Property(fieldType, "Version");
            localVersion = AccessTools.Property(fieldType, "LocalVersion");
            if (fieldFor == null || glowing == null || layerGlow == null || version?.PropertyType != typeof(int)
                || localVersion?.PropertyType != typeof(int)) return "ShoreField/WaveMaterials members missing";

            var two = new[] { typeof(int), typeof(int) };
            var patches = new List<(MethodInfo target, string prefix)>
            {
                (AccessTools.Method(fieldType, "Corner", two), nameof(CornerPrefix)),
                (AccessTools.Method(fieldType, "WaveDistance", two), nameof(WaveDistancePrefix)),
                (AccessTools.Method(fieldType, "Strength", two), nameof(StrengthPrefix)),
                (AccessTools.Method(fieldType, "Reach", two), nameof(ReachPrefix)),
                (AccessTools.Method(fieldType, "AlongFirstDirection", two), nameof(DirectionPrefix)),
                (AccessTools.Method(fieldType, "WaveSlope", two), nameof(DirectionPrefix)),
                (AccessTools.Method(fieldType, "NotFrozen", two), nameof(OnePrefix)),
                (AccessTools.Method(fieldType, "Clearance", two), nameof(ClearancePrefix)),
                (AccessTools.Method(fieldType, "Covered", two), nameof(FalsePrefix)),
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
            foreach ((MethodInfo target, string prefix) in patches)
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(Compat_Waves), prefix));
            }
            return null;
        }

        internal sealed class OutsideShore
        {
            public Map Map;
            public int R, GW, GH, W, H;
            public byte[] Kind;
            public float[] Signed, Calm, Reach, Clearance;
            public bool[] Rock;
            public WaterSeam Seam;

            public bool InMap(int x, int z) => x >= 0 && z >= 0 && x < W && z < H;

            public bool OnMap(int x, int z) => x >= 0 && z >= 0 && x <= W && z <= H;

            private int Idx(int x, int z)
            {
                x = Mathf.Clamp(x, -R, W + R - 1);
                z = Mathf.Clamp(z, -R, H + R - 1);
                return (z + R) * GW + (x + R);
            }

            public float SignedAt(int x, int z) => Signed[Idx(x, z)];

            public byte KindAt(int x, int z) => Kind[Idx(x, z)];

            public bool RockAt(int x, int z) => Rock[Idx(x, z)];

            public float Corner(int x, int z) => (SignedAt(x - 1, z - 1) + SignedAt(x, z - 1) + SignedAt(x - 1, z) + SignedAt(x, z)) * 0.25f;
            public float WaveDistance(int x, int z) => WaterFieldMath.WaveDistance(Corner(x, z), Calm[Idx(x, z)]);
            public float ReachAt(int x, int z) => Reach[Idx(x, z)];
            public float ClearanceAt(int x, int z) => Clearance[Idx(x, z)];
            public float StrengthAt(int x, int z)
            {
                byte kind = 0;
                for (int dz = -2; dz <= 1; dz++)
                    for (int dx = -2; dx <= 1; dx++) kind = Math.Max(kind, KindAt(x + dx, z + dz));
                return kind == 2 ? 1f : kind == 1 ? 0.5f : 0f;
            }
        }

        private static OutsideShore BuildShore(HorizonState s)
        {
            Map map = s.map;
            int w = map.Size.x, h = map.Size.z, r = s.RingWidth + Section.Size + 24;
            var o = new OutsideShore { Map = map, R = r, W = w, H = h, GW = w + 2 * r, GH = h + 2 * r };
            int n = o.GW * o.GH;
            o.Kind = new byte[n];
            o.Rock = new bool[n];
            var inland = new bool[n];
            var covered = new bool[n];
            bool any = false;
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
                    }
                    else
                    {
                        t = s.Field.At(c) ?? s.Field.At(new IntVec3(Mathf.Clamp(x, -s.RingWidth, w + s.RingWidth - 1), 0, Mathf.Clamp(z, -s.RingWidth, h + s.RingWidth - 1)));
                        rock = s.Mountains != null && s.Mountains.IsRock(x, z);
                    }
                    o.Rock[i] = rock;
                    bool sea = !rock && t != null && (t.defName == "WaterOceanDeep" || t.defName == "WaterOceanShallow");
                    o.Kind[i] = sea ? (byte)2 : (byte)0;
                    inland[i] = !rock && !sea && t != null && t.IsWater;
                    if (sea && !o.InMap(x, z)) any = true;
                }
            }
            if (!any) return null;
            BuildReach(o, inland);
            CalmPockets(o);
            float[] toLand = CompatSections.Distance(o.GW, o.GH, i => o.Kind[i] == 0, 64f);
            float[] toSea = CompatSections.Distance(o.GW, o.GH, i => o.Kind[i] != 0, 8f);
            o.Signed = new float[n];
            for (int i = 0; i < n; i++)
            {
                o.Signed[i] = o.Kind[i] != 0 ? toLand[i] - 0.5f : 0.5f - toSea[i];
            }
            WaterFieldMath.Blur(o.Signed, o.GW, o.GH);
            WaterFieldMath.Blur(o.Signed, o.GW, o.GH);
            o.Calm = (float[])o.Signed.Clone();
            WaterFieldMath.WideBlur(o.Calm, o.GW, o.GH);
            WaterFieldMath.WideBlur(o.Calm, o.GW, o.GH);
            o.Clearance = CompatSections.Distance(o.GW, o.GH, i => covered[i], 64f);
            o.Seam = new WaterSeam(w, h);
            o.Seam.Capture(fieldFor.Invoke(null, new object[] { map }), "Corner", "WaveDistance", "Strength", "Reach",
                "NotFrozen", "Clearance", "Blocked", "Covered", "AlongFirstDirection", "WaveSlope");
            return o;
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

        public static bool CornerPrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = o.Seam.Connect("Corner", __0, __1, o.Corner);
            return false;
        }

        public static bool WaveDistancePrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = o.Seam.Connect("WaveDistance", __0, __1, o.WaveDistance);
            return false;
        }

        public static bool StrengthPrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = Mathf.Clamp01(o.Seam.Connect("Strength", __0, __1, o.StrengthAt));
            return false;
        }

        public static bool DirectionPrefix(int __0, int __1, MethodBase __originalMethod, ref float __result)
        {
            OutsideShore o = Building;
            if (o == null || !o.Seam.IsEdge(__0, __1)) return true;
            __result = o.Seam.Value(__originalMethod.Name, __0, __1);
            return false;
        }

        public static bool ReachPrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = Mathf.Clamp01(o.Seam.Connect("Reach", __0, __1, o.ReachAt));
            return false;
        }

        public static bool OnePrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = Mathf.Clamp01(o.Seam.Connect("NotFrozen", __0, __1, (x, z) => 1f));
            return false;
        }

        public static bool ClearancePrefix(int __0, int __1, ref float __result)
        {
            OutsideShore o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = Math.Max(0f, o.Seam.Connect("Clearance", __0, __1, o.ClearanceAt));
            return false;
        }

        public static bool FalsePrefix(int __0, int __1, ref bool __result)
        {
            OutsideShore o = Building;
            if (o == null || o.OnMap(__0, __1)) return true;
            __result = false;
            return false;
        }

        public static bool BlockedPrefix(int __0, int __1, ref bool __result)
        {
            OutsideShore o = Building;
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

        public static bool WaterNearPrefix(ref bool __result)
        {
            if (Building == null) return true;
            __result = true;
            return false;
        }

        public static bool IsStandInPrefix(ref bool __result)
        {
            if (Building == null) return true;
            __result = false;
            return false;
        }

        public static (int, int, int) Revision(Map map)
        {
            object field = fieldFor.Invoke(null, new object[] { map });
            return ((int)version.GetValue(field), (int)localVersion.GetValue(field), 0);
        }

        public static IOutsideCompatLayer Build(HorizonState s)
        {
            OutsideShore o = BuildShore(s);
            if (o == null) return null;
            var layers = new List<(CellRect, SectionLayer)>();
            Building = o;
            try
            {
                foreach (CellRect strip in CompatSections.OutsideStrips(s.map, s.RingWidth))
                {
                    if (!SeaNear(o, strip)) continue;
                    var layer = (SectionLayer)Activator.CreateInstance(layerType, CompatSections.StandIn(s.map, strip));
                    layers.Add((strip, layer));
                    layer.Regenerate();
                }
            }
            catch
            {
                CompatSections.Destroy(layers);
                throw;
            }
            finally
            {
                Building = null;
            }
            return layers.Count == 0 ? null : new Layer(layers);
        }

        private static bool SeaNear(OutsideShore o, CellRect strip)
        {
            foreach (IntVec3 c in strip.ExpandedBy(4))
            {
                if (o.KindAt(c.x, c.z) != 0) return true;
            }
            return false;
        }

        private sealed class Layer : IOutsideCompatLayer
        {
            private readonly List<(CellRect rect, SectionLayer layer)> layers;
            private readonly List<LayerSubMesh> glow = new List<LayerSubMesh>();

            public Layer(List<(CellRect, SectionLayer)> layers)
            {
                this.layers = layers;
                foreach ((CellRect _, SectionLayer layer) in layers)
                {
                    if (layerGlow.GetValue(layer) is List<LayerSubMesh> g) glow.AddRange(g);
                }
            }

            public int Draw(CellRect view)
            {
                bool on = (bool)glowing.GetValue(null);
                for (int i = 0; i < glow.Count; i++) glow[i].disabled = !on;
                return CompatSections.DrawLayers(layers, view);
            }

            public void Dispose() => CompatSections.Destroy(layers);
        }
    }
}
