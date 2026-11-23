#if EH_DEV
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EndlessHorizon.Api;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace EndlessHorizonSamples
{
    [StaticConstructorOnStartup]
    internal static class SampleMods
    {
        internal const string Marker = "EndlessHorizonSamples.txt";
        internal const string LogPrefix = "EH_SAMPLE ";
        internal static readonly bool On;

        static SampleMods()
        {
            string marker = Path.Combine(GenFilePaths.SaveDataFolderPath, Marker);
            if (!File.Exists(marker)) return;
            On = true;
            string wanted = File.ReadAllText(marker);
            if (wanted.Contains("A")) ReefsSample.Register();
            if (wanted.Contains("B")) DataTextureSample.Register();
            if (wanted.Contains("X")) BrokenSample.Register();
            Log.Message(LogPrefix + "registered " + wanted.Trim());
        }
    }

    internal sealed class SampleMesh
    {
        private readonly List<Vector3> verts = new List<Vector3>();
        private readonly List<int> tris = new List<int>();

        internal int Count => verts.Count / 4;

        internal void Cell(int x, int z, float y)
        {
            int n = verts.Count;
            verts.Add(new Vector3(x, y, z)); verts.Add(new Vector3(x, y, z + 1));
            verts.Add(new Vector3(x + 1, y, z + 1)); verts.Add(new Vector3(x + 1, y, z));
            tris.Add(n); tris.Add(n + 1); tris.Add(n + 2);
            tris.Add(n); tris.Add(n + 2); tris.Add(n + 3);
        }

        internal Mesh Make()
        {
            var mesh = new Mesh();
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    internal static class ReefsSample
    {
        private const string Owner = "eh.sample.reefs", Step = Owner + ".mesh";

        private sealed class Reefs
        {
            internal Mesh Mesh;
            internal int Inside, Outside;
            internal float SeamStep;
        }

        private static readonly Dictionary<Map, Reefs> pending = new Dictionary<Map, Reefs>(), current = new Dictionary<Map, Reefs>();
        private static MethodInfo fieldFor, corner;
        private static Material material;

        internal static void Register()
        {
            if (!HorizonInfo.Has("waves.shore")) return;
            Type field = AccessTools.TypeByName("RimWorldWaves.ShoreField");
            fieldFor = AccessTools.Method(field, "For", new[] { typeof(Map) });
            corner = AccessTools.Method(field, "Corner", new[] { typeof(int), typeof(int) });
            HorizonExtensions.RegisterBuildStep(Owner, Step, Build, new[] { "waves.field" });
            HorizonExtensions.RegisterDraw(Owner, "beforeLighting", Draw);
            HorizonInfo.Observe(Owner, Changed);
        }

        private static IEnumerable<bool> Build(Map map)
        {
            CellRect bounds = HorizonSpace.Bounds(map);
            var sea = new float[bounds.Area];
            HorizonWorld.Copy(map, "water.kind", bounds, sea);
            yield return false;
            object waves = fieldFor.Invoke(null, new object[] { map });
            var made = new Reefs();
            var mesh = new SampleMesh();
            float y = AltitudeLayer.MoteOverhead.AltitudeFor();
            var at = new object[2];
            for (int z = bounds.minZ; z <= bounds.maxZ; z++)
            {
                using (HorizonExtensions.Enter(map, "waves.shore"))
                {
                    float Corner(int x, int cz)
                    {
                        at[0] = x; at[1] = cz;
                        return (float)corner.Invoke(waves, at);
                    }
                    for (int x = bounds.minX; x <= bounds.maxX; x++)
                    {
                        if (sea[(z - bounds.minZ) * bounds.Width + (x - bounds.minX)] != 3f) continue;
                        float shore = Corner(x, z);
                        var c = new IntVec3(x, 0, z);
                        bool inside = HorizonSpace.IsPlayable(map, c);
                        if (inside != HorizonSpace.IsPlayable(map, new IntVec3(x + 1, 0, z))) made.SeamStep = Math.Max(made.SeamStep, Math.Abs(Corner(x + 1, z) - shore));
                        if (shore < 2f || shore > 5f || !inside && !HorizonSpace.IsOutside(map, c)) continue;
                        mesh.Cell(x, z, y);
                        if (inside) made.Inside++;
                        else made.Outside++;
                    }
                }
                if (z % 4 == 0) yield return false;
            }
            made.Mesh = mesh.Make();
            pending[map] = made;
        }

        private static void Changed(Map map, string id, CellRect area)
        {
            if (id == Step && pending.TryGetValue(map, out Reefs next))
            {
                if (current.TryGetValue(map, out Reefs old)) UnityEngine.Object.Destroy(old.Mesh);
                current[map] = next;
                pending.Remove(map);
                HorizonInfo.Report(Owner, "map" + map.uniqueID, $"inside={next.Inside} outside={next.Outside} seamStep={next.SeamStep:0.###}");
                Log.Message($"{SampleMods.LogPrefix}A map={map.uniqueID} inside={next.Inside} outside={next.Outside} seamStep={next.SeamStep:0.###}");
            }
            else if (id == "disposed")
            {
                if (current.TryGetValue(map, out Reefs old)) UnityEngine.Object.Destroy(old.Mesh);
                current.Remove(map);
                pending.Remove(map);
            }
        }

        private static void Draw(Map map, CellRect view)
        {
            if (!current.TryGetValue(map, out Reefs reefs)) return;
            material ??= SolidColorMaterials.NewSolidColorMaterial(new Color(1f, 0.3f, 0.55f, 0.5f), ShaderDatabase.Transparent);
            Graphics.DrawMesh(reefs.Mesh, Matrix4x4.identity, material, 0);
        }
    }

    internal static class DataTextureSample
    {
        private const string Owner = "eh.sample.water", Step = Owner + ".texture";
        private static readonly int Global = Shader.PropertyToID("_EhSampleWaterKind");

        private sealed class Field
        {
            internal CellRect Bounds;
            internal Texture2D Texture;
            internal Material Material;
            internal Mesh Mesh;

            internal void Free()
            {
                UnityEngine.Object.Destroy(Mesh);
                UnityEngine.Object.Destroy(Material);
                UnityEngine.Object.Destroy(Texture);
            }
        }

        private static readonly Dictionary<Map, Field> pending = new Dictionary<Map, Field>(), current = new Dictionary<Map, Field>();
        private static readonly Stack<Texture> saved = new Stack<Texture>();
        private static Texture boundNow;
        private static int bound, restored, wrongMap;

        internal static void Register()
        {
            HorizonExtensions.RegisterBuildStep(Owner, Step, Build, new[] { "terrain.base", "surface.frozen" });
            HorizonExtensions.RegisterDraw(Owner, "before", Bind);
            HorizonExtensions.RegisterDraw(Owner, "afterTerrain", Draw);
            HorizonExtensions.RegisterDraw(Owner, "after", Restore);
            bool replaced = HorizonExtensions.Replace(Owner, "livingwaters");
            HorizonInfo.Observe(Owner, Changed);
            Log.Message($"{SampleMods.LogPrefix}B replace={replaced} state={HorizonExtensions.StateOf("livingwaters")} data={HorizonInfo.Has("livingwaters.kind")}");
        }

        private static IEnumerable<bool> Build(Map map)
        {
            CellRect bounds = HorizonSpace.Bounds(map);
            var row = new float[bounds.Width];
            var pixels = new Color32[bounds.Area];
            int own = 0;
            for (int z = 0; z < bounds.Height; z++)
            {
                HorizonWorld.Copy(map, "water.kind", new CellRect(bounds.minX, bounds.minZ + z, bounds.Width, 1), row);
                for (int x = 0; x < row.Length; x++)
                {
                    float kind = row[x];
                    pixels[z * row.Length + x] = kind == 1f ? new Color32(40, 220, 255, 120) : kind == 2f ? new Color32(255, 235, 40, 120)
                        : kind == 3f ? new Color32(70, 90, 255, 90) : kind == 4f ? new Color32(120, 255, 90, 120) : new Color32(0, 0, 0, 0);
                }
                own += CountKnown(map, bounds, z);
                if (z % 4 == 0) yield return false;
            }
            var made = new Field { Bounds = bounds, Texture = new Texture2D(bounds.Width, bounds.Height, TextureFormat.RGBA32, false) };
            made.Texture.filterMode = FilterMode.Point;
            made.Texture.wrapMode = TextureWrapMode.Clamp;
            made.Texture.SetPixels32(0, 0, bounds.Width, bounds.Height, pixels);
            made.Texture.Apply(false);
            made.Material = new Material(ShaderDatabase.Transparent) { mainTexture = made.Texture };
            float x0 = bounds.minX, z0 = bounds.minZ, x1 = bounds.maxX + 1, z1 = bounds.maxZ + 1, y = AltitudeLayer.TerrainScatter.AltitudeFor();
            made.Mesh = new Mesh();
            made.Mesh.SetVertices(new List<Vector3> { new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0) });
            made.Mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            made.Mesh.SetTriangles(new List<int> { 0, 1, 2, 0, 2, 3 }, 0);
            made.Mesh.RecalculateBounds();
            pending[map] = made;
            Log.Message($"{SampleMods.LogPrefix}B built map={map.uniqueID} origin={bounds.minX},{bounds.minZ} size={bounds.Width}x{bounds.Height} livingwatersCells={own}");
        }

        private static int CountKnown(Map map, CellRect bounds, int z)
        {
            int count = 0;
            for (int x = bounds.minX; x <= bounds.maxX; x += 5)
                if (HorizonWorld.Value(map, "livingwaters.kind", new IntVec3(x, 0, bounds.minZ + z)) > 0f) count++;
            return count;
        }

        private static void Changed(Map map, string id, CellRect area)
        {
            if (id == Step && pending.TryGetValue(map, out Field next))
            {
                if (current.TryGetValue(map, out Field old)) old.Free();
                current[map] = next;
                pending.Remove(map);
            }
            else if (id == "disposed")
            {
                if (current.TryGetValue(map, out Field old)) old.Free();
                current.Remove(map);
                pending.Remove(map);
            }
        }

        private static void Bind(Map map, CellRect view)
        {
            saved.Push(boundNow);
            boundNow = current.TryGetValue(map, out Field field) ? field.Texture : null;
            Shader.SetGlobalTexture(Global, boundNow);
            bound++;
        }

        private static void Draw(Map map, CellRect view)
        {
            if (!current.TryGetValue(map, out Field field)) return;
            if (HorizonView.DrawingMap != map || boundNow != field.Texture) wrongMap++;
            Graphics.DrawMesh(field.Mesh, Matrix4x4.identity, field.Material, 0);
        }

        private static void Restore(Map map, CellRect view)
        {
            if (saved.Count > 0)
            {
                boundNow = saved.Pop();
                Shader.SetGlobalTexture(Global, boundNow);
            }
            restored++;
            if (restored % 60 == 1) HorizonInfo.Report(Owner, "globals", $"bound={bound} restored={restored} wrongMap={wrongMap} open={saved.Count}");
        }
    }

    internal static class BrokenSample
    {
        private const string Owner = "eh.sample.broken";

        internal static void Register()
        {
            HorizonExtensions.RegisterBuildStep(Owner, Owner + ".step", Build, new string[0]);
            HorizonExtensions.RegisterChannel(Owner, Owner + ".value", (map, c) => throw new InvalidOperationException("sample channel failure"));
            HorizonExtensions.RegisterDraw(Owner, "afterTerrain", (map, view) =>
            {
                HorizonWorld.Value(map, Owner + ".value", new IntVec3(-1, 0, 0));
                throw new InvalidOperationException("sample draw failure");
            });
        }

        private static IEnumerable<bool> Build(Map map)
        {
            yield return false;
            throw new InvalidOperationException("sample step failure");
        }
    }

    internal static class HexagonSample
    {
        private const string Owner = "eh.sample.hexagon";

        private static Map first, second;
        private static bool external;
        private static Harmony harmony;
        private static int draws, failedDraws;

        [DebugAction("Endless Horizon", "Sample C: hexagon region", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SetHexagon()
        {
            if (!SampleMods.On) return;
            Map map = Find.CurrentMap;
            Report(map, HorizonSpace.SetRegion(map, Owner, Hexagon(map), null), "hexagon");
        }

        [DebugAction("Endless Horizon", "Sample C: clear region", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Clear()
        {
            if (!SampleMods.On) return;
            Map map = Find.CurrentMap;
            Log.Message($"{SampleMods.LogPrefix}C cleared={HorizonSpace.ClearRegion(map, Owner)}");
        }

        [DebugAction("Endless Horizon", "Sample C: two maps, external drawing", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void TwoMaps()
        {
            if (!SampleMods.On) return;
            first = Find.CurrentMap;
            second = Find.Maps.FirstOrDefault(m => m != first && HorizonInfo.IsActive(m)) ?? Generate(first);
            if (second == null)
            {
                Log.Message(SampleMods.LogPrefix + "C no second map");
                return;
            }
            CellRect bounds = HorizonSpace.Bounds(first);
            int ring = -bounds.minX, w = bounds.Width - 2 * ring, h = bounds.Height - 2 * ring;
            float middle = w / 2f, far = 3000f;
            var left = new List<Vector2> { new Vector2(-far, -far), new Vector2(middle, -far), new Vector2(middle, h + far), new Vector2(-far, h + far) };
            var right = new List<Vector2> { new Vector2(middle, -far), new Vector2(w + far, -far), new Vector2(w + far, h + far), new Vector2(middle, h + far) };
            float size = w / 4f;
            Report(first, HorizonSpace.SetRegion(first, Owner, c => InHexagon(c, middle - size, h / 2f, size), left), "left map");
            Report(second, HorizonSpace.SetRegion(second, Owner, c => InHexagon(c, middle + size, h / 2f, size), right), "right map");
            int both = 0;
            foreach (IntVec3 c in bounds)
            {
                bool a = HorizonSpace.IsOutside(first, c), b = HorizonSpace.IsOutside(second, c);
                if (a && b) both++;
            }
            Log.Message($"{SampleMods.LogPrefix}C before the rebuild: drawnByBoth={both}");
            HorizonView.SetExternalDrawing(first, Owner, true);
            HorizonView.SetExternalDrawing(second, Owner, true);
            external = true;
            if (harmony != null) return;
            harmony = new Harmony("jongsh.endlesshorizon.samples");
            harmony.Patch(AccessTools.Method(typeof(MapEdgeClipDrawer), nameof(MapEdgeClipDrawer.DrawClippers)),
                prefix: new HarmonyMethod(typeof(HexagonSample), nameof(OwnClippers)) { priority = Priority.First });
        }

        [DebugAction("Endless Horizon", "Sample C: report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ReportNow()
        {
            if (!SampleMods.On) return;
            foreach (Map map in Find.Maps) Report(map, true, "state");
            if (!external) return;
            CellRect bounds = HorizonSpace.Bounds(first);
            int both = 0, gaps = 0;
            foreach (IntVec3 c in bounds)
            {
                bool a = HorizonSpace.IsOutside(first, c) || HorizonSpace.IsPlayable(first, c);
                bool b = HorizonSpace.IsOutside(second, c) || HorizonSpace.IsPlayable(second, c);
                if (a && b) both++;
                else if (!a && !b && HorizonSpace.Bounds(second).Contains(c)) gaps++;
            }
            Log.Message($"{SampleMods.LogPrefix}C two maps: overlap={both} gaps={gaps} draws={draws} failedDraws={failedDraws} drawing={(HorizonView.DrawingMap == null ? "none" : "set")}");
        }

        private static bool OwnClippers(Map map)
        {
            if (!external || map != first) return true;
            CellRect view = Find.CameraDriver.CurrentViewRect;
            if (HorizonView.Draw(first, view)) draws++;
            else failedDraws++;
            if (HorizonView.Draw(second, view)) draws++;
            else failedDraws++;
            return false;
        }

        private static Predicate<IntVec3> Hexagon(Map map)
        {
            CellRect bounds = HorizonSpace.Bounds(map);
            int ring = -bounds.minX;
            float w = bounds.Width - 2 * ring, h = bounds.Height - 2 * ring;
            return c => InHexagon(c, w / 2f, h / 2f, Mathf.Min(w, h) * 0.44f);
        }

        private static bool InHexagon(IntVec3 c, float cx, float cz, float size)
        {
            float x = Mathf.Abs(c.x + 0.5f - cx), z = Mathf.Abs(c.z + 0.5f - cz);
            return x <= size && x * 0.5f + z * 0.8660254f <= size;
        }

        private static Map Generate(Map beside)
        {
            var near = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(beside.Tile, near);
            foreach (PlanetTile tile in near)
            {
                if (Find.World.Impassable(tile) || Find.WorldObjects.MapParentAt(tile) != null) continue;
                CellRect bounds = HorizonSpace.Bounds(beside);
                int ring = -bounds.minX;
                return GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(bounds.Width - 2 * ring, 1, bounds.Height - 2 * ring), WorldObjectDefOf.Camp);
            }
            return null;
        }

        private static void Report(Map map, bool ok, string what)
        {
            IReadOnlyList<IntVec3> edge = HorizonSpace.BoundaryCells(map);
            CellRect view = HorizonView.ViewOf(map);
            int roads = 0;
            foreach (IntVec3 c in edge)
                if (HorizonWorld.TerrainAt(map, c)?.HasTag("Road") == true) roads++;
            Log.Message($"{SampleMods.LogPrefix}C {what} ok={ok} map={map.uniqueID} current={map == Find.CurrentMap} built={HorizonInfo.IsBuilt(map)} "
                + $"revision={HorizonInfo.Revision(map, "terrain.base")} boundary={edge.Count} rivers={HorizonWorld.RiverCount(map)} roadOnBoundary={roads} "
                + $"extent={HorizonSpace.DrawExtent(map)} view={view.minX},{view.minZ},{view.Width}x{view.Height}");
        }
    }
}
#endif
