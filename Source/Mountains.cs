using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    public sealed class MountainMask
    {
        private readonly Map map;
        private readonly HorizonField field;
        private readonly int r, w, h, gw, gh;
        private readonly bool[] rock;
        private readonly ThingDef[] def;
        public int RockCells;
        public int RockCellsWithoutDef;

        public MountainMask(Map map, HorizonField field)
        {
            this.map = map;
            this.field = field;
            r = field.RingWidth;
            w = map.Size.x;
            h = map.Size.z;
            gw = w + 2 * r;
            gh = h + 2 * r;
            rock = new bool[gw * gh];
            def = new ThingDef[gw * gh];
            var raw = new bool[gw * gh];
            var rawDef = new ThingDef[gw * gh];
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    if (x >= 0 && z >= 0 && x < w && z < h)
                    {
                        continue;
                    }
                    var c = new IntVec3(x, 0, z);
                    int s = field.RawSampleAt(c);
                    TerrainDef t = field.At(c);
                    if (field.Generator != null)
                    {
                        if (t != null && !t.IsWater && field.GenRock(c))
                        {
                            raw[Index(x, z)] = true;
                            rawDef[Index(x, z)] = field.GenRockDef(c);
                        }
                        continue;
                    }
                    if (s < 0 || t == null || t.IsWater)
                    {
                        continue;
                    }
                    Building b = map.edificeGrid.InnerArray[s];
                    if (b != null && b.def.mineable && b.def.graphic is Graphic_Linked)
                    {
                        raw[Index(x, z)] = true;
                        rawDef[Index(x, z)] = b.def;
                    }
                }
            }
            var counts = new Dictionary<ThingDef, int>();
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    if (x >= 0 && z >= 0 && x < w && z < h)
                    {
                        continue;
                    }
                    TerrainDef t = field.At(new IntVec3(x, 0, z));
                    if (t == null || t.IsWater)
                    {
                        continue;
                    }
                    int n = 0;
                    counts.Clear();
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            ThingDef d = RawRockAt(raw, rawDef, x + dx, z + dz);
                            if (d != null)
                            {
                                n++;
                                counts.TryGetValue(d, out int k);
                                counts[d] = k + 1;
                            }
                        }
                    }
                    int dist = Mathf.Max(Mathf.Max(-x, x - w + 1), Mathf.Max(-z, z - h + 1));
                    ThingDef edgeRock = null;
                    if (dist <= 2)
                    {
                        Building eb = map.edificeGrid.InnerArray[map.cellIndices.CellToIndex(Mathf.Clamp(x, 0, w - 1), Mathf.Clamp(z, 0, h - 1))];
                        if (eb != null && eb.def.mineable && eb.def.graphic is Graphic_Linked)
                        {
                            edgeRock = eb.def;
                        }
                    }
                    bool forceEdge = field.Generator == null && edgeRock != null;
                    bool isRock = dist <= 2 ? forceEdge || raw[Index(x, z)] : n >= 5;
                    if (isRock)
                    {
                        int i = Index(x, z);
                        rock[i] = true;
                        ThingDef best = edgeRock ?? rawDef[i];
                        if (best == null)
                        {
                            int bestCount = 0;
                            foreach (KeyValuePair<ThingDef, int> kv in counts)
                            {
                                if (kv.Value > bestCount)
                                {
                                    best = kv.Key;
                                    bestCount = kv.Value;
                                }
                            }
                        }
                        def[i] = best;
                        RockCells++;
                        if (best == null) RockCellsWithoutDef++;
                    }
                }
            }
        }

        private int Index(int x, int z) => (z + r) * gw + (x + r);

        private bool InRing(int x, int z) => x >= -r && z >= -r && x < w + r && z < h + r;

        private ThingDef RawRockAt(bool[] raw, ThingDef[] rawDef, int x, int z)
        {
            if (x >= 0 && z >= 0 && x < w && z < h)
            {
                Building b = map.edificeGrid.InnerArray[map.cellIndices.CellToIndex(x, z)];
                return b != null && b.def.mineable && b.def.graphic is Graphic_Linked ? b.def : null;
            }
            if (!InRing(x, z))
            {
                return null;
            }
            int i = Index(x, z);
            return raw[i] ? rawDef[i] : null;
        }

        public bool IsRock(int x, int z)
        {
            if (x >= 0 && z >= 0 && x < w && z < h)
            {
                Building b = map.edificeGrid.InnerArray[map.cellIndices.CellToIndex(x, z)];
                return b != null && b.def.mineable;
            }
            x = Mathf.Clamp(x, -r, w + r - 1);
            z = Mathf.Clamp(z, -r, h + r - 1);
            return rock[Index(x, z)];
        }

        public ThingDef RockDefAt(int x, int z) => InRing(x, z) ? def[Index(x, z)] : null;

        private readonly Dictionary<ThingDef, List<Thing>> sources = new Dictionary<ThingDef, List<Thing>>();
        private const int MaxSourcesPerDef = 256;

        public Thing SourceFor(ThingDef rockDef, IntVec3 target)
        {
            if (!sources.TryGetValue(rockDef, out List<Thing> pool))
            {
                pool = new List<Thing>();
                int w = map.Size.x, h = map.Size.z;
                List<Thing> all = map.listerThings.ThingsOfDef(rockDef);
                var revealed = new List<Thing>();
                var fogged = new List<Thing>();
                for (int i = 0; i < all.Count; i++)
                {
                    Thing c = all[i];
                    IntVec3 p = c.Position;
                    if (!c.Spawned || c.def.size.x != 1 || c.def.size.z != 1 || p.x < 2 || p.z < 2 || p.x >= w - 2 || p.z >= h - 2) continue;
                    (map.fogGrid.IsFogged(p) ? fogged : revealed).Add(c);
                }
                List<Thing> from = revealed.Count > 0 ? revealed : fogged;
                int stride = Mathf.Max(1, from.Count / MaxSourcesPerDef);
                for (int i = 0; i < from.Count && pool.Count < MaxSourcesPerDef; i += stride)
                {
                    pool.Add(from[i]);
                }
                sources[rockDef] = pool;
            }
            if (pool.Count == 0) return null;
            int start = (int)((uint)Gen.HashCombineInt(target.x * 73856093, target.z * 19349663) % (uint)pool.Count);
            for (int k = 0; k < pool.Count; k++)
            {
                Thing t = pool[(start + k) % pool.Count];
                if (t.Spawned && t.Map == map) return t;
            }
            return null;
        }

        private static readonly Vector2[] CornerFillUVs =
        {
            new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.6f),
        };
        private static readonly float CoverOffsetDist = new Vector2(0.5f, 0.5f).magnitude - new Vector2(0.5f, 0.5f).magnitude * 0.5f;
        private static readonly Color32 Shadowed = new Color32(195, 195, 195, 255);
        private static readonly Color32 Lit = new Color32(255, 255, 255, 255);

        public void PrintRock(SectionLayer layer, IntVec3 c)
        {
            ThingDef rockDef = RockDefAt(c.x, c.z);
            if (rockDef != null && !HorizonThings.OutsideFogged(map, field, c) && RockPrint.TryPrint(layer, this, SourceFor(rockDef, c), c))
            {
                return;
            }
            if (rockDef != null) RockPrint.Fallbacks++;
            if (!(rockDef?.graphic is Graphic_Linked linked))
            {
                return;
            }
            Material atlas = linked.SubGraphic.MatSingle;
            bool n = IsRock(c.x, c.z + 1), e = IsRock(c.x + 1, c.z), s = IsRock(c.x, c.z - 1), wv = IsRock(c.x - 1, c.z);
            int bits = (n ? 1 : 0) | (e ? 2 : 0) | (s ? 4 : 0) | (wv ? 8 : 0);
            Material mat = MaterialAtlasPool.SubMaterialFromAtlas(atlas, (LinkDirections)bits);
            Vector3 center = c.ToVector3Shifted();
            center.y = rockDef.Altitude;
            Printer_Plane.PrintPlane(layer, center, Vector2.one, mat);
            if (linked is Graphic_LinkedCornerFiller)
            {
                bool[] corner =
                {
                    wv && s && IsRock(c.x - 1, c.z - 1),
                    wv && n && IsRock(c.x - 1, c.z + 1),
                    e && n && IsRock(c.x + 1, c.z + 1),
                    e && s && IsRock(c.x + 1, c.z - 1),
                };
                for (int i = 0; i < 4; i++)
                {
                    if (!corner[i])
                    {
                        continue;
                    }
                    Vector3 p = center + GenAdj.DiagonalDirectionsAround[i].ToVector3().normalized * CoverOffsetDist + Altitudes.AltIncVect + new Vector3(0f, 0f, 0.09f);
                    Printer_Plane.PrintPlane(layer, p, new Vector2(0.5f, 0.5f), mat, 0f, false, CornerFillUVs);
                }
            }
        }

        public void PrintEdgeShadow(SectionLayer layer, IntVec3 c, bool outsideNeighboursOnly = false)
        {
            LayerSubMesh sm = layer.GetSubMesh(MatBases.EdgeShadow);
            float y = AltitudeLayer.Shadows.AltitudeFor();
            int x = c.x, z = c.z;
            void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Color32 ca, Color32 cb, Color32 ccol, Color32 cd)
            {
                int i = sm.verts.Count;
                sm.verts.Add(a); sm.verts.Add(b); sm.verts.Add(cc); sm.verts.Add(d);
                sm.colors.Add(ca); sm.colors.Add(cb); sm.colors.Add(ccol); sm.colors.Add(cd);
                sm.tris.Add(i); sm.tris.Add(i + 1); sm.tris.Add(i + 2);
                sm.tris.Add(i); sm.tris.Add(i + 2); sm.tris.Add(i + 3);
            }
            void Tri(Vector3 a, Vector3 b, Vector3 cc)
            {
                int i = sm.verts.Count;
                sm.verts.Add(a); sm.verts.Add(b); sm.verts.Add(cc);
                sm.colors.Add(Shadowed); sm.colors.Add(Lit); sm.colors.Add(Lit);
                sm.tris.Add(i); sm.tris.Add(i + 1); sm.tris.Add(i + 2);
            }
            if (IsRock(x, z))
            {
                if (!outsideNeighboursOnly)
                {
                    Quad(new Vector3(x, y, z), new Vector3(x, y, z + 1), new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y, z), Shadowed, Shadowed, Shadowed, Shadowed);
                }
                return;
            }
            const float d = 0.45f;
            bool R(int rx, int rz) => (!outsideNeighboursOnly || rx < 0 || rz < 0 || rx >= w || rz >= h) && IsRock(rx, rz);
            bool n = R(x, z + 1), e = R(x + 1, z), s = R(x, z - 1), wv = R(x - 1, z);
            if (n) Quad(new Vector3(x, y, z + 1), new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y, z + 1 - d), new Vector3(x, y, z + 1 - d), Shadowed, Shadowed, Lit, Lit);
            if (s) Quad(new Vector3(x + 1, y, z), new Vector3(x, y, z), new Vector3(x, y, z + d), new Vector3(x + 1, y, z + d), Shadowed, Shadowed, Lit, Lit);
            if (e) Quad(new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y, z), new Vector3(x + 1 - d, y, z), new Vector3(x + 1 - d, y, z + 1), Shadowed, Shadowed, Lit, Lit);
            if (wv) Quad(new Vector3(x, y, z), new Vector3(x, y, z + 1), new Vector3(x + d, y, z + 1), new Vector3(x + d, y, z), Shadowed, Shadowed, Lit, Lit);
            if (!n && !wv && R(x - 1, z + 1)) Tri(new Vector3(x, y, z + 1), new Vector3(x + d, y, z + 1), new Vector3(x, y, z + 1 - d));
            if (!n && !e && R(x + 1, z + 1)) Tri(new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y, z + 1 - d), new Vector3(x + 1 - d, y, z + 1));
            if (!s && !e && R(x + 1, z - 1)) Tri(new Vector3(x + 1, y, z), new Vector3(x + 1 - d, y, z), new Vector3(x + 1, y, z + d));
            if (!s && !wv && R(x - 1, z - 1)) Tri(new Vector3(x, y, z), new Vector3(x, y, z + d), new Vector3(x + d, y, z));
        }
    }

    public static class RingLighting
    {
        public static Mesh BuildChunk(Map map, CellRect rect, HorizonField field, MountainMask mountains, int fogStart)
        {
            int w = map.Size.x, h = map.Size.z;
            float fadeFrom = fogStart - 8f, fadeTo = field.RingWidth - 8f;
            void Cell(int x, int z, out Color32 glow, out bool blocks, out bool roofed)
            {
                glow = new Color32(0, 0, 0, 0);
                blocks = false;
                roofed = false;
                int index;
                if (x >= 0 && z >= 0 && x < w && z < h)
                {
                    index = map.cellIndices.CellToIndex(x, z);
                    Building b = map.edificeGrid.InnerArray[index];
                    blocks = b != null && b.def.blockLight;
                    RoofDef roof = map.roofGrid.RoofAt(index);
                    roofed = roof != null && (roof.isThickRoof || b == null || !b.def.holdsRoof);
                    glow = map.glowGrid.VisualGlowAt(index);
                    return;
                }
                var c = new IntVec3(x, 0, z);
                if (!field.Covers(c))
                {
                    return;
                }
                if (mountains != null && mountains.IsRock(x, z))
                {
                    blocks = true;
                    roofed = field.OutsideThickRoof(c);
                    return;
                }
                index = field.RawSampleAt(c);
                if (index < 0)
                {
                    return;
                }
                RoofDef srcRoof = map.roofGrid.RoofAt(index);
                roofed = srcRoof != null && srcRoof.isNatural;
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
                    float dx = Mathf.Max(0f, Mathf.Max(-x, x - w)), dz = Mathf.Max(0f, Mathf.Max(-z, z - h));
                    float t = Mathf.Clamp01((Mathf.Sqrt(dx * dx + dz * dz) - fadeFrom) / (fadeTo - fadeFrom));
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
                    if (cell.InBounds(map) || !field.Covers(cell))
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
