using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class Compat_Waves
    {
        private static Type fieldType, layerType, levelsType, materialsType;
        private static MethodInfo fieldFor, peek, touchedSince;
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
            peek = AccessTools.Method(fieldType, "Peek", new[] { typeof(Map) });
            touchedSince = AccessTools.Method(fieldType, "TouchedSince", new[] { typeof(CellRect), typeof(int) });
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
            public bool[] Frozen;
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

        private static IEnumerable<bool> BuildShoreSteps(HorizonState s, StripBuild<OutsideShore>.Holder result)
        {
            Map map = s.map;
            int w = map.Size.x, h = map.Size.z, r = s.RingWidth + Section.Size + 24;
            var o = new OutsideShore { Map = map, R = r, W = w, H = h, GW = w + 2 * r, GH = h + 2 * r };
            int n = o.GW * o.GH;
            o.Kind = new byte[n];
            o.Rock = new bool[n];
            o.Frozen = new bool[n];
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
                    o.Frozen[i] = o.InMap(x, z) ? RimWorld.TerrainDefOf.ThinIce != null && map.terrainGrid.TempTerrainAt(c) == RimWorld.TerrainDefOf.ThinIce : s.Field.FrozenAt(c);
                    bool sea = !rock && t != null && (t.defName == "WaterOceanDeep" || t.defName == "WaterOceanShallow");
                    o.Kind[i] = sea ? (byte)2 : (byte)0;
                    inland[i] = !rock && !sea && t != null && t.IsWater;
                    if (sea && !o.InMap(x, z)) any = true;
                }
                if (((z + r) & 31) == 31) yield return false;
            }
            if (!any) yield break;
            BuildReach(o, inland);
            yield return false;
            CalmPockets(o);
            yield return false;
            float[] toLand = CompatSections.Distance(o.GW, o.GH, i => o.Kind[i] == 0, 64f);
            float[] toSea = CompatSections.Distance(o.GW, o.GH, i => o.Kind[i] != 0, 8f);
            o.Signed = new float[n];
            for (int i = 0; i < n; i++)
            {
                o.Signed[i] = o.Kind[i] != 0 ? toLand[i] - 0.5f : 0.5f - toSea[i];
            }
            yield return false;
            WaterFieldMath.Blur(o.Signed, o.GW, o.GH);
            WaterFieldMath.Blur(o.Signed, o.GW, o.GH);
            yield return false;
            o.Calm = (float[])o.Signed.Clone();
            WaterFieldMath.WideBlur(o.Calm, o.GW, o.GH);
            yield return false;
            WaterFieldMath.WideBlur(o.Calm, o.GW, o.GH);
            yield return false;
            o.Clearance = CompatSections.Distance(o.GW, o.GH, i => covered[i], 64f);
            yield return false;
            o.Seam = new WaterSeam(w, h);
            o.Seam.Capture(fieldFor.Invoke(null, new object[] { map }), "Corner", "WaveDistance", "Strength", "Reach",
                "NotFrozen", "Clearance", "Blocked", "Covered", "AlongFirstDirection", "WaveSlope");
            result.Value = o;
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
            __result = o.Seam.IsEdge(__0, __1) ? o.Seam.Value("NotFrozen", __0, __1) : o.NotFrozenAt(__0, __1);
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

        public static ICompatBuild Begin(HorizonState s, IOutsideCompatLayer previous, List<CellRect> areas) =>
            new StripBuild<OutsideShore>(s, h => BuildShoreSteps(s, h), SeaNear, o => Building = o, () => Building = null,
                layerType, l => new Layer(l), o => WatchOf(s)?.Learn(s, o), previous, areas);

        private static readonly ConditionalWeakTable<HorizonState, WavesWatch> watches = new ConditionalWeakTable<HorizonState, WavesWatch>();

        private static WavesWatch WatchOf(HorizonState s) => watches.TryGetValue(s, out WavesWatch w) ? w : null;

        public static ICompatWatch Watch(HorizonState s) => watches.GetValue(s, _ => new WavesWatch());

        private sealed class WavesWatch : ICompatWatch
        {
            private bool started;
            private int lastVersion, lastLocal;
            private readonly List<CellRect> seaSections = new List<CellRect>();

            public void Learn(HorizonState s, OutsideShore o)
            {
                seaSections.Clear();
                if (o == null) return;
                int w = s.map.Size.x, h = s.map.Size.z, size = Section.Size;
                for (int x = 0; x < w; x += size)
                {
                    for (int z = 0; z < h; z += size)
                    {
                        if (x > 0 && z > 0 && x + size < w && z + size < h) continue;
                        var section = new CellRect(x, z, size, size);
                        foreach (IntVec3 c in section.ExpandedBy(8))
                        {
                            if (o.KindAt(c.x, c.z) != 0)
                            {
                                seaSections.Add(section);
                                break;
                            }
                        }
                    }
                }
            }

            public void Poll(HorizonState s, List<CellRect> areas, ref bool global)
            {
                object field = (peek ?? fieldFor).Invoke(null, new object[] { s.map });
                if (field == null) return;
                int v = (int)version.GetValue(field), lv = (int)localVersion.GetValue(field);
                if (started)
                {
                    if (v != lastVersion) global = true;
                    else if (lv != lastLocal)
                    {
                        if (touchedSince == null) global = true;
                        else
                        {
                            foreach (CellRect section in seaSections)
                            {
                                if ((bool)touchedSince.Invoke(field, new object[] { section, lastLocal })) areas.Add(section);
                            }
                        }
                    }
                }
                started = true;
                lastVersion = v;
                lastLocal = lv;
            }

            public bool Relevant(HorizonState s, IntVec3 loc) => CompatSections.WaterWithin(s, loc, 8, t => t.defName == "WaterOceanDeep" || t.defName == "WaterOceanShallow");
        }

        private static bool SeaNear(OutsideShore o, CellRect strip)
        {
            foreach (IntVec3 c in strip.ExpandedBy(4))
            {
                if (o.KindAt(c.x, c.z) != 0) return true;
            }
            return false;
        }

        private sealed class Layer : StripLayer
        {
            private readonly List<LayerSubMesh> glow = new List<LayerSubMesh>();

            public Layer(List<(CellRect, SectionLayer)> layers) : base(layers)
            {
                foreach ((CellRect _, SectionLayer layer) in layers)
                {
                    if (layerGlow.GetValue(layer) is List<LayerSubMesh> g) glow.AddRange(g);
                }
            }

            public override int Draw(CellRect view)
            {
                bool on = (bool)glowing.GetValue(null);
                for (int i = 0; i < glow.Count; i++) glow[i].disabled = !on;
                return CompatSections.DrawLayers(Layers, view);
            }
        }
    }
}
