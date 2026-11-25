using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EndlessHorizon.Api;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static partial class Compat_QuietWonders
    {
        private sealed class Group
        {
            public string Name;
            public bool On;
            public string Reason = "";
            public readonly List<MethodInfo> Entries = new List<MethodInfo>();
            public readonly List<MethodInfo> ViewEntries = new List<MethodInfo>();
            public readonly List<MethodInfo> Transpiled = new List<MethodInfo>();
        }

        private static readonly Dictionary<MethodBase, MethodInfo> swaps = new Dictionary<MethodBase, MethodInfo>();
        private static readonly List<Group> groups = new List<Group>();
        private static FieldInfo dropField;
        private static Assembly qwAssembly;

        private const string Owner = "quietwonders";

        public static string Detail { get; private set; }

        private static readonly Type[] CellTypes =
        {
            typeof(GenGrid), typeof(GridsUtility), typeof(TerrainGrid), typeof(GlowGrid), typeof(RoofGrid), typeof(FogGrid),
            typeof(ThingGrid), typeof(EdificeGrid), typeof(CellIndices), typeof(WaterInfo), typeof(SnowGrid),
        };

        private static readonly Type[] CellCallTypes = { typeof(FishingUtility), typeof(Area), typeof(GenTemperature), typeof(VacuumUtility) };

        private static readonly string[] PlaceMembers =
        {
            "get_Position", "get_PositionHeld", "get_DrawPos", "get_DrawPosHeld", "get_InteractionCell", "get_InteractionCells",
        };

        private static readonly string[] SafeTypes =
        {
            "QWBands", "QWSpawn", "QWSound", "QWCounts", "QWLedger", "QWRand", "QWTime", "QWSeasons", "QWFleckSystem",
            "QWPuddles", "QWGust", "QWStir", "QWDefOf", "QWSettings", "QuietWondersMod", "QWTerrain",
        };

        public static string Enable(Harmony harmony)
        {
            Type sky = T("QWSky"), mesh = T("QWCellMesh"), classics = T("QWClassics"), effects = T("QWEffects"), land = T("QWLandscape"), bands = T("QWBands");
            Type places = T("QWPlaces"), pond = T("QWPond");
            if (sky == null || mesh == null) return "QWSky/QWCellMesh not found";
            qwAssembly = sky.Assembly;
            dropField = bands != null ? AccessTools.Field(bands, "Drop") : null;
            if (dropField == null || dropField.FieldType != typeof(int) || !dropField.IsStatic) return "QWBands.Drop not found";

            Swap(AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds), new[] { typeof(IntVec3), typeof(Map) }), nameof(W_InBounds));
            Swap(AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Fogged), new[] { typeof(IntVec3), typeof(Map) }), nameof(W_Fogged));
            Swap(AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Roofed), new[] { typeof(IntVec3), typeof(Map) }), nameof(W_Roofed));
            Swap(AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.GetTerrain), new[] { typeof(IntVec3), typeof(Map) }), nameof(W_GetTerrain));
            Swap(AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.GetEdifice), new[] { typeof(IntVec3), typeof(Map) }), nameof(W_GetEdifice));
            Swap(AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.GetPlant), new[] { typeof(IntVec3), typeof(Map) }), nameof(W_GetPlant));
            Swap(AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.BaseTerrainAt), new[] { typeof(IntVec3) }), nameof(W_BaseTerrainAt));
            Swap(AccessTools.Method(typeof(GlowGrid), nameof(GlowGrid.GroundGlowAt), new[] { typeof(IntVec3), typeof(bool), typeof(bool) }), nameof(W_GroundGlowAt));
            Swap(AccessTools.Method(typeof(WaterInfo), nameof(WaterInfo.GetWaterMovement), new[] { typeof(Vector3) }), nameof(W_GetWaterMovement));
            Swap(AccessTools.Method(typeof(CellRect), nameof(CellRect.ClipInsideMap), new[] { typeof(Map) }), nameof(W_Clip));
            Swap(AccessTools.Method(typeof(SnowGrid), nameof(SnowGrid.GetDepth), new[] { typeof(IntVec3) }), nameof(W_SnowDepth));
            Swap(AccessTools.Method(typeof(GenGrid), nameof(GenGrid.Standable), new[] { typeof(IntVec3), typeof(Map) }), nameof(W_Standable));
            Swap(AccessTools.Method(typeof(FishingUtility), nameof(FishingUtility.GetWaterBody), new[] { typeof(IntVec3), typeof(Map) }), nameof(W_WaterBody));

            MethodInfo ensure = AccessTools.Method(mesh, "Ensure", new[] { typeof(Map), typeof(CellRect), typeof(float) });
            AddGroup("cloud shadows", new[] { M(sky, "DrawClouds", MapFirst(2)) }, null, new[] { M(sky, "DrawClouds", MapFirst(2)), ensure });
            AddGroup("fog banks", new[] { M(sky, "DrawFog", MapFirst(2)) }, null,
                new[] { M(sky, "DrawFog", MapFirst(2)), M(sky, "BuildFogBase", p => p.Length == 2 && p[0].ParameterType == typeof(Map) && p[1].ParameterType == typeof(CellRect)), ensure });
            MethodInfo open = M(classics, "Open", CellMap);
            AddGroup("rain splashes and rings", null, new[] { M(classics, "Rain", ViewFirst) }, new[] { M(classics, "Rain", ViewFirst), open });
            AddGroup("snowflakes", null, new[] { M(classics, "Snow", ViewFirst) }, new[] { M(classics, "Snow", ViewFirst), open });
            AddGroup("water mist", null, new[] { M(effects, "Mist", ViewFirst) },
                new[] { M(effects, "Mist", ViewFirst), M(effects, "Outdoor", CellMap), M(effects, "NearWater", CellMap) });
            MethodInfo visible = M(land, "Visible", CellMap), landDir = M(land, "LandDirection", CellMap);
            AddGroup("glow tide", null, new[] { M(land, "GlowTide", ViewFirst) }, new[] { M(land, "GlowTide", ViewFirst), visible, landDir });
            AddGroup("moon path", null, new[] { M(land, "MoonPath", ViewFirst) }, new[] { M(land, "MoonPath", ViewFirst), visible });
            AddGroup("marsh wisps", null, new[] { M(land, "Marsh", ViewFirst) }, new[] { M(land, "Marsh", ViewFirst), visible });
            AddGroup("river flotsam", null, new[] { M(land, "River", ViewFirst) }, new[] { M(land, "River", ViewFirst), visible });
            AddGroup("drifting snow", null, new[] { M(places, "GroundDrift", ViewFirst) }, new[] { M(places, "GroundDrift", ViewFirst) });
            MethodInfo outdoor = M(effects, "Outdoor", CellMap), nearWater = M(effects, "NearWater", CellMap);
            AddGroup("butterflies and bees", null, new[] { M(effects, "Butterflies", ViewFirst) }, new[] { M(effects, "Butterflies", ViewFirst), outdoor });
            AddGroup("fireflies", null, new[] { M(classics, "Fireflies", ViewFirst) },
                new[] { M(classics, "Fireflies", ViewFirst), M(classics, "GoodFireflyCell", MapCell), open, nearWater });
            AddGroup("dragonflies", null, new[] { M(pond, "Dragonflies", ViewFirst) }, new[] { M(pond, "Dragonflies", ViewFirst), M(pond, "Shore", CellMap) });
            AddGroup("frogs", null, new[] { M(pond, "Frogs", ViewFirst) }, new[] { M(pond, "Frogs", ViewFirst), M(pond, "FindBanks", ViewFirst) });
            AddGroup("leaping fish", null, new[] { M(pond, "Fish", ViewFirst) }, new[] { M(pond, "Fish", ViewFirst), M(pond, "FindWater", ViewFirst) });

            var transpiled = new HashSet<MethodInfo>();
            var entered = new HashSet<MethodInfo>();
            var widened = new HashSet<MethodInfo>();
            foreach (Group g in groups)
            {
                g.Reason = g.Reason.Length > 0 ? g.Reason : Check(g);
                g.On = g.Reason.Length == 0;
                if (!g.On) continue;
                transpiled.UnionWith(g.Transpiled);
                entered.UnionWith(g.Entries);
                widened.UnionWith(g.ViewEntries);
            }
            if (transpiled.Count == 0) return "no effect group recognised (" + groups[0].Reason + ")";

            var transpiler = new HarmonyMethod(typeof(Compat_QuietWonders), nameof(Transpiler));
            foreach (MethodInfo m in transpiled) harmony.Patch(m, transpiler: transpiler);
            var exit = new HarmonyMethod(typeof(Compat_QuietWonders), nameof(ExitFinalizer));
            foreach (MethodInfo m in entered) harmony.Patch(m, prefix: new HarmonyMethod(typeof(Compat_QuietWonders), nameof(EnterPrefix)), finalizer: exit);
            foreach (MethodInfo m in widened) harmony.Patch(m, prefix: new HarmonyMethod(typeof(Compat_QuietWonders), nameof(EnterViewPrefix)), finalizer: exit);
            foreach (MethodInfo m in new[] { M(pond, "FindBanks", ViewFirst), M(pond, "FindWater", ViewFirst) })
            {
                if (m == null || !transpiled.Contains(m)) continue;
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(Compat_QuietWonders), nameof(ScanPrefix)),
                    finalizer: new HarmonyMethod(typeof(Compat_QuietWonders), nameof(ScanFinalizer)));
            }

            Detail = "on: " + string.Join(", ", groups.Where(g => g.On).Select(g => g.Name))
                + (groups.Any(g => !g.On) ? "\noff: " + string.Join("; ", groups.Where(g => !g.On).Select(g => g.Name + " (" + g.Reason + ")")) : "");
            HorizonInfo.Report(Owner, "effects", Detail);
            Log.Message(IntegrationLog.Prefix + "Quiet Wonders " + Detail.Replace('\n', ' '));
            return null;
        }

        private static Type T(string name) => AccessTools.TypeByName("QuietWonders." + name) ?? AccessTools.TypeByName(name);

        private static Func<ParameterInfo[], bool> MapFirst(int count) => p => p.Length == count && p[0].ParameterType == typeof(Map);

        private static bool ViewFirst(ParameterInfo[] p) => p.Length >= 2 && p[0].ParameterType == typeof(Map) && p[1].ParameterType == typeof(CellRect);

        private static bool CellMap(ParameterInfo[] p) => p.Length == 2 && p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(Map);

        private static bool MapCell(ParameterInfo[] p) => p.Length == 2 && p[0].ParameterType == typeof(Map) && p[1].ParameterType == typeof(IntVec3);

        private static MethodInfo M(Type t, string name, Func<ParameterInfo[], bool> ok) =>
            t == null ? null : AccessTools.GetDeclaredMethods(t).FirstOrDefault(m => m.Name == name && ok(m.GetParameters()));

        private static void Swap(MethodInfo from, string to)
        {
            if (from != null) swaps[from] = AccessTools.Method(typeof(Compat_QuietWonders), to);
        }

        private static void AddGroup(string name, MethodInfo[] entries, MethodInfo[] viewEntries, MethodInfo[] transpiled)
        {
            var g = new Group { Name = name };
            groups.Add(g);
            foreach (MethodInfo m in (entries ?? new MethodInfo[0]).Concat(viewEntries ?? new MethodInfo[0]).Concat(transpiled))
            {
                if (m == null)
                {
                    g.Reason = "method not found";
                    return;
                }
            }
            if (entries != null) g.Entries.AddRange(entries);
            if (viewEntries != null) g.ViewEntries.AddRange(viewEntries);
            g.Transpiled.AddRange(transpiled.Distinct());
        }

        private static bool Sensitive(MethodBase callee)
        {
            Type t = callee.DeclaringType;
            if (t == null) return false;
            if (Array.IndexOf(CellTypes, t) >= 0) return true;
            if (Array.IndexOf(CellCallTypes, t) >= 0) return callee.GetParameters().Any(p => p.ParameterType == typeof(IntVec3));
            return t == typeof(CellRect) && callee.GetParameters().Any(p => p.ParameterType == typeof(Map));
        }

        private static bool ReadsPlant(MethodBase callee) => swaps.TryGetValue(callee, out MethodInfo to) && to.Name == nameof(W_GetPlant);

        private static bool FitsPlant(Type t)
        {
            if (t == null) return false;
            if (t.IsByRef) t = t.GetElementType();
            return t != typeof(object) && t.IsAssignableFrom(typeof(Plant));
        }

        private static string PlantLeaves(MethodBase m, List<CodeInstruction> code)
        {
            if (m is MethodInfo mi && FitsPlant(mi.ReturnType) || m.GetParameters().Any(p => p.ParameterType.IsByRef && FitsPlant(p.ParameterType)))
                return "gives a plant back: " + m.Name;
            foreach (CodeInstruction ins in code)
            {
                if (ins.operand is FieldInfo field)
                {
                    bool store = ins.opcode == OpCodes.Stfld || ins.opcode == OpCodes.Stsfld;
                    if (store && FitsPlant(field.FieldType)) return "keeps a plant: " + m.Name + " -> " + field.DeclaringType?.Name + "." + field.Name;
                    bool place = field.FieldType == typeof(IntVec3) || field.FieldType == typeof(Vector3);
                    if (!store && !field.IsStatic && place && FitsPlant(field.DeclaringType))
                        return "asks where a plant is: " + m.Name + " -> " + field.DeclaringType.Name + "." + field.Name;
                    continue;
                }
                if (!(ins.operand is MethodBase callee) || ReadsPlant(callee)) continue;
                if (ins.opcode != OpCodes.Call && ins.opcode != OpCodes.Callvirt && ins.opcode != OpCodes.Newobj) continue;
                string name = callee.DeclaringType?.Name + "." + callee.Name;
                if (!callee.IsStatic && FitsPlant(callee.DeclaringType) && Array.IndexOf(PlaceMembers, callee.Name) >= 0)
                    return "asks where a plant is: " + m.Name + " -> " + name;
                if (callee.GetParameters().Any(p => FitsPlant(p.ParameterType))) return "hands a plant on: " + m.Name + " -> " + name;
            }
            return null;
        }

        private static string Check(Group g)
        {
            try
            {
                var seen = new HashSet<MethodBase>();
                foreach (MethodInfo m in g.Transpiled)
                {
                    string bad = Unsafe(m, g.Transpiled, 0, seen);
                    if (bad != null) return bad;
                }
                return "";
            }
            catch (Exception ex)
            {
                Log.Error(IntegrationLog.Prefix + "Quiet Wonders: reading the code of '" + g.Name + "' failed; that effect group stays off. " + ex);
                return "could not read: " + ex.GetType().Name;
            }
        }

        private static string Unsafe(MethodBase m, List<MethodInfo> transpiled, int depth, HashSet<MethodBase> seen)
        {
            if (!seen.Add(m)) return null;
            bool swapped = m is MethodInfo mi && transpiled.Contains(mi);
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(m);
            if (swapped && code.Any(i => i.operand is MethodBase read && ReadsPlant(read)))
            {
                string leaves = PlantLeaves(m, code);
                if (leaves != null) return leaves;
            }
            foreach (CodeInstruction ins in code)
            {
                if (!(ins.operand is MethodBase callee) || (ins.opcode != OpCodes.Call && ins.opcode != OpCodes.Callvirt)) continue;
                if (Sensitive(callee))
                {
                    if (!swapped || !swaps.ContainsKey(callee)) return "asks the map: " + m.Name + " -> " + callee.DeclaringType.Name + "." + callee.Name;
                    continue;
                }
                Type ct = callee.DeclaringType;
                if (ct == null || ct.Assembly != qwAssembly || depth >= 3) continue;
                Type outer = ct;
                while (outer.DeclaringType != null) outer = outer.DeclaringType;
                if (Array.IndexOf(SafeTypes, outer.Name) >= 0) continue;
                if (callee.IsAbstract || callee.GetMethodBody() == null) continue;
                string bad = Unsafe(callee, transpiled, depth + 1, seen);
                if (bad != null) return bad;
            }
            return null;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction i in instructions)
            {
                if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodBase callee && swaps.TryGetValue(callee, out MethodInfo to))
                {
                    yield return new CodeInstruction(OpCodes.Call, to) { labels = i.labels, blocks = i.blocks };
                }
                else
                {
                    yield return i;
                }
            }
        }

        private sealed class Ctx
        {
            public Map Map;
            public int W, H, L;
            public Memo Scan;
        }

        [ThreadStatic] private static Ctx ctx;
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static int[] scanStamp;

        private static bool Enter(Map map)
        {
            depth++;
            if (depth > 1) return ctx != null && ctx.Map == map;
            ctx = null;
            if (map == null || (int)dropField.GetValue(null) != 0 || !HorizonInfo.IsActive(map) || !HorizonInfo.IsBuilt(map)) return false;
            if (HorizonExtensions.StateOf(Owner) != "active") return false;
            int ring = HorizonInfo.RingWidth(map), fogStart = HorizonInfo.FogStart(map);
            int l = Mathf.Clamp(fogStart + (ring - fogStart) / 2, 0, ring);
            if (l <= 0) return false;
            FieldShape shape = FieldShape.Of(map);
            ctx = new Ctx { Map = map, W = shape.W, H = shape.H, L = l };
            return true;
        }

        public static void EnterPrefix(Map __0) => Enter(__0);

        public static void EnterViewPrefix(Map __0, ref CellRect __1)
        {
            if (!Enter(__0) || depth > 1) return;
            CellRect seen = HorizonView.ViewOf(__0);
            if (seen.Width <= 0 || seen.Height <= 0) return;
            __1 = Zone(ctx, seen.ExpandedBy(2));
        }

        public static Exception ExitFinalizer(Exception __exception)
        {
            if (--depth <= 0)
            {
                depth = 0;
                ctx = null;
            }
            return __exception;
        }

        private static CellRect Zone(Ctx k, CellRect r) => CellRect.FromLimits(
            Math.Max(r.minX, -k.L), Math.Max(r.minZ, -k.L), Math.Min(r.maxX, k.W + k.L - 1), Math.Min(r.maxZ, k.H + k.L - 1));

        internal sealed class Memo
        {
            internal const ushort SideAsked = 1, IsPlayable = 2, ZoneAsked = 4, IsInZone = 8, TerrainAsked = 16,
                FogAsked = 32, IsFogged = 64, RoofAsked = 128, IsRoofed = 256, StandAsked = 512, CanStand = 1024;

            private const int MaxCells = 1 << 21;

            private int minX, minZ, width, height;
            private int[] stamp = new int[0];
            private ushort[] known = new ushort[0];
            private TerrainDef[] terrain = new TerrainDef[0];

            internal bool Start(int minX, int minZ, int width, int height, int[] stamp)
            {
                if (width <= 0 || height <= 0 || (long)width * height > MaxCells)
                {
                    this.width = this.height = 0;
                    return false;
                }
                if (minX == this.minX && minZ == this.minZ && width == this.width && height == this.height && Same(stamp)) return true;
                int cells = width * height;
                if (known.Length < cells)
                {
                    known = new ushort[cells];
                    terrain = new TerrainDef[cells];
                }
                else
                {
                    Array.Clear(known, 0, cells);
                }
                this.minX = minX;
                this.minZ = minZ;
                this.width = width;
                this.height = height;
                this.stamp = (int[])stamp.Clone();
                return true;
            }

            private bool Same(int[] other)
            {
                if (other.Length != stamp.Length) return false;
                for (int i = 0; i < other.Length; i++)
                    if (other[i] != stamp[i]) return false;
                return true;
            }

            internal int Slot(int x, int z)
            {
                x -= minX;
                z -= minZ;
                return (uint)x < (uint)width && (uint)z < (uint)height ? z * width + x : -1;
            }

            internal ushort Known(int slot) => known[slot];

            internal void Keep(int slot, ushort bits) => known[slot] |= bits;

            internal TerrainDef Terrain(int slot) => terrain[slot];

            internal void Keep(int slot, TerrainDef shown)
            {
                terrain[slot] = shown;
                known[slot] |= TerrainAsked;
            }
        }

        private static readonly ConditionalWeakTable<Map, Memo> memos = new ConditionalWeakTable<Map, Memo>();

        private static readonly string[] MemoChannels = { "space.outside", "terrain.top", "surface.rock", "surface.roof", "surface.fogged" };

        public static void ScanPrefix(Map __0)
        {
            Ctx k = ctx;
            if (k == null || k.Map != __0 || k.Scan != null) return;
            int[] stamp = scanStamp ??= new int[MemoChannels.Length];
            for (int i = 0; i < stamp.Length; i++) stamp[i] = HorizonInfo.Revision(__0, MemoChannels[i]);
            Memo memo = memos.GetValue(__0, _ => new Memo());
            if (memo.Start(-k.L, -k.L, k.W + 2 * k.L, k.H + 2 * k.L, stamp)) k.Scan = memo;
        }

        public static Exception ScanFinalizer(Exception __exception)
        {
            Ctx k = ctx;
            if (k != null) k.Scan = null;
            return __exception;
        }

        private static bool Kept(Ctx k, IntVec3 c, ushort asked, ushort yes, out bool answer, out int slot)
        {
            Memo memo = k.Scan;
            slot = memo == null ? -1 : memo.Slot(c.x, c.z);
            if (slot >= 0)
            {
                ushort known = memo.Known(slot);
                if ((known & asked) != 0)
                {
                    answer = (known & yes) != 0;
                    return true;
                }
            }
            answer = false;
            return false;
        }

        private static bool Keep(Ctx k, int slot, ushort asked, ushort yes, bool answer)
        {
            if (slot >= 0) k.Scan.Keep(slot, answer ? (ushort)(asked | yes) : asked);
            return answer;
        }

        private static bool Outside(Ctx k, IntVec3 c) =>
            Kept(k, c, Memo.SideAsked, Memo.IsPlayable, out bool playable, out int slot)
                ? !playable
                : !Keep(k, slot, Memo.SideAsked, Memo.IsPlayable, HorizonSpace.IsPlayable(k.Map, c));

        private static bool Off(IntVec3 c, Map map, out Ctx k)
        {
            k = ctx;
            return k != null && map == k.Map && Outside(k, c);
        }

        private static bool InZone(Ctx k, IntVec3 c) =>
            Kept(k, c, Memo.ZoneAsked, Memo.IsInZone, out bool inZone, out int slot) ? inZone : Keep(k, slot, Memo.ZoneAsked, Memo.IsInZone, AskZone(k, c));

        private static bool AskZone(Ctx k, IntVec3 c)
        {
            if (c.x < -k.L || c.z < -k.L || c.x >= k.W + k.L || c.z >= k.H + k.L) return false;
            if (!HorizonSpace.IsOutside(k.Map, c)) return false;
            IntVec3 near = HorizonSpace.NearestPlayable(k.Map, c);
            return Math.Max(Math.Abs(c.x - near.x), Math.Abs(c.z - near.z)) <= k.L;
        }

        private static TerrainDef TerrainOut(Ctx k, IntVec3 c)
        {
            Memo memo = k.Scan;
            int slot = memo == null ? -1 : memo.Slot(c.x, c.z);
            if (slot < 0) return AskTerrain(k, c);
            if ((memo.Known(slot) & Memo.TerrainAsked) != 0) return memo.Terrain(slot);
            TerrainDef shown = AskTerrain(k, c);
            memo.Keep(slot, shown);
            return shown;
        }

        private static TerrainDef AskTerrain(Ctx k, IntVec3 c)
        {
            TerrainDef t = HorizonWorld.TerrainAt(k.Map, c);
            if (t != null) return t;
            return k.Map.terrainGrid.BaseTerrainAt(HorizonSpace.NearestPlayable(k.Map, c)) ?? TerrainDefOf.Soil;
        }

        private static bool Rock(Ctx k, IntVec3 c) => HorizonWorld.RockAt(k.Map, c) != null;

        public static bool W_InBounds(IntVec3 c, Map map)
        {
            Ctx k = ctx;
            if (k == null || map != k.Map) return HorizonSpace.IsPlayable(map, c);
            Memo memo = k.Scan;
            int slot = memo == null ? -1 : memo.Slot(c.x, c.z);
            if (slot >= 0)
            {
                ushort known = memo.Known(slot);
                if ((known & Memo.IsPlayable) != 0) return true;
                if ((known & Memo.SideAsked) != 0 && (known & Memo.ZoneAsked) != 0) return (known & Memo.IsInZone) != 0;
            }
            return !Outside(k, c) || InZone(k, c);
        }

        public static bool W_Fogged(IntVec3 c, Map map) => Off(c, map, out Ctx k) ? FoggedOut(k, c) : c.Fogged(map);

        private static bool FoggedOut(Ctx k, IntVec3 c) =>
            Kept(k, c, Memo.FogAsked, Memo.IsFogged, out bool fogged, out int slot)
                ? fogged
                : Keep(k, slot, Memo.FogAsked, Memo.IsFogged, !InZone(k, c) || HorizonWorld.FoggedAt(k.Map, c));

        public static bool W_Roofed(IntVec3 c, Map map) => Off(c, map, out Ctx k) ? RoofedOut(k, c) : c.Roofed(map);

        private static bool RoofedOut(Ctx k, IntVec3 c) =>
            Kept(k, c, Memo.RoofAsked, Memo.IsRoofed, out bool roofed, out int slot)
                ? roofed
                : Keep(k, slot, Memo.RoofAsked, Memo.IsRoofed, !InZone(k, c) || Rock(k, c) || HorizonWorld.RoofAt(k.Map, c) != null);

        public static TerrainDef W_GetTerrain(IntVec3 c, Map map)
        {
            Ctx k = ctx;
            if (k == null || map != k.Map) return c.GetTerrain(map);
            Memo memo = k.Scan;
            int slot = memo == null ? -1 : memo.Slot(c.x, c.z);
            if (slot >= 0)
            {
                ushort known = memo.Known(slot);
                if ((known & Memo.IsPlayable) != 0) return c.GetTerrain(map);
                if ((known & Memo.TerrainAsked) != 0) return memo.Terrain(slot);
            }
            return Outside(k, c) ? TerrainOut(k, c) : c.GetTerrain(map);
        }

        public static Building W_GetEdifice(IntVec3 c, Map map) => Off(c, map, out _) ? null : c.GetEdifice(map);

        public static Plant W_GetPlant(IntVec3 c, Map map) => Off(c, map, out Ctx k) ? PlantOut(k, c) : c.GetPlant(map);

        private static Plant PlantOut(Ctx k, IntVec3 c) => InZone(k, c) ? HorizonWorld.PlantAt(k.Map, c) : null;

        public static TerrainDef W_BaseTerrainAt(TerrainGrid grid, IntVec3 c)
        {
            Ctx k = ctx;
            if (k != null && grid == k.Map.terrainGrid && Outside(k, c)) return TerrainOut(k, c);
            return grid.BaseTerrainAt(c);
        }

        public static float W_GroundGlowAt(GlowGrid grid, IntVec3 c, bool ignoreCavePlants, bool ignoreSky)
        {
            Ctx k = ctx;
            if (k != null && grid == k.Map.glowGrid && Outside(k, c))
            {
                return ignoreSky ? 0f : k.Map.skyManager.CurSkyGlow;
            }
            return grid.GroundGlowAt(c, ignoreCavePlants, ignoreSky);
        }

        public static Vector3 W_GetWaterMovement(WaterInfo info, Vector3 at)
        {
            Ctx k = ctx;
            IntVec3 c = at.ToIntVec3();
            if (k != null && info == k.Map.waterInfo && Outside(k, c)) return FlowOut(k, c);
            return info.GetWaterMovement(at);
        }

        public static CellRect W_Clip(ref CellRect rect, Map map)
        {
            Ctx k = ctx;
            if (k != null && map == k.Map) return Zone(k, rect);
            FieldShape shape = FieldShape.Of(map);
            rect.minX = Math.Max(rect.minX, 0);
            rect.minZ = Math.Max(rect.minZ, 0);
            rect.maxX = Math.Min(rect.maxX, shape.W - 1);
            rect.maxZ = Math.Min(rect.maxZ, shape.H - 1);
            return rect;
        }

        private static Vector3 FlowOut(Ctx k, IntVec3 c) => HorizonWorld.WaterFlowAt(k.Map, c);

        public static float W_SnowDepth(SnowGrid grid, IntVec3 c)
        {
            Ctx k = ctx;
            if (k != null && grid == k.Map.snowGrid && Outside(k, c)) return SnowOut(HorizonWorld.Value(k.Map, "surface.snow", c));
            return grid.GetDepth(c);
        }

        internal static float SnowOut(float shown) => float.IsNaN(shown) ? 0f : shown;

        public static bool W_Standable(IntVec3 c, Map map) => Off(c, map, out Ctx k) ? StandableOut(k, c) : c.Standable(map);

        private static bool StandableOut(Ctx k, IntVec3 c) =>
            Kept(k, c, Memo.StandAsked, Memo.CanStand, out bool stands, out int slot)
                ? stands
                : Keep(k, slot, Memo.StandAsked, Memo.CanStand,
                    InZone(k, c) && StandsOut(Rock(k, c), TerrainOut(k, c), HorizonWorld.PlantAt(k.Map, c)?.def));

        internal static bool StandsOut(bool rock, TerrainDef shown, ThingDef plant) =>
            !rock && shown != null && shown.passability != Traversability.Impassable && (plant == null || plant.passability == Traversability.Standable);

        public static WaterBody W_WaterBody(IntVec3 c, Map map) => Off(c, map, out _) ? null : c.GetWaterBody(map);
    }
}
