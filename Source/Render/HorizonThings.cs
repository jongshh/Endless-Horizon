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
    internal static class HorizonThings
    {
        private static readonly FieldInfo MapField = AccessTools.Field(typeof(MapDrawLayer), "map");
        public static SectionLayer MakeScratchLayer(Map map)
        {
            var layer = (SectionLayer)RuntimeHelpers.GetUninitializedObject(typeof(SectionLayer_ThingsGeneral));
            MapField.SetValue(layer, map);
            layer.subMeshes = new List<LayerSubMesh>();
            return layer;
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

        private static void AddWeatherMask(HorizonSurface surface, CellRect rect, SectionLayer layer)
        {
            LayerSubMesh indoor = null, outdoor = null;
            foreach (IntVec3 c in rect)
            {
                if (!surface.Domain.Drawn(c))
                {
                    continue;
                }
                WeatherCover cover = surface.Cover(c, out float overage);
                if (cover == WeatherCover.Open) continue;
                LayerSubMesh mask = cover == WeatherCover.Indoors
                    ? (indoor ??= layer.GetSubMesh(MatBases.IndoorMask))
                    : (outdoor ??= layer.GetSubMesh(MatBases.RoofedOutdoorMask));
                SectionLayer_IndoorMask.AppendQuadToMesh(mask, c.x, c.z, overage);
            }
        }

        private static float SmoothStep(float a, float b, float v)
        {
            float t = Mathf.Clamp01((v - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        public static HorizonThingsChunk Build(HorizonSurface surface, CellRect rect, RockRender rocks, FogTint tint, ref int printed, ref int fogCells)
        {
            using var work = BeginBuild(surface, rect, rocks, tint);
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

        private static bool PrintRoadBuilding(SectionLayer layer, IntVec3 c, ThingDef def)
        {
            Graphic graphic = def.graphic;
            if (graphic == null || graphic is Graphic_Linked) return false;
            if (graphic is Graphic_Random random && random.SubGraphicsCount > 0)
            {
                int pick = (Gen.HashCombineInt(Gen.HashCombineInt(c.x, c.z), def.shortHash) & int.MaxValue) % random.SubGraphicsCount;
                graphic = random.SubGraphicAtIndex(pick);
            }
            Vector3 center = c.ToVector3Shifted();
            center.y = def.Altitude;
            center += graphic.DrawOffset(Rot4.North);
            Printer_Plane.PrintPlane(layer, center, graphic.drawSize, graphic.MatSingle);
            if (def.graphicData?.shadowData != null)
            {
                Vector3 shadowAt = c.ToVector3Shifted();
                shadowAt.y = def.Altitude;
                Printer_Shadow.PrintShadow(layer, shadowAt, def.graphicData.shadowData, Rot4.North);
            }
            return true;
        }

        public static BuildWork BeginBuild(HorizonSurface surface, CellRect rect, RockRender rocks, FogTint tint)
        {
            var work = new BuildWork();
            work.Steps = BuildSteps(surface, rect, rocks, tint, work).GetEnumerator();
            return work;
        }

        private static IEnumerable<bool> BuildSteps(HorizonSurface surface, CellRect rect, RockRender rocks, FogTint tint, BuildWork work)
        {
            Map map = surface.Map;
            HorizonDomain domain = surface.Domain;
            int fogStart = surface.FogStart;
            bool printThings = HorizonDebug.Things && HorizonSettings.Current.things;
            SectionLayer layer = work.Layer = MakeScratchLayer(map);
            var before = new List<int>();
            float maxDist = FogRamp.CoveredFrom(fogStart, surface.Ring, FogRamp.HiddenAlpha);
            bool anyFog = false;
            foreach (IntVec3 c in printThings ? rect.Cells : System.Linq.Enumerable.Empty<IntVec3>())
            {
                yield return false;
                if (!domain.Drawn(c))
                {
                    continue;
                }
                float d = domain.Steps(c);
                if (rocks != null)
                {
                    if (d <= fogStart + 4)
                    {
                        rocks.PrintEdgeShadow(layer, c);
                    }
                    if (surface.OutsideRock(c.x, c.z))
                    {
                        rocks.PrintRock(layer, c);
                        work.Printed++;
                        if (surface.Fogged(c))
                        {
                            anyFog = true;
                        }
                        continue;
                    }
                }
                ThingDef roadBuilding = surface.RoadBuildingAt(c);
                if (roadBuilding != null)
                {
                    bool hidden = surface.Fogged(c);
                    if (hidden) anyFog = true;
                    if (d <= maxDist && !(hidden && !roadBuilding.seeThroughFog) && PrintRoadBuilding(layer, c, roadBuilding)) work.Printed++;
                    continue;
                }
                if (!surface.TryThingSource(c, out IntVec3 src))
                {
                    continue;
                }
                bool fogged = surface.Fogged(c);
                if (fogged)
                {
                    anyFog = true;
                }
                if (d > maxDist)
                {
                    continue;
                }

                List<Thing> things = map.thingGrid.ThingsListAtFast(map.cellIndices.CellToIndex(src));
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t.Position != src || !HorizonSurface.ShownOutside(t))
                    {
                        continue;
                    }
                    if (fogged && !t.def.seeThroughFog)
                    {
                        continue;
                    }
                    if (t is Plant && surface.Drowned(c, t.def))
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
                        if (t.def.drawerType == DrawerType.RealtimeOnly) t.Graphic.Print(layer, t, 0f);
                        else t.Print(layer);
                    }
                    catch (Exception ex)
                    {
                        surface.PrintFailed(t.def);
                        Log.Error($"{HorizonLog.Prefix}Printing {t.def.defName} outside the map failed; skipping this def.\n{ex}");
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
                    if (domain.Inside(c) ? map.fogGrid.IsFogged(c) : domain.InGrid(c) && surface.Fogged(c))
                    {
                        anyFog = true;
                        break;
                    }
                }
            }
            yield return false;
            if (anyFog)
            {
                work.FogCells += AddFog(surface, rect, layer, tint);
            }
            yield return false;
            AddWeatherMask(surface, rect, layer);
            yield return false;
            if (printThings && rocks != null)
            {
                foreach (IntVec3 c in rect)
                {
                    if (domain.OnEdge(c))
                    {
                        rocks.PrintEdgeShadow(layer, c, outsideNeighboursOnly: true);
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

        private static int AddFog(HorizonSurface surface, CellRect rect, SectionLayer layer, FogTint fogTint)
        {
            Map map = surface.Map;
            HorizonDomain domain = surface.Domain;
            bool Fogged(int x, int z)
            {
                var c = new IntVec3(x, 0, z);
                if (domain.Inside(c))
                {
                    return map.fogGrid.IsFogged(c);
                }
                return domain.InGrid(c) && surface.Fogged(c);
            }
            bool MapFogged(int x, int z)
            {
                var c = new IntVec3(x, 0, z);
                return domain.Inside(c) && map.fogGrid.IsFogged(c);
            }
            bool anyOutside = false;
            foreach (IntVec3 c in rect)
            {
                if (domain.MayDraw(c)) { anyOutside = true; break; }
            }
            if (!anyOutside)
            {
                return 0;
            }
            LayerSubMesh sm = layer.GetSubMesh(MatBases.FogOfWar);
            float y = AltitudeLayer.FogOfWar.AltitudeFor();
            float fadeFrom = surface.FogStart - 8f, fadeTo = surface.Ring - 8f;
            int w = rect.Width, h = rect.Height;
            int baseIndex = sm.verts.Count;
            for (int z = 0; z <= h; z++)
            {
                for (int x = 0; x <= w; x++)
                {
                    int cx = rect.minX + x, cz = rect.minZ + z;
                    bool touchesMap = domain.Inside(cx, cz) || domain.Inside(cx - 1, cz)
                                      || domain.Inside(cx, cz - 1) || domain.Inside(cx - 1, cz - 1);
                    bool covered;
                    if (touchesMap)
                    {
                        covered = MapFogged(cx, cz) || MapFogged(cx - 1, cz) || MapFogged(cx, cz - 1) || MapFogged(cx - 1, cz - 1);
                    }
                    else
                    {
                        covered = Fogged(cx, cz) || Fogged(cx - 1, cz) || Fogged(cx, cz - 1) || Fogged(cx - 1, cz - 1);
                    }
                    float fade = 1f - SmoothStep(fadeFrom, fadeTo, domain.DistanceFromPoint(cx, cz));
                    sm.verts.Add(new Vector3(cx, y, cz));
                    Color32 tint = fogTint?.At(cx, cz) ?? new Color32(255, 255, 255, 255);
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
                    if (!domain.MayDraw(c))
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

    internal sealed class HorizonThingsChunk : IDisposable
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
