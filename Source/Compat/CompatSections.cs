using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class CompatSections
    {
        private static readonly FieldInfo SectionMap = AccessTools.Field(typeof(Section), "map");
        private static readonly FieldInfo SectionRect = AccessTools.Field(typeof(Section), "calculatedRect");
        private static readonly FieldInfo SectionLayers = AccessTools.Field(typeof(Section), "layers");
        private static readonly FieldInfo SectionDynamic = AccessTools.Field(typeof(Section), "dynamic");

        public static bool Ready => SectionMap != null && SectionRect != null && SectionLayers != null;

        public static Section StandIn(Map map, CellRect rect)
        {
            var s = (Section)RuntimeHelpers.GetUninitializedObject(typeof(Section));
            SectionMap.SetValue(s, map);
            s.botLeft = new IntVec3(rect.minX, 0, rect.minZ);
            SectionRect.SetValue(s, rect);
            SectionLayers.SetValue(s, new List<SectionLayer>());
            SectionDynamic?.SetValue(s, Activator.CreateInstance(SectionDynamic.FieldType));
            return s;
        }

        public static List<CellRect> OutsideStrips(Map map, int ringWidth)
        {
            int size = Section.Size, w = map.Size.x, h = map.Size.z;
            int min = (int)Math.Floor(-ringWidth / (double)size) * size;
            var strips = new List<CellRect>();
            for (int x = min; x < w + ringWidth; x += size)
            {
                for (int z = min; z < h + ringWidth; z += size)
                {
                    int x0 = x, z0 = z, x1 = x + size - 1, z1 = z + size - 1;
                    if (x0 >= 0 && z0 >= 0 && x1 < w && z1 < h) continue;
                    void Add(int a0, int b0, int a1, int b1)
                    {
                        if (a1 >= a0 && b1 >= b0) strips.Add(CellRect.FromLimits(a0, b0, a1, b1));
                    }
                    Add(x0, z0, Math.Min(x1, -1), z1);
                    Add(Math.Max(x0, w), z0, x1, z1);
                    Add(Math.Max(x0, 0), z0, Math.Min(x1, w - 1), Math.Min(z1, -1));
                    Add(Math.Max(x0, 0), Math.Max(z0, h), Math.Min(x1, w - 1), z1);
                }
            }
            return strips;
        }

        public static int DrawLayers(List<(CellRect rect, SectionLayer layer)> layers, CellRect view)
        {
            int calls = 0;
            foreach ((CellRect rect, SectionLayer layer) in layers)
            {
                if (!rect.Overlaps(view) || !layer.Visible) continue;
                List<LayerSubMesh> subs = layer.subMeshes;
                for (int i = 0; i < subs.Count; i++)
                {
                    LayerSubMesh sm = subs[i];
                    if (sm.finalized && !sm.disabled && sm.mesh != null && sm.mesh.vertexCount > 0)
                    {
                        Graphics.DrawMesh(sm.mesh, Matrix4x4.identity, sm.material, sm.renderLayer);
                        calls++;
                    }
                }
            }
            return calls;
        }

        public static void Destroy(List<(CellRect rect, SectionLayer layer)> layers)
        {
            foreach ((CellRect _, SectionLayer layer) in layers)
            {
                foreach (LayerSubMesh sm in layer.subMeshes)
                {
                    if (sm.mesh != null) UnityEngine.Object.Destroy(sm.mesh);
                }
            }
            layers.Clear();
        }

        public static float[] Distance(int w, int h, Func<int, bool> source, float cap)
        {
            var d = new float[w * h];
            for (int i = 0; i < d.Length; i++) d[i] = source(i) ? 0f : cap;
            const float D1 = 1f, D2 = 1.4142f;
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    float v = d[i];
                    if (x > 0) v = Mathf.Min(v, d[i - 1] + D1);
                    if (z > 0)
                    {
                        v = Mathf.Min(v, d[i - w] + D1);
                        if (x > 0) v = Mathf.Min(v, d[i - w - 1] + D2);
                        if (x < w - 1) v = Mathf.Min(v, d[i - w + 1] + D2);
                    }
                    d[i] = v;
                }
            }
            for (int z = h - 1; z >= 0; z--)
            {
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = z * w + x;
                    float v = d[i];
                    if (x < w - 1) v = Mathf.Min(v, d[i + 1] + D1);
                    if (z < h - 1)
                    {
                        v = Mathf.Min(v, d[i + w] + D1);
                        if (x < w - 1) v = Mathf.Min(v, d[i + w + 1] + D2);
                        if (x > 0) v = Mathf.Min(v, d[i + w - 1] + D2);
                    }
                    d[i] = v;
                }
            }
            return d;
        }
    }
}
