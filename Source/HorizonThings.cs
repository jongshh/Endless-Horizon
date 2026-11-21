using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class HorizonThings
    {
        private static readonly FieldInfo MapField = AccessTools.Field(typeof(MapDrawLayer), "map");
        private static readonly HashSet<ThingDef> failedDefs = new HashSet<ThingDef>();

        public static bool Enabled = true;

        public const float HiddenAlpha = 0.97f;

        public static SectionLayer MakeScratchLayer(Map map)
        {
            var layer = (SectionLayer)RuntimeHelpers.GetUninitializedObject(typeof(SectionLayer_ThingsGeneral));
            MapField.SetValue(layer, map);
            layer.subMeshes = new List<LayerSubMesh>();
            return layer;
        }

        public static bool IsShownOutside(Thing t) => Eligible(t);

        private static bool Eligible(Thing t)
        {
            ThingDef def = t.def;
            bool meshDrawn = def.drawerType == DrawerType.MapMeshOnly || def.drawerType == DrawerType.MapMeshAndRealTime;
            if (!meshDrawn && !(def.category == ThingCategory.Plant && def.drawerType != DrawerType.None))
            {
                return false;
            }
            if (t is Plant sownPlant && sownPlant.sown)
            {
                return false;
            }
            if (def.size.x != 1 || def.size.z != 1 || failedDefs.Contains(def))
            {
                return false;
            }
            if (def.category == ThingCategory.Plant)
            {
                return true;
            }
            if (def.mineable)
            {
                return false;
            }
            return def.thingCategories != null && (def.thingCategories.Contains(ThingCategoryDefOf.Chunks) || def.thingCategories.Contains(ThingCategoryDefOf.StoneChunks));
        }

        public static bool OutsideFogged(Map map, HorizonField field, IntVec3 c)
        {
            if (!field.OutsideFogAt(c))
            {
                return false;
            }
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = c + GenAdj.AdjacentCells[i];
                if (n.InBounds(map) && !map.fogGrid.IsFogged(n))
                {
                    Building b = n.GetEdifice(map);
                    if (b == null || !b.def.mineable)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static void Truncate(LayerSubMesh sm, int vertCount)
        {
            void Cut<T>(List<T> list)
            {
                if (list.Count > vertCount) list.RemoveRange(vertCount, list.Count - vertCount);
            }
            Cut(sm.verts);
            Cut(sm.colors);
            Cut(sm.uvs);
            Cut(sm.uvsChannelTwo);
            Cut(sm.normals);
            Cut(sm.pollution);
            int t = sm.tris.Count;
            while (t >= 3 && (sm.tris[t - 1] >= vertCount || sm.tris[t - 2] >= vertCount || sm.tris[t - 3] >= vertCount))
            {
                t -= 3;
            }
            sm.tris.RemoveRange(t, sm.tris.Count - t);
        }

        private static void AddWeatherMask(Map map, CellRect rect, HorizonField field, MountainMask mountains, SectionLayer layer)
        {
            LayerSubMesh indoor = null, outdoor = null;
            foreach (IntVec3 c in rect)
            {
                if (c.InBounds(map) || !field.Covers(c))
                {
                    continue;
                }
                if (mountains != null && mountains.IsRock(c.x, c.z))
                {
                    indoor ??= layer.GetSubMesh(MatBases.IndoorMask);
                    SectionLayer_IndoorMask.AppendQuadToMesh(indoor, c.x, c.z, 0f);
                    continue;
                }
                int s = field.RawSampleAt(c);
                if (s < 0)
                {
                    continue;
                }
                IntVec3 src = map.cellIndices.IndexToCell(s);
                Building edifice = map.edificeGrid.InnerArray[s];
                bool hide = OutsideFogged(map, field, c);
                RoofDef srcRoof = map.roofGrid.RoofAt(s);
                if (!hide && srcRoof != null && srcRoof.isNatural)
                {
                    hide = edifice == null || edifice.def.Fillage != FillCategory.Full || edifice.def.size.x > 1 || edifice.def.size.z > 1
                        || edifice.def.holdsRoof || edifice.def.blockWeather;
                }
                if (!hide)
                {
                    continue;
                }
                Room room = src.GetRoom(map);
                RoofDef roof = map.roofGrid.RoofAt(s);
                if ((room == null || !room.ProperRoom) && (edifice == null || edifice.def.Fillage != FillCategory.Full) && (roof == null || !roof.isThickRoof))
                {
                    outdoor ??= layer.GetSubMesh(MatBases.RoofedOutdoorMask);
                    SectionLayer_IndoorMask.AppendQuadToMesh(outdoor, c.x, c.z, 0f);
                    continue;
                }
                float overage = edifice == null || (edifice.def.passability != Traversability.Impassable && !edifice.def.IsDoor) ? 0.16f : 0f;
                indoor ??= layer.GetSubMesh(MatBases.IndoorMask);
                SectionLayer_IndoorMask.AppendQuadToMesh(indoor, c.x, c.z, overage);
            }
        }

        private static float SmoothStep(float a, float b, float v)
        {
            float t = Mathf.Clamp01((v - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        public static HorizonThingsChunk Build(Map map, CellRect rect, HorizonField field, int fogStart, MountainMask mountains, ref int printed, ref int fogCells)
        {
            using var work = BeginBuild(map, rect, field, fogStart, mountains);
            while (work.Steps.MoveNext()) { }
            printed += work.Printed;
            fogCells += work.FogCells;
            return work.Result;
        }

        public sealed class BuildWork : IDisposable
        {
            internal SectionLayer Layer;
            public int Printed, FogCells;
            public HorizonThingsChunk Result { get; internal set; }
            public IEnumerator<bool> Steps { get; internal set; }

            public void Dispose()
            {
                Steps?.Dispose();
                if (Layer == null) return;
                foreach (LayerSubMesh sm in Layer.subMeshes)
                    if (sm.mesh != null) UnityEngine.Object.Destroy(sm.mesh);
                Layer.subMeshes.Clear();
                Layer = null;
            }
        }

        public static BuildWork BeginBuild(Map map, CellRect rect, HorizonField field, int fogStart, MountainMask mountains)
        {
            var work = new BuildWork();
            work.Steps = BuildSteps(map, rect, field, fogStart, mountains, work).GetEnumerator();
            return work;
        }

        private static IEnumerable<bool> BuildSteps(Map map, CellRect rect, HorizonField field, int fogStart, MountainMask mountains, BuildWork work)
        {
            bool printThings = Enabled && EndlessHorizonMod.Settings.things;
            SectionLayer layer = work.Layer = MakeScratchLayer(map);
            var before = new List<int>();
            float maxDist = HorizonFog.CoveredFrom(fogStart, field.RingWidth, HiddenAlpha);
            bool anyFog = false;
            foreach (IntVec3 c in printThings ? rect.Cells : System.Linq.Enumerable.Empty<IntVec3>())
            {
                yield return false;
                if (c.InBounds(map) || !field.Covers(c))
                {
                    continue;
                }
                if (mountains != null)
                {
                    float dm = Mathf.Max(Mathf.Max(-c.x, c.x - map.Size.x + 1), Mathf.Max(-c.z, c.z - map.Size.z + 1));
                    if (dm <= fogStart + 4)
                    {
                        mountains.PrintEdgeShadow(layer, c);
                    }
                    if (mountains.IsRock(c.x, c.z))
                    {
                        mountains.PrintRock(layer, c);
                        work.Printed++;
                        int rs = field.RawSampleAt(c);
                        if (OutsideFogged(map, field, c))
                        {
                            anyFog = true;
                        }
                        continue;
                    }
                }
                int sample = field.SampleAt(c);
                if (sample < 0)
                {
                    continue;
                }
                IntVec3 src = map.cellIndices.IndexToCell(sample);
                bool fogged = OutsideFogged(map, field, c);
                if (fogged)
                {
                    anyFog = true;
                }
                float d = Mathf.Max(Mathf.Max(-c.x, c.x - map.Size.x + 1), Mathf.Max(-c.z, c.z - map.Size.z + 1));
                if (d > maxDist)
                {
                    continue;
                }

                List<Thing> things = map.thingGrid.ThingsListAtFast(sample);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t.Position != src || !Eligible(t))
                    {
                        continue;
                    }
                    if (fogged && !t.def.seeThroughFog)
                    {
                        continue;
                    }
                    before.Clear();
                    for (int m = 0; m < layer.subMeshes.Count; m++)
                    {
                        before.Add(layer.subMeshes[m].verts.Count);
                    }
                    try
                    {
                        t.Print(layer);
                    }
                    catch (Exception ex)
                    {
                        failedDefs.Add(t.def);
                        Log.Warning($"{EndlessHorizonMod.LogPrefix}Printing {t.def.defName} outside the map failed; skipping this def.\n{ex}");
                        for (int m = 0; m < layer.subMeshes.Count; m++)
                        {
                            Truncate(layer.subMeshes[m], m < before.Count ? before[m] : 0);
                        }
                        continue;
                    }
                    var delta = new Vector3(c.x - src.x, 0f, c.z - src.z);
                    for (int m = 0; m < layer.subMeshes.Count; m++)
                    {
                        List<Vector3> verts = layer.subMeshes[m].verts;
                        for (int v = m < before.Count ? before[m] : 0; v < verts.Count; v++)
                        {
                            verts[v] += delta;
                        }
                    }
                    work.Printed++;
                }
            }
            if (!anyFog)
            {
                foreach (IntVec3 c in rect.ExpandedBy(1))
                {
                    if (c.InBounds(map) ? map.fogGrid.IsFogged(c) : field.Covers(c) && OutsideFogged(map, field, c))
                    {
                        anyFog = true;
                        break;
                    }
                }
            }
            yield return false;
            if (anyFog)
            {
                work.FogCells += AddFog(map, rect, field, layer, fogStart);
            }
            yield return false;
            AddWeatherMask(map, rect, field, mountains, layer);
            yield return false;
            if (printThings && mountains != null)
            {
                int w = map.Size.x, h = map.Size.z;
                foreach (IntVec3 c in rect)
                {
                    if (c.InBounds(map) && (c.x == 0 || c.z == 0 || c.x == w - 1 || c.z == h - 1))
                    {
                        mountains.PrintEdgeShadow(layer, c, outsideNeighboursOnly: true);
                    }
                }
            }
            layer.subMeshes.RemoveAll(sm => sm.verts.Count == 0 || sm.tris.Count == 0);
            if (layer.subMeshes.Count == 0)
            {
                yield break;
            }
            Bounds bounds = rect.ExpandedBy(4).ToBounds();
            foreach (LayerSubMesh sm in layer.subMeshes)
            {
                sm.FinalizeMesh(MeshParts.All);
                sm.mesh.bounds = bounds;
                yield return false;
            }
            work.Result = new HorizonThingsChunk(rect, layer.subMeshes);
            work.Layer = null;
        }

        private static int AddFog(Map map, CellRect rect, HorizonField field, SectionLayer layer, int fogStart)
        {
            bool Fogged(int x, int z)
            {
                var c = new IntVec3(x, 0, z);
                if (c.InBounds(map))
                {
                    return map.fogGrid.IsFogged(c);
                }
                return field.Covers(c) && OutsideFogged(map, field, c);
            }
            bool MapFogged(int x, int z)
            {
                var c = new IntVec3(x, 0, z);
                return c.InBounds(map) && map.fogGrid.IsFogged(c);
            }
            bool anyOutside = false;
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map)) { anyOutside = true; break; }
            }
            if (!anyOutside)
            {
                return 0;
            }
            LayerSubMesh sm = layer.GetSubMesh(MatBases.FogOfWar);
            float y = AltitudeLayer.FogOfWar.AltitudeFor();
            int ring = field.RingWidth;
            float fadeFrom = fogStart - 8f, fadeTo = ring - 8f;
            int w = rect.Width, h = rect.Height;
            int baseIndex = sm.verts.Count;
            for (int z = 0; z <= h; z++)
            {
                for (int x = 0; x <= w; x++)
                {
                    int cx = rect.minX + x, cz = rect.minZ + z;
                    bool touchesMap = new IntVec3(cx, 0, cz).InBounds(map) || new IntVec3(cx - 1, 0, cz).InBounds(map)
                                      || new IntVec3(cx, 0, cz - 1).InBounds(map) || new IntVec3(cx - 1, 0, cz - 1).InBounds(map);
                    bool covered;
                    if (touchesMap)
                    {
                        covered = MapFogged(cx, cz) || MapFogged(cx - 1, cz) || MapFogged(cx, cz - 1) || MapFogged(cx - 1, cz - 1);
                    }
                    else
                    {
                        covered = Fogged(cx, cz) || Fogged(cx - 1, cz) || Fogged(cx, cz - 1) || Fogged(cx - 1, cz - 1);
                    }
                    float dx = Mathf.Max(0f, Mathf.Max(-cx, cx - map.Size.x));
                    float dz = Mathf.Max(0f, Mathf.Max(-cz, cz - map.Size.z));
                    float fade = 1f - SmoothStep(fadeFrom, fadeTo, Mathf.Sqrt(dx * dx + dz * dz));
                    sm.verts.Add(new Vector3(cx, y, cz));
                    Color32 tint = field.FogTint?.At(cx, cz) ?? new Color32(255, 255, 255, 255);
                    tint.a = covered ? (byte)Mathf.RoundToInt(255f * fade) : (byte)0;
                    sm.colors.Add(tint);
                }
            }
            int count = 0;
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = new IntVec3(rect.minX + x, 0, rect.minZ + z);
                    if (c.InBounds(map))
                    {
                        continue;
                    }
                    int i0 = baseIndex + z * (w + 1) + x;
                    int i1 = i0 + (w + 1);
                    sm.tris.Add(i0); sm.tris.Add(i1); sm.tris.Add(i1 + 1);
                    sm.tris.Add(i0); sm.tris.Add(i1 + 1); sm.tris.Add(i0 + 1);
                    if (Fogged(c.x, c.z)) count++;
                }
            }
            return count;
        }
    }

    public sealed class HorizonThingsChunk : IDisposable
    {
        public readonly CellRect Rect;
        public readonly List<LayerSubMesh> subMeshes;

        public HorizonThingsChunk(CellRect rect, List<LayerSubMesh> subMeshes)
        {
            Rect = rect;
            this.subMeshes = subMeshes;
        }

        public int Draw()
        {
            int calls = 0;
            for (int i = 0; i < subMeshes.Count; i++)
            {
                LayerSubMesh sm = subMeshes[i];
                if (sm.finalized && !sm.disabled)
                {
                    Graphics.DrawMesh(sm.mesh, Matrix4x4.identity, sm.material, sm.renderLayer);
                    calls++;
                }
            }
            return calls;
        }

        public void Dispose()
        {
            foreach (LayerSubMesh sm in subMeshes)
            {
                UnityEngine.Object.Destroy(sm.mesh);
            }
            subMeshes.Clear();
        }
    }
}
