using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EndlessHorizon.Api;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal static class Compat_RealisticRainOutside
    {
        private const string Owner = "realisticrain", Step = Owner + ".field";

        internal const int FadeCells = 8;

        internal static string Detail { get; private set; } = "off: not set up";
        internal static bool On { get; private set; }
        internal static bool Puddles { get; private set; }

        internal static bool Hidden;

        private static Type layerType, compType;
        private static MethodInfo clipMethod;
        private static FieldInfo matField, activeField, waterField, wetField, basinField, pixelsField, texDirtyField;
        private static Func<TerrainDef, float> factorOf;
        private delegate CellRect ClipFn(ref CellRect rect, Map map);
        private static ClipFn clipToMap;
        private static AccessTools.FieldRef<SectionLayer, Section> layerSection;
        private static AccessTools.FieldRef<MapDrawer, Section[,]> drawerSections;

        internal static MethodInfo DebugFill { get; private set; }

        internal static Type ComponentType => compType;

        internal static string Enable(Harmony harmony, string modRoot = null)
        {
            On = Puddles = false;
            string problem, basinProblem;
            try
            {
                problem = Prepare(modRoot, out basinProblem, out MethodBase regenerate, out MethodBase materialGetter, out MethodBase simulate);
                if (problem == null)
                {
                    harmony.Patch(regenerate, prefix: new HarmonyMethod(typeof(Compat_RealisticRainOutside), nameof(RegeneratePrefix)),
                        finalizer: new HarmonyMethod(typeof(Compat_RealisticRainOutside), nameof(RegenerateFinalizer)),
                        transpiler: new HarmonyMethod(typeof(Compat_RealisticRainOutside), nameof(Transpiler)));
                    harmony.Patch(materialGetter, prefix: new HarmonyMethod(typeof(Compat_RealisticRainOutside), nameof(MaterialPrefix)));
                    harmony.Patch(simulate, postfix: new HarmonyMethod(typeof(Compat_RealisticRainOutside), nameof(SimulatePostfix)));
                    if (!HorizonExtensions.RegisterLayer(Owner, layerType, null, Wanted, joinMapEdge: false, callDrawLayer: false)
                        || !HorizonExtensions.RegisterBuildStep(Owner, Step, BuildSteps, new[] { "terrain.top", "weather.exposed", "surface.rock" })
                        || !HorizonExtensions.RegisterDraw(Owner, "before", BeforeDraw))
                        problem = "registration refused";
                    else HorizonInfo.Observe(Owner, Changed);
                }
            }
            catch (Exception ex)
            {
                Log.Error(IntegrationLog.Prefix + "Realistic Rain outside the map could not be set up and stays off. " + ex);
                problem = "setup failed: " + ex.GetType().Name;
                basinProblem = null;
            }
            if (problem != null)
            {
                Detail = "off: " + problem;
                return problem;
            }
            Puddles = basinProblem == null;
            On = true;
            Detail = "on: wet ground, " + (Puddles ? "puddles (toxic, blood and ice tints)" : "no puddles (" + basinProblem + ")")
                + ", raindrop rings, splashes; left out: snow patches, puddle reflections, flood cover";
            return null;
        }

        private static string Prepare(string modRoot, out string basinProblem, out MethodBase regenerate, out MethodBase materialGetter, out MethodBase simulate)
        {
            basinProblem = null;
            regenerate = materialGetter = simulate = null;
            layerType = AccessTools.TypeByName("RealisticRain.SectionLayer_Puddles");
            compType = AccessTools.TypeByName("RealisticRain.MapComponent_Puddles");
            Type render = AccessTools.TypeByName("RealisticRain.PuddleRender"), wetness = AccessTools.TypeByName("RealisticRain.Wetness");
            if (layerType == null || compType == null || render == null || wetness == null) return "Realistic Rain types not found";
            if (!typeof(SectionLayer).IsAssignableFrom(layerType) || layerType.GetConstructor(new[] { typeof(Section) }) == null)
                return "SectionLayer_Puddles(Section) missing";
            if (!typeof(MapComponent).IsAssignableFrom(compType)) return "MapComponent_Puddles changed";
            regenerate = AccessTools.DeclaredMethod(layerType, "Regenerate", Type.EmptyTypes);
            materialGetter = AccessTools.DeclaredPropertyGetter(layerType, "Material");
            clipMethod = AccessTools.Method(typeof(CellRect), nameof(CellRect.ClipInsideMap), new[] { typeof(Map) });
            simulate = AccessTools.DeclaredMethod(compType, "Simulate", Type.EmptyTypes);
            MethodBase init = AccessTools.DeclaredMethod(compType, "Init", Type.EmptyTypes);
            MethodInfo factor = AccessTools.Method(wetness, "Factor", new[] { typeof(TerrainDef) });
            matField = AccessTools.Field(render, "Mat");
            activeField = AccessTools.Field(render, "ActiveMap");
            waterField = AccessTools.Field(compType, "water");
            wetField = AccessTools.Field(compType, "wet");
            basinField = AccessTools.Field(compType, "basin");
            pixelsField = AccessTools.Field(compType, "pixels");
            texDirtyField = AccessTools.Field(compType, "texDirty");
            DebugFill = AccessTools.Method(compType, "DebugFill", new[] { typeof(float), typeof(float) });
            if (materialGetter == null || ((MethodInfo)materialGetter).ReturnType != typeof(Material) || clipMethod == null || simulate == null)
                return "SectionLayer_Puddles or MapComponent_Puddles members missing";
            if (factor == null || !factor.IsStatic || factor.ReturnType != typeof(float)) return "Wetness.Factor missing";
            if (matField?.FieldType != typeof(Material) || !matField.IsStatic || activeField?.FieldType != typeof(Map) || !activeField.IsStatic)
                return "PuddleRender.Mat/ActiveMap missing";
            if (waterField?.FieldType != typeof(float[]) || wetField?.FieldType != typeof(float[]) || basinField?.FieldType != typeof(float[])
                || pixelsField?.FieldType != typeof(Color32[]) || texDirtyField?.FieldType != typeof(bool))
                return "MapComponent_Puddles fields changed";
            string bad = RealisticRainRules.RegenerateProblem(regenerate, clipMethod, materialGetter) ?? RealisticRainRules.EncodingProblem(simulate);
            if (bad != null) return bad;
            bad = RealisticRainRules.FragmentProblem(ShaderText(modRoot, "RR_Puddle.frag.glsl"))
                ?? RealisticRainRules.VertexProblem(ShaderText(modRoot, "RR_World.vert.glsl"));
            if (bad != null) return bad;
            basinProblem = RealisticRainRules.BasinProblem(init);
            layerSection = AccessTools.FieldRefAccess<SectionLayer, Section>("section");
            drawerSections = AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");
            factorOf = (Func<TerrainDef, float>)Delegate.CreateDelegate(typeof(Func<TerrainDef, float>), factor);
            clipToMap = (ClipFn)Delegate.CreateDelegate(typeof(ClipFn), clipMethod);
            return null;
        }

        private static string ShaderText(string modRoot, string file)
        {
            var roots = new List<string>();
            if (modRoot != null) roots.Add(modRoot);
            else
            {
                Assembly rr = layerType.Assembly;
                foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
                {
                    if (pack?.assemblies?.loadedAssemblies == null || !pack.assemblies.loadedAssemblies.Contains(rr)) continue;
                    foreach (string folder in pack.foldersToLoadDescendingOrder) roots.Add(folder);
                    roots.Add(pack.RootDir);
                }
            }
            foreach (string root in roots)
            {
                string path = Path.Combine(root, "Shaders", "RealisticRain", file);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            return null;
        }

        private const int Unknown = -3, OpenWater = -2, NoPuddles = -1;
        private static readonly List<float> kindFactors = new List<float>();
        private static readonly Dictionary<float, int> kindOfFactor = new Dictionary<float, int>();
        private static TerrainDef[] indexed = new TerrainDef[0];
        private static int[] kindAt = new int[0];
        private static readonly Dictionary<TerrainDef, int> kindOfOther = new Dictionary<TerrainDef, int>();
        private static bool everyDefKnown;

        private static int KindOf(TerrainDef t)
        {
            if (t == null) return NoPuddles;
            int i = t.index;
            if (i < indexed.Length && indexed[i] == t) return kindAt[i];
            if (kindOfOther.TryGetValue(t, out int kind)) return kind;
            float f = factorOf(t);
            if (f < 0f) kind = OpenWater;
            else if (!(f > 0f)) kind = NoPuddles;
            else if (!kindOfFactor.TryGetValue(f, out kind))
            {
                kind = kindFactors.Count;
                kindFactors.Add(f);
                kindOfFactor.Add(f, kind);
            }
            if (i < 65536 && (i >= indexed.Length || indexed[i] == null))
            {
                if (i >= indexed.Length)
                {
                    int size = Math.Max(i + 1, indexed.Length * 2);
                    Array.Resize(ref indexed, size);
                    int was = kindAt.Length;
                    Array.Resize(ref kindAt, size);
                    for (int k = was; k < size; k++) kindAt[k] = Unknown;
                }
                indexed[i] = t;
                kindAt[i] = kind;
            }
            else kindOfOther[t] = kind;
            return kind;
        }

        private static void KnowEveryDef()
        {
            if (everyDefKnown) return;
            everyDefKnown = true;
            foreach (TerrainDef t in DefDatabase<TerrainDef>.AllDefsListForReading) KindOf(t);
        }

        internal sealed class RainGrid
        {
            internal const byte Closed = 0, Live = 1, Water = 2, Inside = 3;
            internal FieldShape Shape;
            internal int W, H, Band, End, West, South, East, North, GW, GH;
            internal byte[] Kind;
            internal int LiveCount;
            internal int[] LiveCell, LiveKind, LiveTexel;
            internal float[] LiveFade, LiveBucket;
            internal byte[] LiveQuadrant, LiveSnow;
            internal int SnowRevision = int.MinValue;
            internal int WaterCount;
            internal int[] EdgeTexel, EdgeMap;
            internal bool[] AnchorRows, AnchorColumns;
            internal bool AnchorCorner;
            internal readonly HashSet<CellRect> Strips = new HashSet<CellRect>();
            internal int Straddling;

            internal int Index(int x, int z) => (z + South) * GW + x + West;

            internal bool Covers(CellRect r) => r.minX >= -West && r.maxX < East && r.minZ >= -South && r.maxZ < North;

            internal bool Drawable(CellRect r)
            {
                bool any = false;
                for (int z = r.minZ; z <= r.maxZ; z++)
                    for (int x = r.minX; x <= r.maxX; x++)
                    {
                        byte k = Kind[Index(x, z)];
                        if (k == Inside) return false;
                        any |= k == Live || k == Water;
                    }
                return any;
            }
        }

        private static readonly StepResult<RainGrid> grids = new StepResult<RainGrid>();

        private static IEnumerable<bool> BuildSteps(Map map)
        {
            if (!On)
            {
                grids.Made(map, null);
                yield break;
            }
            FieldShape shape = FieldShape.Of(map);
            foreach (bool step in shape.ReadSteps(map)) yield return step;
            int ring = shape.Ring, fogStart = HorizonInfo.FogStart(map);
            int band = HorizonInfo.FogShown(map) ? Mathf.Clamp(fogStart + (ring - fogStart) / 2, 0, ring) : ring;
            int end = Math.Min(band + FadeCells, ring);
            if (end <= 0)
            {
                grids.Made(map, null);
                yield break;
            }
            KnowEveryDef();
            var g = new RainGrid { Shape = shape, W = shape.W, H = shape.H, Band = band, End = end };
            RealisticRainRules.Coverage(g.W, g.H, end, Section.Size, out g.West, out g.South, out g.East, out g.North);
            g.GW = g.West + g.East;
            g.GH = g.South + g.North;
            g.Kind = new byte[g.GW * g.GH];
            Perlin noise = Puddles ? RealisticRainRules.BasinNoise(map.ConstantRandSeed) : null;
            var top = new TerrainDef[g.GW];
            float[] open = new float[g.GW], distance = new float[g.GW], outside = new float[g.GW];
            var cells = new List<int>();
            var kinds = new List<int>();
            var texels = new List<int>();
            var fades = new List<float>();
            var buckets = new List<float>();
            var quadrants = new List<byte>();
            for (int z = -g.South; z < g.North; z++)
            {
                var row = new CellRect(-g.West, z, g.GW, 1);
                if (!HorizonWorld.CopyTerrain(map, "terrain.top", row, top) || !HorizonWorld.Copy(map, "weather.exposed", row, open)
                    || !HorizonWorld.Copy(map, "space.distance", row, distance) || !HorizonWorld.Copy(map, "space.outside", row, outside))
                    throw new InvalidOperationException("the outside could not be read");
                for (int i = 0; i < g.GW; i++)
                {
                    int x = i - g.West, cell = (z + g.South) * g.GW + i;
                    if (shape.InMap(x, z))
                    {
                        g.Kind[cell] = RainGrid.Inside;
                        continue;
                    }
                    float fade = RealisticRainRules.Fade(distance[i], band, end);
                    if (fade <= 0f || outside[i] != 1f || open[i] != 1f || top[i] == null) continue;
                    int kind = KindOf(top[i]);
                    if (kind == OpenWater)
                    {
                        g.Kind[cell] = RainGrid.Water;
                        g.WaterCount++;
                        continue;
                    }
                    if (kind < 0) continue;
                    g.Kind[cell] = RainGrid.Live;
                    int q = RealisticRainRules.QuadrantOf(x, z);
                    RealisticRainRules.TextureSize(q, g.West, g.South, g.East, g.North, out int tw, out int _);
                    cells.Add(cell);
                    kinds.Add(kind);
                    fades.Add(fade);
                    buckets.Add(noise == null ? 0f : RealisticRainRules.BucketOf(RealisticRainRules.BasinAt(noise, x, z)));
                    quadrants.Add((byte)q);
                    texels.Add(RealisticRainRules.TexelOf(x, z, tw));
                }
                if (((z + g.South) & 3) == 3) yield return false;
            }
            g.LiveCount = cells.Count;
            g.LiveCell = cells.ToArray();
            g.LiveKind = kinds.ToArray();
            g.LiveTexel = texels.ToArray();
            g.LiveFade = fades.ToArray();
            g.LiveBucket = buckets.ToArray();
            g.LiveQuadrant = quadrants.ToArray();
            g.LiveSnow = new byte[g.LiveCount];
            yield return false;
            Edges(g);
            Anchors(g);
            grids.Made(map, g);
        }

        private static void Edges(RainGrid g)
        {
            var texel = new List<int>();
            var mapIndex = new List<int>();
            for (int z = 0; z < g.H; z++)
                for (int x = 0; x < g.W; x++)
                {
                    if (g.Kind[g.Index(x, z)] != RainGrid.Inside) continue;
                    bool edge = false;
                    for (int dz = -1; dz <= 1 && !edge; dz++)
                        for (int dx = -1; dx <= 1 && !edge; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            edge = nx >= -g.West && nx < g.East && nz >= -g.South && nz < g.North && g.Kind[g.Index(nx, nz)] != RainGrid.Inside;
                        }
                    if (!edge) continue;
                    texel.Add(RealisticRainRules.TexelOf(x, z, g.East));
                    mapIndex.Add(z * g.W + x);
                }
            g.EdgeTexel = texel.ToArray();
            g.EdgeMap = mapIndex.ToArray();
        }

        private static void Anchors(RainGrid g)
        {
            bool Shows(int x, int z)
            {
                byte k = g.Kind[g.Index(x, z)];
                return k == RainGrid.Live || k == RainGrid.Water;
            }
            g.AnchorRows = new bool[g.North];
            for (int z = 0; z < g.North; z++)
                for (int x = -g.West; x < 0 && !g.AnchorRows[z]; x++) g.AnchorRows[z] = Shows(x, z);
            g.AnchorColumns = new bool[g.East];
            for (int x = 0; x < g.East; x++)
                for (int z = -g.South; z < 0 && !g.AnchorColumns[x]; z++) g.AnchorColumns[x] = Shows(x, z);
            for (int z = -g.South; z < 0 && !g.AnchorCorner; z++)
                for (int x = -g.West; x < 0 && !g.AnchorCorner; x++) g.AnchorCorner = Shows(x, z);
        }

        private static bool Wanted(Map map, CellRect rect)
        {
            if (!On) return false;
            RainGrid g = grids.Building(map);
            if (g == null) return false;
            if (!RealisticRainRules.OneQuadrant(rect))
            {
                g.Straddling++;
                return false;
            }
            if (!g.Covers(rect) || !g.Drawable(rect) || matField.GetValue(null) == null) return false;
            g.Strips.Add(rect);
            return true;
        }

        internal sealed class Regen
        {
            internal Map Map;
            internal SectionLayer Layer;
            internal RainMap State;
            internal int Quadrant;
        }

        private static Regen regenerating;

        public static void RegeneratePrefix(SectionLayer __instance, out Regen __state)
        {
            __state = regenerating;
            regenerating = null;
            try
            {
                if (!On || __instance.GetType() != layerType) return;
                Section section = layerSection(__instance);
                Map map = section?.map;
                if (map == null || !StandIn(map, section)) return;
                CellRect rect = section.CellRect;
                if (!RealisticRainRules.OneQuadrant(rect)) return;
                regenerating = new Regen
                {
                    Map = map, Layer = __instance, State = maps.GetValue(map, _ => new RainMap()),
                    Quadrant = RealisticRainRules.QuadrantOf(rect.minX, rect.minZ),
                };
            }
            catch (Exception ex)
            {
                regenerating = null;
                Log.ErrorOnce(IntegrationLog.Prefix + "Realistic Rain outside: a strip could not be set up; it stays empty. " + ex, 0x45485252);
            }
        }

        public static Exception RegenerateFinalizer(Exception __exception, Regen __state)
        {
            regenerating = __state;
            return __exception;
        }

        private static bool StandIn(Map map, Section section)
        {
            Section[,] own = map.mapDrawer == null ? null : drawerSections(map.mapDrawer);
            IntVec3 at = section.botLeft;
            if (own == null || at.x < 0 || at.z < 0) return true;
            int ix = at.x / Section.Size, iz = at.z / Section.Size;
            return ix >= own.GetLength(0) || iz >= own.GetLength(1) || own[ix, iz] != section;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo to = AccessTools.Method(typeof(Compat_RealisticRainOutside), nameof(W_Clip));
            foreach (CodeInstruction i in instructions)
            {
                if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodBase m && m == clipMethod)
                    yield return new CodeInstruction(OpCodes.Call, to) { labels = i.labels, blocks = i.blocks };
                else yield return i;
            }
        }

        public static CellRect W_Clip(ref CellRect rect, Map map)
        {
            Regen r = regenerating;
            if (r != null && r.Map == map) return rect;
            return clipToMap(ref rect, map);
        }

        public static bool MaterialPrefix(SectionLayer __instance, ref Material __result)
        {
            Regen r = regenerating;
            if (r == null || r.Layer != __instance) return true;
            try
            {
                Material m = r.State.MaterialFor(r.Quadrant);
                if (m != null)
                {
                    __result = m;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(IntegrationLog.Prefix + "Realistic Rain outside: its material could not be copied; the strip stays empty. " + ex, 0x45485253);
            }
            regenerating = null;
            return true;
        }

        public static void SimulatePostfix(MapComponent __instance)
        {
            try
            {
                if (__instance?.map != null && maps.TryGetValue(__instance.map, out RainMap st) && (bool)texDirtyField.GetValue(__instance)) st.Dirty = true;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(IntegrationLog.Prefix + "Realistic Rain outside: following its step failed. " + ex, 0x45485254);
            }
        }

        private static int dataTexId = -1, mapSizeId, thingsTexId, baseTexId, reflId;
        private static Texture2D empty;

        private static void Ids()
        {
            if (dataTexId >= 0) return;
            mapSizeId = Shader.PropertyToID("_MapSize");
            thingsTexId = Shader.PropertyToID("_RR_ThingsTex");
            baseTexId = Shader.PropertyToID("_RR_BaseTex");
            reflId = Shader.PropertyToID("_RR_Refl");
            dataTexId = Shader.PropertyToID("_DataTex");
        }

        private static Texture2D Empty()
        {
            if (empty != null) return empty;
            empty = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = "EndlessHorizon_RealisticRain_Empty", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            empty.SetPixels32(new[] { new Color32(0, 0, 0, 0) });
            empty.Apply(false);
            return empty;
        }

        private static Texture2D NewTexture(int w, int h, string name, FilterMode filter) =>
            new Texture2D(w, h, TextureFormat.RGBA32, false, true) { name = name, filterMode = filter, wrapMode = TextureWrapMode.Clamp };

        internal sealed class RainMap
        {
            internal readonly Material[] Mats = new Material[4];
            internal readonly Texture2D[] Tex = new Texture2D[4];
            internal readonly Color32[][] Px = new Color32[4][];
            internal Texture2D Black;
            internal RainGrid Grid;
            internal MapComponent Comp;
            internal bool Dirty = true, Failed;
            internal int Bound = -1;
            internal readonly RealisticRainTable Table = new RealisticRainTable();
            internal float[] Row = new float[0];
            internal double RefreshMs, UploadMs, LongestMs;
            internal int Refreshes;

            internal Material MaterialFor(int q)
            {
                if (Mats[q] != null) return Mats[q];
                if (!(matField.GetValue(null) is Material rr)) return null;
                Ids();
                var m = new Material(rr) { name = "EndlessHorizon_RealisticRain_" + RealisticRainRules.QuadrantNames[q] };
                m.SetTexture(thingsTexId, Empty());
                m.SetVector(reflId, Vector4.zero);
                Mats[q] = m;
                Bind(this, q, false);
                Bound = -1;
                return m;
            }

            internal void Free()
            {
                for (int q = 0; q < 4; q++)
                {
                    if (Tex[q] != null) UnityEngine.Object.Destroy(Tex[q]);
                    if (Mats[q] != null) UnityEngine.Object.Destroy(Mats[q]);
                    Tex[q] = null;
                    Mats[q] = null;
                    Px[q] = null;
                }
                if (Black != null) UnityEngine.Object.Destroy(Black);
                Black = null;
                Grid = null;
            }
        }

        private static readonly ConditionalWeakTable<Map, RainMap> maps = new ConditionalWeakTable<Map, RainMap>();

        private static void Bind(RainMap st, int q, bool real)
        {
            Material m = st.Mats[q];
            if (m == null) return;
            Texture2D tex = real && !st.Failed ? st.Tex[q] : null;
            if (tex == null || st.Black == null)
            {
                m.SetTexture(dataTexId, Empty());
                m.SetTexture(baseTexId, Empty());
                m.SetVector(mapSizeId, RealisticRainRules.MapSizeOf(q, 1, 1));
                return;
            }
            m.SetTexture(dataTexId, tex);
            m.SetTexture(baseTexId, st.Black);
            m.SetVector(mapSizeId, RealisticRainRules.MapSizeOf(q, tex.width, tex.height));
        }

        private static void Attach(RainMap st, RainGrid g)
        {
            Ids();
            for (int q = 0; q < 4; q++)
            {
                RealisticRainRules.TextureSize(q, g.West, g.South, g.East, g.North, out int w, out int h);
                if (st.Tex[q] == null || st.Tex[q].width != w || st.Tex[q].height != h)
                {
                    if (st.Tex[q] != null) UnityEngine.Object.Destroy(st.Tex[q]);
                    st.Tex[q] = NewTexture(w, h, "EndlessHorizon_RealisticRain_" + RealisticRainRules.QuadrantNames[q], FilterMode.Bilinear);
                    st.Px[q] = new Color32[w * h];
                }
                else Array.Clear(st.Px[q], 0, st.Px[q].Length);
            }
            if (st.Black == null || st.Black.width != g.East || st.Black.height != g.North)
            {
                if (st.Black != null) UnityEngine.Object.Destroy(st.Black);
                st.Black = NewTexture(g.East, g.North, "EndlessHorizon_RealisticRain_NoTrees", FilterMode.Point);
                st.Black.SetPixels32(new Color32[g.East * g.North]);
                st.Black.Apply(false);
            }
            var water = new Color32(0, 0, 255, 255);
            for (int z = -g.South; z < g.North; z++)
                for (int x = -g.West; x < g.East; x++)
                {
                    if (g.Kind[g.Index(x, z)] != RainGrid.Water) continue;
                    int q = RealisticRainRules.QuadrantOf(x, z);
                    RealisticRainRules.TextureSize(q, g.West, g.South, g.East, g.North, out int w, out int _);
                    st.Px[q][RealisticRainRules.TexelOf(x, z, w)] = water;
                }
            for (int z = 0; z < g.North; z++)
                if (g.AnchorRows[z]) st.Px[RealisticRainRules.West][RealisticRainRules.TexelOf(-1, z, g.West)].a = 255;
            for (int x = 0; x < g.East; x++)
                if (g.AnchorColumns[x]) st.Px[RealisticRainRules.South][RealisticRainRules.TexelOf(x, -1, g.East)].a = 255;
            if (g.AnchorCorner) st.Px[RealisticRainRules.SouthWest][0].a = 255;
            if (st.Row.Length < g.GW) st.Row = new float[g.GW];
            st.Grid = g;
            st.Dirty = true;
            st.Bound = -1;
        }

        private static void BeforeDraw(Map map, CellRect view)
        {
            if (!On) return;
            RainGrid g = grids.Current(map);
            if (g == null || !(activeField.GetValue(null) is Map active) || active != map) return;
            if (HorizonExtensions.StateOf(Owner) != "active") return;
            RainMap st = maps.GetValue(map, _ => new RainMap());
            if (st.Failed) return;
            try
            {
                if (st.Grid != g) Attach(st, g);
                int bound = Hidden ? 0 : 1;
                if (st.Bound != bound)
                {
                    for (int q = 0; q < 4; q++) Bind(st, q, !Hidden);
                    st.Bound = bound;
                }
                if (HorizonInfo.Revision(map, "surface.snow") != g.SnowRevision) st.Dirty = true;
                if (st.Dirty) Refresh(map, st);
            }
            catch (Exception ex)
            {
                st.Failed = true;
                for (int q = 0; q < 4; q++) Bind(st, q, false);
                Detail = "off: drawing failed (see the log)";
                HorizonInfo.Report(Owner, "outside", Detail);
                Log.Error(IntegrationLog.Prefix + "Realistic Rain outside failed and is off for this map. " + ex);
            }
        }

        private static MapComponent ComponentOf(Map map)
        {
            List<MapComponent> list = map.components;
            if (list == null) return null;
            foreach (MapComponent c in list)
                if (c != null && c.GetType() == compType) return c;
            return null;
        }

        private static void Refresh(Map map, RainMap st)
        {
            long started = Stopwatch.GetTimestamp();
            RainGrid g = st.Grid;
            MapComponent comp = st.Comp ??= ComponentOf(map);
            var pixels = comp == null ? null : pixelsField.GetValue(comp) as Color32[];
            var water = comp == null ? null : waterField.GetValue(comp) as float[];
            var wet = comp == null ? null : wetField.GetValue(comp) as float[];
            var basin = comp == null ? null : basinField.GetValue(comp) as float[];
            int cells = g.W * g.H;
            bool inside = pixels != null && water != null && wet != null && basin != null
                && pixels.Length == cells && water.Length == cells && wet.Length == cells && basin.Length == cells;
            st.Table.Start(kindFactors);
            if (inside)
            {
                TerrainGrid terrain = map.terrainGrid;
                SnowGrid snow = map.snowGrid;
                int stride = cells >= 40000 ? 2 : 1;
                for (int z = 0; z < g.H; z += stride)
                {
                    for (int x = 0; x < g.W; x += stride)
                    {
                        int i = z * g.W + x;
                        Color32 px = pixels[i];
                        if (px.a != 255 || px.b != 0 || !g.Shape.InMap(x, z)) continue;
                        int kind = KindOf(terrain.TerrainAt(i));
                        if (kind < 0) continue;
                        float depth = snow.GetDepth(new IntVec3(x, 0, z));
                        st.Table.Add(kind, RealisticRainRules.SnowLevel(depth), RealisticRainRules.BucketOf(basin[i]), water[i], wet[i]);
                    }
                }
            }
            st.Table.Finish();
            int revision = HorizonInfo.Revision(map, "surface.snow");
            if (revision != g.SnowRevision)
            {
                ReadSnow(map, g, st);
                g.SnowRevision = revision;
            }
            for (int i = 0; i < g.LiveCount; i++)
            {
                int slot = st.Table.SlotFor(g.LiveKind[i], g.LiveSnow[i]);
                float fade = g.LiveFade[i];
                float standing = Puddles ? st.Table.Water(slot, g.LiveBucket[i]) * fade : 0f;
                st.Px[g.LiveQuadrant[i]][g.LiveTexel[i]] = RealisticRainRules.Encode(standing, st.Table.Wet(slot) * fade, true, 1f);
            }
            if (inside)
            {
                Color32[] ne = st.Px[RealisticRainRules.NorthEast];
                for (int j = 0; j < g.EdgeTexel.Length; j++) ne[g.EdgeTexel[j]] = pixels[g.EdgeMap[j]];
            }
            long filled = Stopwatch.GetTimestamp();
            for (int q = 0; q < 4; q++)
            {
                st.Tex[q].SetPixels32(st.Px[q]);
                st.Tex[q].Apply(false);
            }
            long done = Stopwatch.GetTimestamp();
            st.RefreshMs = (filled - started) * 1000.0 / Stopwatch.Frequency;
            st.UploadMs = (done - filled) * 1000.0 / Stopwatch.Frequency;
            st.LongestMs = Math.Max(st.LongestMs, st.RefreshMs + st.UploadMs);
            st.Refreshes++;
            st.Dirty = false;
        }

        private static void ReadSnow(Map map, RainGrid g, RainMap st)
        {
            int li = 0;
            for (int z = -g.South; z < g.North && li < g.LiveCount; z++)
            {
                int first = (z + g.South) * g.GW;
                if (g.LiveCell[li] >= first + g.GW) continue;
                if (!HorizonWorld.Copy(map, "surface.snow", new CellRect(-g.West, z, g.GW, 1), st.Row)) return;
                for (; li < g.LiveCount && g.LiveCell[li] < first + g.GW; li++)
                    g.LiveSnow[li] = (byte)RealisticRainRules.SnowLevel(st.Row[g.LiveCell[li] - first]);
            }
        }

        private static void Changed(Map map, string id, CellRect area)
        {
            if (id == Step) grids.Shown(map);
            else if (id == "disposed")
            {
                grids.Disposed(map);
                if (maps.TryGetValue(map, out RainMap st)) st.Free();
                maps.Remove(map);
            }
        }

        private static string N(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        internal static string Summary(Map map)
        {
            RainGrid g = map == null ? null : grids.Current(map);
            RainMap st = null;
            if (map != null) maps.TryGetValue(map, out st);
            bool active = activeField != null && activeField.GetValue(null) as Map == map;
            string head = $"on={On} puddles={Puddles} hidden={Hidden} rrDraws={active} state={HorizonExtensions.StateOf(Owner)}";
            if (g == null) return head + $" grid=none detail=\"{Detail}\"";
            CellRect view = HorizonView.ViewOf(map);
            int inView = 0;
            foreach (CellRect strip in g.Strips)
                if (strip.Overlaps(view)) inView++;
            int texels = 0;
            for (int q = 0; q < 4; q++)
            {
                RealisticRainRules.TextureSize(q, g.West, g.South, g.East, g.North, out int w, out int h);
                texels += w * h;
            }
            return head + $" band={g.Band} end={g.End} box=W{g.West}/S{g.South}/E{g.East}/N{g.North} strips={g.Strips.Count} inView={inView}"
                + $" straddling={g.Straddling} texels={texels} live={g.LiveCount} water={g.WaterCount} edge={g.EdgeTexel.Length}"
                + $" refreshes={st?.Refreshes ?? 0} refreshMs={N(st?.RefreshMs ?? 0)} uploadMs={N(st?.UploadMs ?? 0)} longestMs={N(st?.LongestMs ?? 0)}"
                + $" samples={st?.Table.Samples ?? 0} kinds={kindFactors.Count} failed={st?.Failed ?? false} detail=\"{Detail}\"";
        }

        internal static MapComponent ComponentOfMap(Map map) => map == null || compType == null ? null : ComponentOf(map);

        internal static void MarkDirty(Map map)
        {
            if (map != null && maps.TryGetValue(map, out RainMap st)) st.Dirty = true;
        }
    }
}
