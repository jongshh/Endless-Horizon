using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class Compat_LivingWaters
    {
        private static Type fieldType, layerType, levelsType;
        private static MethodInfo fieldFor, classify, alongCorner, flavorOf, lakeAt, caveNear, swampNear;
        private static FieldInfo alongCells;
        private static PropertyInfo version, iceVersion, floodVersion;

        internal static OutsideWater Building;

        public static string Enable(Harmony harmony)
        {
            fieldType = AccessTools.TypeByName("RimWorldRivers.WaterField");
            layerType = AccessTools.TypeByName("RimWorldRivers.SectionLayer_Rivers");
            levelsType = AccessTools.TypeByName("RimWorldRivers.Levels");
            Type api = AccessTools.TypeByName("RimWorldRivers.LivingWatersApi");
            if (fieldType == null || layerType == null || levelsType == null || api == null) return "types not found";
            object apiVersion = AccessTools.Property(api, "Version")?.GetValue(null);
            if (!(apiVersion is int v) || v < 1) return "LivingWatersApi.Version missing";
            if (!CompatSections.Ready) return "Section layout changed";
            if (layerType.GetConstructor(new[] { typeof(Section) }) == null) return "SectionLayer_Rivers(Section) missing";
            fieldFor = AccessTools.Method(fieldType, "For", new[] { typeof(Map) });
            classify = AccessTools.Method(fieldType, "Classify", new[] { typeof(TerrainDef) });
            alongCorner = AccessTools.Method(fieldType, "AlongCorner", new[] { typeof(int), typeof(int) });
            flavorOf = AccessTools.Method(fieldType, "FlavorOf", new[] { typeof(TerrainDef) });
            lakeAt = AccessTools.Method(fieldType, "LakeAt", new[] { typeof(int), typeof(int), typeof(Vector2).MakeByRefType(), typeof(float).MakeByRefType() });
            caveNear = AccessTools.Method(fieldType, "CaveNear", new[] { typeof(int), typeof(int) });
            swampNear = AccessTools.Method(fieldType, "SwampNear", new[] { typeof(int), typeof(int) });
            alongCells = AccessTools.Field(fieldType, "along");
            version = AccessTools.Property(fieldType, "Version");
            iceVersion = AccessTools.Property(fieldType, "IceVersion");
            floodVersion = AccessTools.Property(fieldType, "FloodVersion");
            if (fieldFor == null || classify == null || classify.ReturnType != typeof(byte) || alongCorner == null
                || flavorOf == null || flavorOf.ReturnType != typeof(byte) || lakeAt == null || caveNear == null || swampNear == null
                || alongCells?.FieldType != typeof(float[]) || version?.PropertyType != typeof(int)
                || iceVersion?.PropertyType != typeof(int) || floodVersion?.PropertyType != typeof(int)) return "WaterField members missing";
            MethodInfo anyMarsh = AccessTools.PropertyGetter(fieldType, "AnyMarsh");
            MethodInfo glowHash = AccessTools.Method(layerType, "GlowHash", new[] { fieldType });
            if (anyMarsh == null || glowHash == null || glowHash.ReturnType != typeof(int)) return "AnyMarsh/GlowHash missing";

            var two = new[] { typeof(int), typeof(int) };
            var patches = new List<(MethodInfo target, string prefix)>
            {
                (AccessTools.Method(fieldType, "KindAt", two), nameof(KindPrefix)),
                (AccessTools.Method(fieldType, "Corner", two), nameof(CornerPrefix)),
                (AccessTools.Method(fieldType, "DeepCorner", two), nameof(DeepCornerPrefix)),
                (AccessTools.Method(fieldType, "IceCorner", two), nameof(IceCornerPrefix)),
                (alongCorner, nameof(AlongCornerPrefix)),
                (AccessTools.Method(fieldType, "AwayFromSea", two), nameof(AwayFromSeaPrefix)),
                (AccessTools.Method(fieldType, "NotFrozen", two), nameof(NotFrozenPrefix)),
                (AccessTools.Method(fieldType, "MovingNear", two), nameof(NearPrefix)),
                (AccessTools.Method(fieldType, "StillNear", two), nameof(NearPrefix)),
                (AccessTools.Method(fieldType, "OursNear", two), nameof(NearPrefix)),
                (AccessTools.Method(fieldType, "MarshNear", two), nameof(NearPrefix)),
                (AccessTools.Method(fieldType, "Distance", new[] { typeof(IntVec3) }), nameof(DistancePrefix)),
                (AccessTools.Method(fieldType, "Covered", two), nameof(CellFalsePrefix)),
                (AccessTools.Method(fieldType, "Frozen", two), nameof(FrozenPrefix)),
                (AccessTools.Method(fieldType, "Roofed", two), nameof(RoofedPrefix)),
                (AccessTools.Method(fieldType, "FlavorAt", two), nameof(FlavorAtPrefix)),
                (AccessTools.Method(fieldType, "FlavorAround", two), nameof(FlavorAroundPrefix)),
                (AccessTools.Method(fieldType, "FlavorNear", two), nameof(FlavorNearPrefix)),
                (AccessTools.Method(fieldType, "MarshCorner", two), nameof(MarshCornerPrefix)),
                (AccessTools.Method(fieldType, "MarshReach", two), nameof(MarshReachPrefix)),
                (AccessTools.Method(fieldType, "HoleNear", two), nameof(HoleNearPrefix)),
                (lakeAt, nameof(LakeAtPrefix)),
                (AccessTools.Method(fieldType, "LakeExtentNear", two), nameof(LakeExtentPrefix)),
                (caveNear, nameof(CaveNearPrefix)),
                (swampNear, nameof(SwampNearPrefix)),
                (glowHash, nameof(GlowHashPrefix)),
                (AccessTools.Method(fieldType, "CoveredNear", new[] { typeof(Vector2), typeof(float) }), nameof(CoveredNearPrefix)),
                (AccessTools.Method(layerType, "WaterNear", new[] { typeof(CellRect) }), nameof(WaterNearPrefix)),
                (AccessTools.Method(levelsType, "IsStandIn", new[] { typeof(Section) }), nameof(IsStandInPrefix)),
            };
            foreach ((MethodInfo target, string _) in patches)
            {
                if (target == null) return "a WaterField/SectionLayer_Rivers method is missing";
            }
            if (AccessTools.Method(fieldType, "KindAt", two).ReturnType != typeof(byte)
                || AccessTools.Method(fieldType, "Corner", two).ReturnType != typeof(float)) return "WaterField signatures changed";
            foreach ((MethodInfo target, string prefix) in patches)
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(Compat_LivingWaters), prefix));
            }
            harmony.Patch(anyMarsh, postfix: new HarmonyMethod(typeof(Compat_LivingWaters), nameof(AnyMarshPostfix)));
            return null;
        }

        internal sealed class OutsideWater
        {
            public int R, GW, GH, W, H;
            public byte[] Kind;
            public float[] Signed, Deep, Sea, Along, MarshDist, MarshReach, Ice;
            public byte[] Flavor, NearFlavor;
            public bool[] Roofed;
            public bool[] Frozen;
            public bool AnyMarsh;
            public WaterSeam Seam;
            public int[] LakeId, NearLake;
            public readonly List<LakeInfo> Lakes = new List<LakeInfo>();

            public bool InGrid(int x, int z) => x >= -R && z >= -R && x < W + R && z < H + R;

            public bool Nearest(int x, int z, out LakeInfo lake)
            {
                int i0 = Idx(x, z);
                int found = NearLake[i0];
                if (found == -2)
                {
                    found = -1;
                    for (int ring = 0; ring <= 4 && found < 0; ring++)
                    {
                        for (int dz = -1 - ring; dz <= ring && found < 0; dz++)
                        {
                            for (int dx = -1 - ring; dx <= ring; dx++)
                            {
                                if (ring > 0 && dz > -1 - ring && dz < ring && dx > -1 - ring && dx < ring) continue;
                                if (!InGrid(x + dx, z + dz)) continue;
                                int id = LakeId[Idx(x + dx, z + dz)];
                                if (id >= 0)
                                {
                                    found = id;
                                    break;
                                }
                            }
                        }
                    }
                    NearLake[i0] = found;
                }
                lake = found >= 0 ? Lakes[found] : default;
                return found >= 0;
            }

            public bool InMap(int x, int z) => x >= 0 && z >= 0 && x < W && z < H;

            public bool OnMap(int x, int z) => x >= 0 && z >= 0 && x <= W && z <= H;

            public int Idx(int x, int z)
            {
                x = Mathf.Clamp(x, -R, W + R - 1);
                z = Mathf.Clamp(z, -R, H + R - 1);
                return (z + R) * GW + (x + R);
            }

            public float CornerOf(float[] a, int x, int z) =>
                (a[Idx(x - 1, z - 1)] + a[Idx(x, z - 1)] + a[Idx(x - 1, z)] + a[Idx(x, z)]) * 0.25f;

            public float ShoreAt(int x, int z) => CornerOf(Signed, x, z);
            public float DeepAt(int x, int z) => CornerOf(Deep, x, z);
            public float IceAt(int x, int z) => CornerOf(Ice, x, z);
            public float NotFrozenAt(int x, int z)
            {
                for (int dz = -1; dz <= 0; dz++)
                    for (int dx = -1; dx <= 0; dx++) if (Frozen[Idx(x + dx, z + dz)]) return 0f;
                return 1f;
            }
            public float MarshAt(int x, int z) => CornerOf(MarshDist, x, z);
            public float SeaAt(int x, int z)
            {
                float t = Mathf.Clamp01((Sea[Idx(x, z)] - 4f) / 12f);
                return t * t * (3f - 2f * t);
            }
            public float AlongAt(int x, int z)
            {
                float sum = 0f;
                int count = 0;
                for (int dz = -1; dz <= 0; dz++)
                    for (int dx = -1; dx <= 0; dx++)
                    {
                        int i = Idx(x + dx, z + dz);
                        if (Kind[i] == 2) { sum += Along[i]; count++; }
                    }
                return count > 0 ? sum / count : 0f;
            }
            public float MarshReachAt(int x, int z)
            {
                float result = 6f;
                for (int dz = -1; dz <= 0; dz++)
                    for (int dx = -1; dx <= 0; dx++) result = Math.Min(result, MarshReach[Idx(x + dx, z + dz)]);
                return result;
            }
        }

        internal struct LakeInfo
        {
            public Vector2 Middle;
            public float Extent, Roofed, Marshy;
        }

        private static OutsideWater BuildWater(HorizonState s)
        {
            Map map = s.map;
            RiverExtension river = s.Field.River;
            int w = map.Size.x, h = map.Size.z, r = s.RingWidth + Section.Size + 4;
            var o = new OutsideWater { R = r, W = w, H = h, GW = w + 2 * r, GH = h + 2 * r };
            int n = o.GW * o.GH;
            o.Kind = new byte[n];
            var deep = new bool[n];
            var soggy = new bool[n];
            o.Flavor = new byte[n];
            o.Roofed = new bool[n];
            o.Frozen = new bool[n];
            bool any = false;
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    int i = (z + r) * o.GW + (x + r);
                    var c = new IntVec3(x, 0, z);
                    TerrainDef t;
                    if (o.InMap(x, z))
                    {
                        t = map.terrainGrid.BaseTerrainAt(c);
                    }
                    else
                    {
                        t = s.Field.At(c) ?? s.Field.At(new IntVec3(Mathf.Clamp(x, -s.RingWidth, w + s.RingWidth - 1), 0, Mathf.Clamp(z, -s.RingWidth, h + s.RingWidth - 1)));
                        if (s.Mountains != null && s.Mountains.IsRock(x, z)) t = null;
                    }
                    byte k = t == null ? (byte)0 : (byte)classify.Invoke(null, new object[] { t });
                    o.Kind[i] = k;
                    deep[i] = k != 0 && k != 3 && t.passability == Traversability.Impassable;
                    soggy[i] = k == 0 && t != null && (t.defName.Contains("Marsh") || t.defName.Contains("Mud"));
                    o.Flavor[i] = k != 0 && k != 4 ? (byte)flavorOf.Invoke(null, new object[] { t }) : (byte)0;
                    o.Roofed[i] = o.InMap(x, z) ? map.roofGrid.RoofAt(c)?.isNatural ?? false : s.Field.OutsideThickRoof(c);
                    o.Frozen[i] = o.InMap(x, z) ? RimWorld.TerrainDefOf.ThinIce != null && map.terrainGrid.TempTerrainAt(c) == RimWorld.TerrainDefOf.ThinIce : s.Field.FrozenAt(c);
                    if (!o.InMap(x, z) && (k == 1 || k == 2 || k == 3)) any = true;
                }
            }
            if (!any) return null;
            bool Water(int i) => o.Kind[i] == 1 || o.Kind[i] == 2 || o.Kind[i] == 4;
            o.Signed = Signed(o, i => Water(i));
            o.Deep = Signed(o, i => Water(i) && deep[i]);
            if (Array.IndexOf(o.Frozen, true) >= 0) o.Ice = Signed(o, i => o.Frozen[i]);
            else { o.Ice = new float[n]; Array.Fill(o.Ice, -5.5f); }
            o.Sea = CompatSections.Distance(o.GW, o.GH, i => o.Kind[i] == 4, 18f);
            object field = fieldFor.Invoke(null, new object[] { map });
            o.Seam = new WaterSeam(w, h);
            o.Seam.Capture(field, "Corner", "DeepCorner", "IceCorner", "AlongCorner", "AwayFromSea", "NotFrozen", "MarshCorner", "MarshReach",
                "MovingNear", "StillNear", "OursNear", "MarshNear", "FlavorNear");
            SpreadFlavor(o);
            BuildMarsh(o);
            FindLakes(o, soggy, field);

            o.Along = new float[n];
            var realAlong = (float[])alongCells.GetValue(field);
            if (realAlong.Length != w * h) throw new InvalidOperationException("Living Waters along grid dimensions changed.");
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++) o.Along[o.Idx(x, z)] = realAlong[z * w + x];
            var start = new List<(float value, float sign)>();
            if (river != null)
            {
                foreach (RiverCrossing c in river.Crossings)
                {
                    Vector2 inward = -c.outDir.normalized;
                    int ex = Mathf.Clamp(Mathf.FloorToInt(c.start.x + inward.x * 0.6f), 0, w - 1);
                    int ez = Mathf.Clamp(Mathf.FloorToInt(c.start.y + inward.y * 0.6f), 0, h - 1);
                    int ix = Mathf.Clamp(Mathf.FloorToInt(c.start.x + inward.x * 5f), 0, w - 1);
                    int iz = Mathf.Clamp(Mathf.FloorToInt(c.start.y + inward.y * 5f), 0, h - 1);
                    float a0 = (float)alongCorner.Invoke(field, new object[] { ex + 1, ez + 1 });
                    float a1 = (float)alongCorner.Invoke(field, new object[] { ix + 1, iz + 1 });
                    start.Add((a0, a0 >= a1 ? 1f : -1f));
                }
            }
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    if (o.InMap(x, z)) continue;
                    int i = (z + r) * o.GW + (x + r);
                    if (o.Kind[i] == 2 && river != null && river.TryPathAt(x, z, out int ci, out float sAlong) && ci >= 0 && ci < start.Count)
                    {
                        o.Along[i] = start[ci].value + start[ci].sign * sAlong;
                    }
                }
            }
            return o;
        }

        private static void SpreadFlavor(OutsideWater o)
        {
            o.NearFlavor = (byte[])o.Flavor.Clone();
            if (Array.TrueForAll(o.Flavor, f => f == 0)) return;
            int w = o.GW;
            var prev = new byte[o.Flavor.Length];
            for (int step = 0; step < 7; step++)
            {
                Array.Copy(o.NearFlavor, prev, prev.Length);
                for (int k = 0; k < prev.Length; k++)
                {
                    if (o.Kind[k] != 0 && o.Kind[k] != 3) continue;
                    int x = k % w;
                    byte b = prev[k];
                    if (x > 0 && prev[k - 1] > b) b = prev[k - 1];
                    if (x < w - 1 && prev[k + 1] > b) b = prev[k + 1];
                    if (k >= w && prev[k - w] > b) b = prev[k - w];
                    if (k < prev.Length - w && prev[k + w] > b) b = prev[k + w];
                    o.NearFlavor[k] = b;
                }
            }
        }

        private static void BuildMarsh(OutsideWater o)
        {
            o.AnyMarsh = false;
            for (int i = 0; i < o.Kind.Length && !o.AnyMarsh; i++)
            {
                o.AnyMarsh = o.Kind[i] == 3;
            }
            if (!o.AnyMarsh)
            {
                o.MarshDist = new float[o.Kind.Length];
                o.MarshReach = new float[o.Kind.Length];
                for (int i = 0; i < o.Kind.Length; i++)
                {
                    o.MarshDist[i] = -6f;
                    o.MarshReach[i] = 6f;
                }
                return;
            }
            o.MarshDist = Signed(o, i => o.Kind[i] != 0);
            o.MarshReach = CompatSections.Distance(o.GW, o.GH, i => o.Kind[i] == 3, 6f);
        }

        private static void FindLakes(OutsideWater o, bool[] soggy, object field)
        {
            int n = o.Kind.Length, w = o.GW;
            o.LakeId = new int[n];
            o.NearLake = new int[n];
            for (int i = 0; i < n; i++)
            {
                o.LakeId[i] = -1;
                o.NearLake[i] = -2;
            }
            var cells = new List<int>();
            var stack = new Stack<int>();
            var band = new Dictionary<int, int>();
            var queue = new Queue<int>();
            for (int i0 = 0; i0 < n; i0++)
            {
                if (o.Kind[i0] != 1 || o.LakeId[i0] >= 0) continue;
                int id = o.Lakes.Count;
                cells.Clear();
                o.LakeId[i0] = id;
                stack.Push(i0);
                int inMap = -1;
                float sx = 0f, sz = 0f;
                int roofed = 0;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    cells.Add(i);
                    int x = i % w, z = i / w;
                    sx += x - o.R;
                    sz += z - o.R;
                    if (o.Roofed[i]) roofed++;
                    if (inMap < 0 && o.InMap(x - o.R, z - o.R)) inMap = i;
                    if (x > 0) Visit(i - 1);
                    if (x < w - 1) Visit(i + 1);
                    if (z > 0) Visit(i - w);
                    if (z < o.GH - 1) Visit(i + w);
                }
                var lake = new LakeInfo();
                if (inMap >= 0)
                {
                    int mx = inMap % w - o.R, mz = inMap / w - o.R;
                    var args = new object[] { mx, mz, Vector2.zero, 0f };
                    lakeAt.Invoke(field, args);
                    lake.Middle = (Vector2)args[2];
                    lake.Extent = (float)args[3];
                    lake.Roofed = (float)caveNear.Invoke(field, new object[] { mx, mz });
                    lake.Marshy = (float)swampNear.Invoke(field, new object[] { mx, mz });
                }
                else
                {
                    lake.Middle = new Vector2(sx / cells.Count + 0.5f, sz / cells.Count + 0.5f);
                    lake.Extent = Mathf.Sqrt(cells.Count);
                    lake.Roofed = (float)roofed / cells.Count;
                    int land = 0, marsh = 0;
                    band.Clear();
                    queue.Clear();
                    foreach (int c in cells)
                    {
                        band[c] = 0;
                        queue.Enqueue(c);
                    }
                    while (queue.Count > 0)
                    {
                        int c = queue.Dequeue();
                        int d = band[c];
                        if (d >= 5) continue;
                        int cx = c % w;
                        for (int dir = 0; dir < 4; dir++)
                        {
                            if ((dir == 0 && cx == 0) || (dir == 1 && cx == w - 1) || (dir == 2 && c < w) || (dir == 3 && c >= n - w)) continue;
                            int nb = dir == 0 ? c - 1 : dir == 1 ? c + 1 : dir == 2 ? c - w : c + w;
                            if (band.ContainsKey(nb) || (o.Kind[nb] != 0 && o.Kind[nb] != 3)) continue;
                            band[nb] = d + 1;
                            queue.Enqueue(nb);
                            if (o.Kind[nb] == 3 || soggy[nb]) marsh++;
                            else land++;
                        }
                    }
                    lake.Marshy = land + marsh > 0 ? (float)marsh / (land + marsh) : 0f;
                }
                o.Lakes.Add(lake);

                void Visit(int j)
                {
                    if (o.Kind[j] == 1 && o.LakeId[j] < 0)
                    {
                        o.LakeId[j] = id;
                        stack.Push(j);
                    }
                }
            }
        }

        private static float[] Signed(OutsideWater o, Func<int, bool> water)
        {
            float[] toLand = CompatSections.Distance(o.GW, o.GH, i => !water(i), 32f);
            float[] toWater = CompatSections.Distance(o.GW, o.GH, water, 6f);
            var d = new float[toLand.Length];
            for (int i = 0; i < d.Length; i++) d[i] = water(i) ? toLand[i] - 0.5f : 0.5f - toWater[i];
            WaterFieldMath.Blur(d, o.GW, o.GH);
            WaterFieldMath.Blur(d, o.GW, o.GH);
            return d;
        }

        public static bool KindPrefix(int __0, int __1, ref byte __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            __result = o.Kind[o.Idx(__0, __1)];
            return false;
        }

        public static bool CornerPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = o.Seam.Connect("Corner", __0, __1, o.ShoreAt);
            return false;
        }

        public static bool DeepCornerPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = o.Seam.Connect("DeepCorner", __0, __1, o.DeepAt);
            return false;
        }

        public static bool AlongCornerPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = o.Seam.Connect("AlongCorner", __0, __1, o.AlongAt);
            return false;
        }

        public static bool IceCornerPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = o.Seam.Connect("IceCorner", __0, __1, o.IceAt);
            return false;
        }

        public static bool FrozenPrefix(int __0, int __1, ref bool __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            __result = o.Frozen[o.Idx(__0, __1)];
            return false;
        }

        public static bool AwayFromSeaPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = Mathf.Clamp01(o.Seam.Connect("AwayFromSea", __0, __1, o.SeaAt));
            return false;
        }

        public static bool NotFrozenPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = o.Seam.IsEdge(__0, __1) ? o.Seam.Value("NotFrozen", __0, __1) : o.NotFrozenAt(__0, __1);
            return false;
        }

        public static bool NearPrefix(int __0, int __1, MethodBase __originalMethod, ref bool __result)
        {
            OutsideWater o = Building;
            if (o == null || !o.Seam.IsEdge(__0, __1)) return true;
            __result = o.Seam.Value(__originalMethod.Name, __0, __1) != 0f;
            return false;
        }

        public static bool DistancePrefix(IntVec3 __0, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0.x, __0.z)) return true;
            __result = o.Signed[o.Idx(__0.x, __0.z)];
            return false;
        }

        public static bool CellFalsePrefix(int __0, int __1, ref bool __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            __result = false;
            return false;
        }

        public static bool RoofedPrefix(int __0, int __1, ref bool __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            __result = o.Roofed[o.Idx(__0, __1)];
            return false;
        }

        public static bool FlavorAtPrefix(int __0, int __1, ref byte __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            __result = o.Flavor[o.Idx(__0, __1)];
            return false;
        }

        public static bool FlavorAroundPrefix(int __0, int __1, ref byte __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            __result = o.NearFlavor[o.Idx(__0, __1)];
            return false;
        }

        public static bool FlavorNearPrefix(int __0, int __1, ref byte __result)
        {
            OutsideWater o = Building;
            if (o != null && o.Seam.IsEdge(__0, __1))
            {
                __result = (byte)o.Seam.Value("FlavorNear", __0, __1);
                return false;
            }
            if (o == null || o.OnMap(__0, __1)) return true;
            byte b = 0;
            for (int dz = -1; dz <= 0; dz++)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    byte f = o.NearFlavor[o.Idx(__0 + dx, __1 + dz)];
                    if (f > b) b = f;
                }
            }
            __result = b;
            return false;
        }

        public static bool MarshCornerPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = o.Seam.Connect("MarshCorner", __0, __1, o.MarshAt);
            return false;
        }

        public static bool MarshReachPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || (o.OnMap(__0, __1) && !o.Seam.IsEdge(__0, __1))) return true;
            __result = Mathf.Clamp(o.Seam.Connect("MarshReach", __0, __1, o.MarshReachAt), 0f, 6f);
            return false;
        }

        public static bool HoleNearPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            int still = 0, open = 0;
            for (int dz = -2; dz <= 2; dz++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (!o.InGrid(__0 + dx, __1 + dz)) continue;
                    int i = o.Idx(__0 + dx, __1 + dz);
                    if (o.Kind[i] != 1) continue;
                    still++;
                    if (!o.Roofed[i]) open++;
                }
            }
            __result = still > 0 ? (float)open / still : 0f;
            return false;
        }

        public static bool LakeAtPrefix(int __0, int __1, ref Vector2 __2, ref float __3, ref bool __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            int id = o.InGrid(__0, __1) ? o.LakeId[o.Idx(__0, __1)] : -1;
            __2 = id >= 0 ? o.Lakes[id].Middle : Vector2.zero;
            __3 = id >= 0 ? o.Lakes[id].Extent : 0f;
            __result = id >= 0;
            return false;
        }

        public static bool LakeExtentPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            __result = o.Nearest(__0, __1, out LakeInfo lake) ? lake.Extent : 0f;
            return false;
        }

        public static bool CaveNearPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            __result = o.Nearest(__0, __1, out LakeInfo lake) ? lake.Roofed : 0f;
            return false;
        }

        public static bool SwampNearPrefix(int __0, int __1, ref float __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            __result = o.Nearest(__0, __1, out LakeInfo lake) ? lake.Marshy : 0f;
            return false;
        }

        public static bool GlowHashPrefix(ref int __result)
        {
            if (Building == null) return true;
            __result = 0;
            return false;
        }

        public static void AnyMarshPostfix(ref bool __result)
        {
            if (Building != null && Building.AnyMarsh) __result = true;
        }

        public static bool CoveredNearPrefix(Vector2 __0, ref bool __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(Mathf.FloorToInt(__0.x), Mathf.FloorToInt(__0.y))) return true;
            __result = false;
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
            return ((int)version.GetValue(field), (int)iceVersion.GetValue(field), (int)floodVersion.GetValue(field));
        }

        public static IOutsideCompatLayer Build(HorizonState s)
        {
            OutsideWater o = BuildWater(s);
            if (o == null) return null;
            var layers = new List<(CellRect, SectionLayer)>();
            Building = o;
            try
            {
                foreach (CellRect strip in CompatSections.OutsideStrips(s.map, s.RingWidth))
                {
                    if (!FreshNear(o, strip)) continue;
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

        private static bool FreshNear(OutsideWater o, CellRect strip)
        {
            foreach (IntVec3 c in strip.ExpandedBy(4))
            {
                byte k = o.Kind[o.Idx(c.x, c.z)];
                if (k == 1 || k == 2 || k == 3) return true;
            }
            return false;
        }

        private sealed class Layer : IOutsideCompatLayer
        {
            private readonly List<(CellRect rect, SectionLayer layer)> layers;

            public Layer(List<(CellRect, SectionLayer)> layers) => this.layers = layers;

            public int Draw(CellRect view) => CompatSections.DrawLayers(layers, view);

            public void Dispose() => CompatSections.Destroy(layers);
        }
    }
}
