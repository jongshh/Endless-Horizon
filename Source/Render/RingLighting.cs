using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class RingLighting
    {
        public static Mesh BuildChunk(HorizonSurface surface, CellRect rect)
        {
            Map map = surface.Map;
            HorizonDomain domain = surface.Domain;
            float fadeFrom = surface.FogStart - 8f, fadeTo = surface.Ring - 8f;
            void Cell(int x, int z, out Color32 glow, out bool blocks, out bool roofed)
            {
                glow = new Color32(0, 0, 0, 0);
                blocks = false;
                roofed = false;
                if (domain.Inside(x, z))
                {
                    int index = map.cellIndices.CellToIndex(x, z);
                    Building b = map.edificeGrid.InnerArray[index];
                    blocks = b != null && b.def.blockLight;
                    RoofDef roof = map.roofGrid.RoofAt(index);
                    roofed = roof != null && (roof.isThickRoof || b == null || !b.def.holdsRoof);
                    glow = map.glowGrid.VisualGlowAt(index);
                    return;
                }
                if (!domain.InGrid(x, z))
                {
                    return;
                }
                roofed = surface.RoofShades(new IntVec3(x, 0, z));
                blocks = surface.OutsideRock(x, z);
                if (!blocks) glow = surface.Glow(x, z);
            }

            var verts = new List<Vector3>();
            var colors = new List<Color32>();
            var tris = new List<int>();
            float y = AltitudeLayer.LightingOverlay.AltitudeFor();
            int cw = rect.Width + 1;
            for (int z = rect.minZ; z <= rect.maxZ + 1; z++)
            {
                for (int x = rect.minX; x <= rect.maxX + 1; x++)
                {
                    var sum = new ColorInt(0, 0, 0, 0);
                    int n = 0;
                    bool anyRoof = false;
                    for (int k = 0; k < 4; k++)
                    {
                        Cell(x - 1 + (k & 1), z - 1 + (k >> 1), out Color32 g, out bool blocks, out bool roofed);
                        anyRoof |= roofed;
                        if (!blocks)
                        {
                            sum += g;
                            n++;
                        }
                    }
                    Color32 col = n > 0 ? (sum / n).ProjectToColor32() : new Color32(0, 0, 0, 0);
                    if (anyRoof && col.a < 100)
                    {
                        col.a = 100;
                    }
                    float t = Mathf.Clamp01((domain.DistanceFromPoint(x, z) - fadeFrom) / (fadeTo - fadeFrom));
                    float fade = 1f - t * t * (3f - 2f * t);
                    col = new Color32((byte)(col.r * fade), (byte)(col.g * fade), (byte)(col.b * fade), (byte)(col.a * fade));
                    verts.Add(new Vector3(x, y, z));
                    colors.Add(col);
                }
            }
            for (int z = 0; z < rect.Height; z++)
            {
                for (int x = 0; x < rect.Width; x++)
                {
                    var cell = new IntVec3(rect.minX + x, 0, rect.minZ + z);
                    if (!domain.Drawn(cell))
                    {
                        continue;
                    }
                    int i0 = z * cw + x, i1 = i0 + cw;
                    tris.Add(i0); tris.Add(i1); tris.Add(i1 + 1);
                    tris.Add(i0); tris.Add(i1 + 1); tris.Add(i0 + 1);
                }
            }
            if (tris.Count == 0)
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
