using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class SnowLayer
    {
        private static readonly IntVec3[] Around = GenAdj.AdjacentCellsAndInsideForUV;

        public static Mesh BuildChunk(HorizonSurface surface, CellRect rect)
        {
            var verts = new List<Vector3>();
            var colors = new List<Color32>();
            var tris = new List<int>();
            var adj = new float[9];
            var dirty = new float[9];
            float y = AltitudeLayer.Terrain.AltitudeFor();
            bool any = false;
            foreach (IntVec3 c in rect)
            {
                if (!surface.Domain.Drawn(c))
                {
                    continue;
                }
                for (int k = 0; k < 9; k++)
                {
                    adj[k] = surface.Snow(c.x + Around[k].x, c.z + Around[k].z);
                    IntVec3 n = c + Around[k];
                    dirty[k] = surface.Polluted(n) ? 1f : 0f;
                }
                int b = verts.Count;
                int i = c.x, j = c.z;
                verts.Add(new Vector3(i, y, j));
                verts.Add(new Vector3(i, y, j + 0.5f));
                verts.Add(new Vector3(i, y, j + 1));
                verts.Add(new Vector3(i + 0.5f, y, j + 1));
                verts.Add(new Vector3(i + 1, y, j + 1));
                verts.Add(new Vector3(i + 1, y, j + 0.5f));
                verts.Add(new Vector3(i + 1, y, j));
                verts.Add(new Vector3(i + 0.5f, y, j));
                verts.Add(new Vector3(i + 0.5f, y, j + 0.5f));
                for (int l = 0; l < 9; l++)
                {
                    List<int> weights = SectionLayer_Snow.vertexWeights[l];
                    float sum = 0f, pollution = 0f;
                    for (int m = 0; m < weights.Count; m++)
                    {
                        sum += adj[weights[m]];
                        pollution += dirty[weights[m]];
                    }
                    float a = sum / weights.Count;
                    if (a > 0.01f)
                    {
                        any = true;
                    }
                    colors.Add(new Color32((byte)Mathf.RoundToInt(pollution / weights.Count * 255f), 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255)));
                }
                int[] order = { 7, 0, 1, 1, 2, 3, 3, 4, 5, 5, 6, 7, 7, 1, 8, 1, 3, 8, 3, 5, 8, 5, 7, 8 };
                for (int k = 0; k < order.Length; k++)
                {
                    tris.Add(b + order[k]);
                }
            }
            if (!any)
            {
                return null;
            }
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = rect.ExpandedBy(1).ToBounds();
            return mesh;
        }
    }
}
