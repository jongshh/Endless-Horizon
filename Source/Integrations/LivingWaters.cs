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
    internal static class Compat_LivingWaters
    {
        private static Type fieldType, layerType, levelsType;
        private static MethodInfo peek, iceTouchedSince;
        private static MethodInfo fieldFor, classify, alongCorner, flavorOf, lakeAt, caveNear, swampNear;
        private static FieldInfo alongCells;
        private static PropertyInfo version, iceVersion, floodVersion;

        private const string Owner = "livingwaters", Step = Owner + ".field", WaterScope = Owner + ".water";
        private static readonly StepResult<OutsideWater> waters = new StepResult<OutsideWater>();

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
            peek = AccessTools.Method(fieldType, "Peek", new[] { typeof(Map) });
            iceTouchedSince = AccessTools.Method(fieldType, "IceTouchedSince", new[] { typeof(int), typeof(CellRect) });
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
                (AccessTools.Method(fieldType, "Covered", two), nameof(CoveredPrefix)),
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
            FloodSheet = PatchFloodSheet(harmony);
            return Register(harmony);
        }

        internal static bool FloodSheet { get; private set; }

        private static FieldInfo floodHere, anyFlood;
        private static MethodInfo floodsOn;
        private static PropertyInfo riversSettings;
        private static FieldInfo shoresOn;
        private static int sheetClips, sheetTemps;
        private static bool sheetOn;
        private static CellRect sheetRect, sheetSearch;

        private static bool PatchFloodSheet(Harmony harmony)
        {
            MethodInfo cover = AccessTools.Method(layerType, "FloodCover", new[] { fieldType, typeof(CellRect) });
            MethodInfo sheet = AccessTools.Method(layerType, "BuildFloodSheet", new[] { fieldType, typeof(float), typeof(float) });
            floodHere = AccessTools.Field(layerType, "floodHere");
            anyFlood = AccessTools.Field(layerType, "anyFlood");
            floodsOn = AccessTools.PropertyGetter(fieldType, "Floods");
            riversSettings = AccessTools.Property(AccessTools.TypeByName("RimWorldRivers.RiversMod"), "Settings");
            shoresOn = riversSettings == null ? null : AccessTools.Field(riversSettings.PropertyType, "shores");
            if (cover == null || cover.ReturnType != typeof(int) || sheet == null || floodHere?.FieldType != typeof(bool[])
                || anyFlood?.FieldType != typeof(bool) || floodsOn?.ReturnType != typeof(bool) || shoresOn?.FieldType != typeof(bool)) return false;
            sheetClips = sheetTemps = 0;
            harmony.Patch(sheet, prefix: new HarmonyMethod(typeof(Compat_LivingWaters), nameof(SheetPrefix)),
                transpiler: new HarmonyMethod(typeof(Compat_LivingWaters), nameof(SheetTranspiler)),
                finalizer: new HarmonyMethod(typeof(Compat_LivingWaters), nameof(SheetFinalizer)));
            if (sheetClips != 1 || sheetTemps != 1)
            {
                harmony.Unpatch(sheet, HarmonyPatchType.All, harmony.Id);
                return false;
            }
            harmony.Patch(cover, postfix: new HarmonyMethod(typeof(Compat_LivingWaters), nameof(FloodCoverPostfix)));
            return true;
        }

        public static IEnumerable<CodeInstruction> SheetTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo expand = AccessTools.Method(typeof(CellRect), nameof(CellRect.ExpandedBy), new[] { typeof(int) });
            MethodInfo clip = AccessTools.Method(typeof(CellRect), nameof(CellRect.ClipInsideMap), new[] { typeof(Map) });
            MethodInfo temp = AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.TempTerrainAt), new[] { typeof(int) });
            var list = new List<CodeInstruction>(instructions);
            for (int k = 0; k < list.Count; k++)
            {
                if (list[k].Calls(clip))
                {
                    int e = k - 1;
                    while (e >= 0 && k - e <= 6 && !list[e].Calls(expand)) e--;
                    if (e < 0 || k - e > 6) continue;
                    sheetClips++;
                    list.Insert(k + 1, new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(Compat_LivingWaters), nameof(SheetSearchRect))));
                    list.Insert(e + 1, new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(Compat_LivingWaters), nameof(SheetExpanded))));
                    k += 2;
                }
                else if (list[k].Calls(temp))
                {
                    sheetTemps++;
                    list[k].opcode = System.Reflection.Emit.OpCodes.Call;
                    list[k].operand = AccessTools.Method(typeof(Compat_LivingWaters), nameof(SheetTempAt));
                }
            }
            return list;
        }

        public static void SheetPrefix(Section ___section)
        {
            sheetOn = Building != null && ___section != null;
            if (sheetOn) sheetRect = ___section.CellRect;
        }

        public static void SheetFinalizer() => sheetOn = false;

        public static CellRect SheetExpanded(CellRect rect)
        {
            sheetSearch = rect;
            return rect;
        }

        public static CellRect SheetSearchRect(CellRect clipped)
        {
            OutsideWater o = Building;
            return sheetOn && o != null ? SearchRect(sheetSearch, o.R, o.W, o.H) : clipped;
        }

        internal static CellRect SearchRect(CellRect rect, int r, int w, int h)
        {
            rect.minX = Math.Max(rect.minX, -r);
            rect.minZ = Math.Max(rect.minZ, -r);
            rect.maxX = Math.Min(rect.maxX, w + r - 1);
            rect.maxZ = Math.Min(rect.maxZ, h + r - 1);
            return rect;
        }

        public static TerrainDef SheetTempAt(TerrainGrid grid, int ind)
        {
            OutsideWater o = Building;
            if (!sheetOn || o == null) return grid.TempTerrainAt(ind);
            if (!SheetCell(ind, o.W, sheetRect, out int x, out int z)) return null;
            return o.InMap(x, z) ? grid.TempTerrainAt(ind) : o.FloodAt(x, z);
        }

        internal static bool SheetCell(int ind, int mapWidth, CellRect rect, out int x, out int z)
        {
            int x0 = rect.minX - 3, span = rect.Width + 6;
            x = z = 0;
            if (mapWidth < span) return false;
            int shifted = ind - x0;
            z = shifted >= 0 ? shifted / mapWidth : -((-shifted + mapWidth - 1) / mapWidth);
            x = ind - z * mapWidth;
            return x < x0 + span && z >= rect.minZ - 3 && z <= rect.maxZ + 3;
        }

        public static void FloodCoverPostfix(CellRect __1, ref int __result)
        {
            OutsideWater o = Building;
            if (o == null || o.Flood == null || !FloodsShown()) return;
            var here = (bool[])floodHere.GetValue(null);
            __result = CoverOutside(here, __1, o, out bool any);
            anyFlood.SetValue(null, any);
        }

        internal static int CoverOutside(bool[] here, CellRect rect, OutsideWater o, out bool any)
        {
            int num = rect.Width + 6, hash = 17;
            any = false;
            for (int z = rect.minZ - 3; z <= rect.maxZ + 3; z++)
            {
                for (int x = rect.minX - 3; x <= rect.maxX + 3; x++)
                {
                    int at = (z - rect.minZ + 3) * num + (x - rect.minX + 3);
                    if (!o.InMap(x, z)) here[at] = o.Kind[o.Idx(x, z)] == 0 && o.FloodAt(x, z) != null;
                    any |= here[at];
                    hash = unchecked(hash * 31 + (here[at] ? 1 : 0));
                }
            }
            return hash;
        }

        private static bool FloodsShown()
        {
            object settings = riversSettings.GetValue(null);
            return settings != null && (bool)floodsOn.Invoke(null, null) && (bool)shoresOn.GetValue(settings);
        }

        private static bool FloodNear(OutsideWater o, CellRect strip)
        {
            if (o.Flood == null) return false;
            foreach (IntVec3 c in strip.ExpandedBy(3))
                if (!o.InMap(c.x, c.z) && o.FloodAt(c.x, c.z) != null) return true;
            return false;
        }

        private static string Register(Harmony harmony)
        {
            if (!HorizonExtensions.RegisterBuildStep(Owner, Step, BuildWaterSteps, new[] { "surface.frozen" })
                || !HorizonExtensions.RegisterScope(Owner, WaterScope, EnterWater)
                || !HorizonExtensions.RegisterLayer(Owner, layerType, new[] { WaterScope }, Wanted, joinMapEdge: false, callDrawLayer: false)
                || !HorizonExtensions.RegisterDraw(Owner, "before", BeforeDraw)) return "Section layout changed";
            RegisterValue("kind", (o, i) => o.Kind[i]);
            RegisterValue("flavor", (o, i) => o.Flavor[i]);
            RegisterValue("shoreDistance", (o, i) => o.Signed[i]);
            RegisterValue("depth", (o, i) => o.Deep[i]);
            RegisterValue("seaDistance", (o, i) => o.Sea[i]);
            RegisterValue("along", (o, i) => o.Kind[i] == 2 ? o.Along[i] : float.NaN);
            RegisterValue("marshDistance", (o, i) => o.MarshDist[i]);
            RegisterValue("marshReach", (o, i) => o.MarshReach[i]);
            RegisterValue("lake", (o, i) => o.LakeId[i]);
            HorizonInfo.Observe(Owner, Changed);
            OptionRedraws.Follow(harmony, Owner);
            HorizonInfo.Report(Owner, "flood sheet", FloodSheet ? "outside too" : "not recognized: the outside flood keeps vanilla's water");
            return null;
        }

        private static void RegisterValue(string name, Func<OutsideWater, int, float> value) =>
            HorizonExtensions.RegisterChannel(Owner, Owner + "." + name, (map, c) =>
            {
                OutsideWater o = waters.Building(map);
                return o != null && o.InGrid(c.x, c.z) ? value(o, o.Idx(c.x, c.z)) : float.NaN;
            });

        private sealed class WaterEntry : IDisposable
        {
            internal OutsideWater Before;

            public void Dispose() => Building = Before;
        }

        private static IDisposable EnterWater(Map map)
        {
            OutsideWater o = waters.Building(map);
            if (o == null) return null;
            var entry = new WaterEntry { Before = Building };
            Building = o;
            return entry;
        }

        internal sealed class OutsideWater
        {
            public int R, GW, GH, W, H;
            internal FieldShape Shape;
            internal int Version, FloodVersion;
            public byte[] Kind;
            public float[] Signed, Deep, Sea, Along, MarshDist, MarshReach, Ice;
            public byte[] Flavor, NearFlavor;
            public bool[] Roofed;
            public bool[] Frozen;
            public bool AnyMarsh;
            public bool[] Covered;
            public TerrainDef[] Flood;
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

            public bool InMap(int x, int z) => Shape != null ? Shape.InMap(x, z) : x >= 0 && z >= 0 && x < W && z < H;

            public bool OnMap(int x, int z) => Shape != null ? Shape.OnMap(x, z) : x >= 0 && z >= 0 && x <= W && z <= H;

            public int Idx(int x, int z)
            {
                x = Mathf.Clamp(x, -R, W + R - 1);
                z = Mathf.Clamp(z, -R, H + R - 1);
                return (z + R) * GW + (x + R);
            }

            public float CornerOf(float[] a, int x, int z) =>
                (a[Idx(x - 1, z - 1)] + a[Idx(x, z - 1)] + a[Idx(x - 1, z)] + a[Idx(x, z)]) * 0.25f;

            public bool CoveredAt(int x, int z) => Covered != null && Covered[Idx(x, z)];

            public TerrainDef FloodAt(int x, int z) => Flood != null && InGrid(x, z) ? Flood[Idx(x, z)] : null;

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

        private static IEnumerable<bool> BuildWaterSteps(Map map)
        {
            FieldShape shape = FieldShape.Of(map);
            foreach (bool step in shape.ReadSteps(map)) yield return step;
            int w = shape.W, h = shape.H, r = shape.Ring + Section.Size + 4;
            var o = new OutsideWater { Shape = shape.Rectangle ? null : shape, R = r, W = w, H = h, GW = w + 2 * r, GH = h + 2 * r };
            int n = o.GW * o.GH;
            o.Kind = new byte[n];
            var deep = new bool[n];
            var soggy = new bool[n];
            o.Flavor = new byte[n];
            o.Roofed = new bool[n];
            o.Frozen = new bool[n];
            bool any = false;
            bool floods = ModsConfig.OdysseyActive;
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
                        IntVec3 read = shape.ClampToBounds(x, z);
                        t = HorizonWorld.BaseTerrainAt(map, read);
                        if (HorizonWorld.RockAt(map, read) != null) t = null;
                        if (t != null && floods && KindOf(t) == 0)
                        {
                            TerrainDef top = HorizonWorld.TerrainAt(map, read);
                            if (top != null && top.IsFlood && top.IsWater)
                            {
                                o.Flood ??= new TerrainDef[n];
                                o.Flood[i] = top;
                            }
                        }
                    }
                    byte k = t == null ? (byte)0 : KindOf(t);
                    o.Kind[i] = k;
                    if (k != 0 && !o.InMap(x, z) && HorizonWorld.Value(map, "water.covered", c) == 1f)
                    {
                        o.Covered ??= new bool[n];
                        o.Covered[i] = true;
                    }
                    deep[i] = k != 0 && k != 3 && t.passability == Traversability.Impassable;
                    soggy[i] = k == 0 && t != null && (t.defName.Contains("Marsh") || t.defName.Contains("Mud"));
                    o.Flavor[i] = k != 0 && k != 4 ? FlavorOf(t) : (byte)0;
                    o.Roofed[i] = o.InMap(x, z) ? map.roofGrid.RoofAt(c)?.isNatural ?? false : HorizonWorld.RoofAt(map, c) != null;
                    o.Frozen[i] = o.InMap(x, z) ? RimWorld.TerrainDefOf.ThinIce != null && map.terrainGrid.TempTerrainAt(c) == RimWorld.TerrainDefOf.ThinIce : t?.canFreeze == true && HorizonWorld.Value(map, "surface.frozen", c) == 1f;
                    if (!o.InMap(x, z) && (k == 1 || k == 2 || k == 3)) any = true;
                }
                if (((z + r) & 3) == 3) yield return false;
            }
            if (o.Flood != null) any = true;
            if (!any)
            {
                waters.Made(map, null);
                yield break;
            }
            bool Water(int i) => o.Kind[i] == 1 || o.Kind[i] == 2 || o.Kind[i] == 4;
            o.Signed = Signed(o, i => Water(i));
            yield return false;
            o.Deep = Signed(o, i => Water(i) && deep[i]);
            yield return false;
            if (Array.IndexOf(o.Frozen, true) >= 0) o.Ice = Signed(o, i => o.Frozen[i]);
            else { o.Ice = new float[n]; Array.Fill(o.Ice, -5.5f); }
            o.Sea = IntegrationWater.Distance(o.GW, o.GH, i => o.Kind[i] == 4, 18f);
            yield return false;
            object field = fieldFor.Invoke(null, new object[] { map });
            o.Seam = o.Shape != null ? new WaterSeam(o.Shape) : new WaterSeam(w, h);
            o.Seam.Capture(field, "Corner", "DeepCorner", "IceCorner", "AlongCorner", "AwayFromSea", "NotFrozen", "MarshCorner", "MarshReach",
                "MovingNear", "StillNear", "OursNear", "MarshNear", "FlavorNear");
            yield return false;
            SpreadFlavor(o);
            BuildMarsh(o);
            yield return false;
            FindLakes(o, soggy, field);
            yield return false;

            o.Along = new float[n];
            var realAlong = (float[])alongCells.GetValue(field);
            if (realAlong.Length != w * h) throw new InvalidOperationException("Living Waters along grid dimensions changed.");
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++) o.Along[o.Idx(x, z)] = realAlong[z * w + x];
            var start = new List<(float value, float sign)>();
            int rivers = HorizonWorld.RiverCount(map);
            for (int index = 0; index < rivers; index++)
            {
                if (HorizonWorld.TryGetRiver(map, index, out Vector2 from, out Vector2 outward, out float _))
                {
                    Vector2 inward = -outward.normalized;
                    IntVec3 edge = HorizonSpace.NearestPlayable(map, new IntVec3(Mathf.FloorToInt(from.x + inward.x * 0.6f), 0, Mathf.FloorToInt(from.y + inward.y * 0.6f)));
                    IntVec3 within = HorizonSpace.NearestPlayable(map, new IntVec3(Mathf.FloorToInt(from.x + inward.x * 5f), 0, Mathf.FloorToInt(from.y + inward.y * 5f)));
                    int ex = edge.x, ez = edge.z, ix = within.x, iz = within.z;
                    float a0 = (float)alongCorner.Invoke(field, new object[] { ex + 1, ez + 1 });
                    float a1 = (float)alongCorner.Invoke(field, new object[] { ix + 1, iz + 1 });
                    start.Add((a0, a0 >= a1 ? 1f : -1f));
                }
                else start.Add((0f, 1f));
            }
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    if (o.InMap(x, z)) continue;
                    int i = (z + r) * o.GW + (x + r);
                    if (o.Kind[i] == 2 && HorizonWorld.TryRiverPath(map, new IntVec3(x, 0, z), out int ci, out float sAlong) && ci >= 0 && ci < start.Count)
                    {
                        o.Along[i] = start[ci].value + start[ci].sign * sAlong;
                    }
                }
                if (((z + r) & 15) == 15) yield return false;
            }
            o.Version = (int)version.GetValue(field);
            o.FloodVersion = (int)floodVersion.GetValue(field);
            waters.Made(map, o);
        }

        private static readonly Dictionary<TerrainDef, byte> kinds = new Dictionary<TerrainDef, byte>();
        private static readonly Dictionary<TerrainDef, byte> flavors = new Dictionary<TerrainDef, byte>();

        private static byte KindOf(TerrainDef t)
        {
            if (!kinds.TryGetValue(t, out byte k))
            {
                k = (byte)classify.Invoke(null, new object[] { t });
                kinds[t] = k;
            }
            return k;
        }

        private static byte FlavorOf(TerrainDef t)
        {
            if (!flavors.TryGetValue(t, out byte f))
            {
                f = (byte)flavorOf.Invoke(null, new object[] { t });
                flavors[t] = f;
            }
            return f;
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
            o.MarshReach = IntegrationWater.Distance(o.GW, o.GH, i => o.Kind[i] == 3, 6f);
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
            float[] toLand = IntegrationWater.Distance(o.GW, o.GH, i => !water(i), 32f);
            float[] toWater = IntegrationWater.Distance(o.GW, o.GH, water, 6f);
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

        public static bool CoveredPrefix(int __0, int __1, ref bool __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(__0, __1)) return true;
            __result = o.CoveredAt(__0, __1);
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

        public static bool CoveredNearPrefix(Vector2 __0, float __1, ref bool __result)
        {
            OutsideWater o = Building;
            if (o == null || o.InMap(Mathf.FloorToInt(__0.x), Mathf.FloorToInt(__0.y))) return true;
            __result = false;
            if (o.Covered == null) return false;
            for (int z = Mathf.FloorToInt(__0.y - __1), z1 = Mathf.FloorToInt(__0.y + __1); z <= z1 && !__result; z++)
                for (int x = Mathf.FloorToInt(__0.x - __1), x1 = Mathf.FloorToInt(__0.x + __1); x <= x1 && !__result; x++)
                    __result = !o.InMap(x, z) && o.CoveredAt(x, z);
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

        private static bool Wanted(Map map, CellRect strip)
        {
            OutsideWater o = waters.Building(map);
            return o != null && (FreshNear(o, strip) || FloodSheet && FloodNear(o, strip));
        }

        private const int PollFrames = 15;
        private const int FloodWaterReach = 16;

        private sealed class RiversWatch
        {
            internal bool Started;
            internal int LastVersion, LastIce, LastFlood, NextPoll;
        }

        private static readonly ConditionalWeakTable<Map, RiversWatch> watches = new ConditionalWeakTable<Map, RiversWatch>();

        private static void Changed(Map map, string id, CellRect area)
        {
            if (id == "disposed")
            {
                waters.Disposed(map);
                watches.Remove(map);
            }
            else if (id == Step)
            {
                waters.Shown(map);
                OutsideWater o = waters.Current(map);
                if (o == null) return;
                object field = fieldFor.Invoke(null, new object[] { map });
                if ((int)version.GetValue(field) != o.Version || (int)floodVersion.GetValue(field) != o.FloodVersion)
                    HorizonInfo.RequestRefresh(map, Owner, HorizonSpace.Bounds(map));
            }
            else if (id == "terrain.base" && IntegrationWater.Within(map, area, 8, t => t.IsWater))
            {
                HorizonInfo.RequestRefresh(map, Owner, area);
            }
            else if (id == "terrain.top" && FloodSheet && ModsConfig.OdysseyActive && IntegrationWater.Within(map, area, FloodWaterReach, t => t.IsWater))
            {
                HorizonInfo.RequestRefresh(map, Owner, area);
            }
        }

        private static void BeforeDraw(Map map, CellRect view)
        {
            RiversWatch watch = watches.GetOrCreateValue(map);
            if (Time.frameCount < watch.NextPoll) return;
            watch.NextPoll = Time.frameCount + PollFrames;
            object field = (peek ?? fieldFor).Invoke(null, new object[] { map });
            if (field == null) return;
            int v = (int)version.GetValue(field), ice = (int)iceVersion.GetValue(field), flood = (int)floodVersion.GetValue(field);
            if (watch.Started)
            {
                if (v != watch.LastVersion || flood != watch.LastFlood) HorizonInfo.RequestRefresh(map, Owner, HorizonSpace.Bounds(map));
                else if (ice != watch.LastIce)
                {
                    FieldShape shape = FieldShape.Of(map);
                    int w = shape.W, h = shape.H, d = 10;
                    CellRect[] bands = waters.Current(map)?.Shape != null ? new[] { new CellRect(0, 0, w, h) }
                        : new[] { new CellRect(0, 0, w, d), new CellRect(0, h - d, w, d), new CellRect(0, 0, d, h), new CellRect(w - d, 0, d, h) };
                    foreach (CellRect band in bands)
                    {
                        if (iceTouchedSince == null || (bool)iceTouchedSince.Invoke(field, new object[] { watch.LastIce, band })) HorizonInfo.RequestRefresh(map, Owner, band);
                    }
                }
            }
            watch.Started = true;
            watch.LastVersion = v;
            watch.LastIce = ice;
            watch.LastFlood = flood;
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

    }
}
