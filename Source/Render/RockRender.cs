using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class RockRender
    {
        private readonly HorizonSurface surface;
        private readonly Map map;
        private readonly HorizonDomain domain;
        private readonly Dictionary<ThingDef, List<Thing>> sources = new Dictionary<ThingDef, List<Thing>>();
        private const int MaxSourcesPerDef = 256;

        internal RockRender(HorizonSurface surface)
        {
            this.surface = surface;
            map = surface.Map;
            domain = surface.Domain;
        }

        internal Thing SourceFor(ThingDef rockDef, IntVec3 target)
        {
            if (!sources.TryGetValue(rockDef, out List<Thing> pool))
            {
                pool = new List<Thing>();
                List<Thing> all = map.listerThings.ThingsOfDef(rockDef);
                var revealed = new List<Thing>();
                var fogged = new List<Thing>();
                for (int i = 0; i < all.Count; i++)
                {
                    Thing c = all[i];
                    IntVec3 p = c.Position;
                    if (!c.Spawned || c.def.size.x != 1 || c.def.size.z != 1 || domain.Depth(p.x, p.z) < 2) continue;
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

        private bool IsRock(int x, int z) => surface.IsRock(x, z);

        internal void PrintRock(SectionLayer layer, IntVec3 c)
        {
            ThingDef rockDef = surface.PrintedRock(c.x, c.z);
            if (rockDef != null && !surface.Fogged(c) && RockPrint.TryPrint(layer, surface, SourceFor(rockDef, c), c))
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

        internal void PrintEdgeShadow(SectionLayer layer, IntVec3 c, bool outsideNeighboursOnly = false)
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
            bool R(int rx, int rz) => (!outsideNeighboursOnly || !domain.Inside(rx, rz)) && IsRock(rx, rz);
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
}
