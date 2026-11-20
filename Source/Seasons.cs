using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public static class SnowLayer
    {
        private static readonly IntVec3[] Around = GenAdj.AdjacentCellsAndInsideForUV;

        public static float DepthAt(Map map, HorizonField field, MountainMask mountains, int x, int z)
        {
            var c = new IntVec3(x, 0, z);
            if (c.InBounds(map))
            {
                return map.snowGrid.GetDepth(c);
            }
            if (!field.Covers(c))
            {
                c = new IntVec3(Mathf.Clamp(x, -field.RingWidth, map.Size.x + field.RingWidth - 1), 0, Mathf.Clamp(z, -field.RingWidth, map.Size.z + field.RingWidth - 1));
            }
            TerrainDef t = field.TopAt(c);
            if (t == null || t.IsWater || (mountains != null && mountains.IsRock(c.x, c.z)))
            {
                return 0f;
            }
            int s = field.RawSampleAt(c);
            if (field.FrozenAt(c))
            {
                IntVec3 src = s < 0 ? IntVec3.Invalid : map.cellIndices.IndexToCell(s);
                if (src.IsValid && RimWorld.TerrainDefOf.ThinIce != null && map.terrainGrid.TempTerrainAt(src) == RimWorld.TerrainDefOf.ThinIce)
                {
                    return map.snowGrid.GetDepth(src);
                }
                return IceSnow(map);
            }
            return s < 0 ? 0f : map.snowGrid.GetDepth(map.cellIndices.IndexToCell(s));
        }

        private static Map iceSnowMap;
        private static int iceSnowFrame = -1;
        private static float iceSnowDepth;

        private static float IceSnow(Map map)
        {
            if (map == iceSnowMap && Time.frameCount == iceSnowFrame) return iceSnowDepth;
            iceSnowMap = map;
            iceSnowFrame = Time.frameCount;
            float ice = 0f, ground = 0f;
            int iceN = 0, groundN = 0;
            TerrainDef thin = RimWorld.TerrainDefOf.ThinIce;
            int w = map.Size.x, h = map.Size.z;
            for (int z = 1; z < h; z += 3)
            {
                for (int x = 1; x < w; x += 3)
                {
                    var c = new IntVec3(x, 0, z);
                    float d = map.snowGrid.GetDepth(c);
                    if (thin != null && map.terrainGrid.TempTerrainAt(c) == thin)
                    {
                        ice += d;
                        iceN++;
                    }
                    else if (iceN == 0 && !map.terrainGrid.TerrainAt(c).IsWater && !c.Roofed(map))
                    {
                        ground += d;
                        groundN++;
                    }
                }
            }
            iceSnowDepth = iceN > 0 ? ice / iceN : groundN > 0 ? ground / groundN : 0f;
            return iceSnowDepth;
        }

        public static Mesh BuildChunk(Map map, CellRect rect, HorizonField field, MountainMask mountains)
        {
            var verts = new List<Vector3>();
            var colors = new List<Color32>();
            var tris = new List<int>();
            var adj = new float[9];
            float y = AltitudeLayer.Terrain.AltitudeFor();
            bool any = false;
            foreach (IntVec3 c in rect)
            {
                if (c.InBounds(map) || !field.Covers(c))
                {
                    continue;
                }
                for (int k = 0; k < 9; k++)
                {
                    adj[k] = DepthAt(map, field, mountains, c.x + Around[k].x, c.z + Around[k].z);
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
                    float sum = 0f;
                    for (int m = 0; m < weights.Count; m++)
                    {
                        sum += adj[weights[m]];
                    }
                    float a = sum / weights.Count;
                    if (a > 0.01f)
                    {
                        any = true;
                    }
                    colors.Add(new Color32(0, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255)));
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

    public sealed class SeasonWatch
    {
        private const int CheckInterval = 120;
        private readonly Map map;
        private readonly HorizonField field;
        private readonly List<int> cells = new List<int>();
        private int nextCheck;
        private long snowSig, thingSig, iceSig;

        public SeasonWatch(Map map, HorizonField field)
        {
            this.map = map;
            this.field = field;
            var rng = new System.Random(map.Tile.GetHashCode());
            int r = field.RingWidth;
            for (int i = 0; i < 600 && cells.Count < 200; i++)
            {
                int x = rng.Next(-r, map.Size.x + r), z = rng.Next(-r, map.Size.z + r);
                var c = new IntVec3(x, 0, z);
                if (c.InBounds(map))
                {
                    continue;
                }
                int s = field.RawSampleAt(c);
                if (s >= 0)
                {
                    cells.Add(s);
                }
            }
            Compute(out snowSig, out thingSig);
            iceSig = field.Ice?.Signature() ?? 0;
            nextCheck = Find.TickManager.TicksGame + CheckInterval;
        }

        private void Compute(out long snow, out long things)
        {
            float depth = 0f;
            bool any = false;
            int leafless = 0, snowy = 0;
            foreach (int s in cells)
            {
                IntVec3 c = map.cellIndices.IndexToCell(s);
                float d = map.snowGrid.GetDepth(c);
                depth += d;
                any |= d > 0.001f;
                List<Thing> list = map.thingGrid.ThingsListAtFast(s);
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is Plant p)
                    {
                        if (p.LeaflessNow) leafless++;
                        if (d > 0.8f) snowy++;
                    }
                }
            }
            snow = (cells.Count == 0 ? 0 : Mathf.RoundToInt(depth / cells.Count * 50f)) * 2 + (any ? 1 : 0);
            things = ((long)leafless << 32) | (uint)snowy;
        }

        public void Check(bool force, out bool snowChanged, out bool thingsChanged, out bool iceChanged)
        {
            snowChanged = thingsChanged = iceChanged = false;
            int now = Find.TickManager.TicksGame;
            if (!force && now < nextCheck)
            {
                return;
            }
            nextCheck = now + CheckInterval;
            Compute(out long snow, out long things);
            long ice = field.Ice?.Signature() ?? 0;
            iceChanged = force || ice != iceSig;
            iceSig = ice;
            snowChanged = force || snow != snowSig;
            thingsChanged = force || things != thingSig;
            snowSig = snow;
            thingSig = things;
        }
    }
}
