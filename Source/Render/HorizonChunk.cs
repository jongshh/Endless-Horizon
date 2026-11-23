using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class HorizonChunk : IDisposable
    {
        public readonly CellRect Rect;
        private readonly Map map;
        private readonly HorizonSurface surface;
        private readonly HorizonDomain domain;
        private readonly bool depthPass;
        public readonly List<LayerSubMesh> subMeshes = new List<LayerSubMesh>();
        private readonly HashSet<LayerSubMesh> water = new HashSet<LayerSubMesh>();
        public int VertexCount;
        public bool HasGeometry;

        public HorizonChunk(HorizonSurface surface, CellRect rect, bool depthPass)
        {
            map = surface.Map;
            Rect = rect;
            this.surface = surface;
            domain = surface.Domain;
            this.depthPass = depthPass;
        }

        private Map Map => map;

        private LayerSubMesh GetSubMesh(Material material)
        {
            if (material == null)
            {
                return null;
            }
            for (int i = 0; i < subMeshes.Count; i++)
            {
                if (subMeshes[i].material == material)
                {
                    return subMeshes[i];
                }
            }
            var sm = new LayerSubMesh(new Mesh(), material);
            subMeshes.Add(sm);
            return sm;
        }

        public void DrawLayer(bool waterSurface = true)
        {
            for (int i = 0; i < subMeshes.Count; i++)
            {
                LayerSubMesh sm = subMeshes[i];
                if (sm.finalized && !sm.disabled && (waterSurface || !water.Contains(sm)))
                {
                    Graphics.DrawMesh(sm.mesh, Matrix4x4.identity, sm.material, sm.renderLayer);
                }
            }
        }

        public void Dispose()
        {
            foreach (LayerSubMesh sm in subMeshes)
            {
                UnityEngine.Object.Destroy(sm.mesh);
            }
            subMeshes.Clear();
            water.Clear();
        }

        private CellTerrain TerrainAt(IntVec3 c, bool asNeighbor)
        {
            Map m = Map;
            if (domain.Inside(c))
            {
                var ct = new CellTerrain(m.terrainGrid.TerrainAt(c), c.IsPolluted(m), m.snowGrid.GetDepth(c), c.GetSandDepth(m), m.terrainGrid.ColorAt(c));
                if (asNeighbor)
                {
                    Thing edifice = c.GetEdifice(m);
                    if (edifice != null && edifice.def.coversFloor)
                    {
                        ct.def = TerrainDefOf.Underwall;
                    }
                }
                return ct;
            }
            return new CellTerrain(surface.Top(c), surface.Polluted(c), 0f, 0f, null);
        }

        private Material MaterialFor(CellTerrain ct)
        {
            if (ct.def == null)
            {
                return null;
            }
            if (depthPass)
            {
                return ct.def.waterDepthMaterial;
            }
            bool polluted = ct.polluted && ct.snowCoverage < 0.4f && ct.sandCoverage < 0.4f && ct.def.graphicPolluted != BaseContent.BadGraphic;
            return Map.terrainGrid.GetMaterial(ct.def, polluted, ct.color);
        }

        public static bool SeamBlend = true;

        private void AddSeamFans(CellTerrain[] around, HashSet<CellTerrain> blends, bool[] fanOn)
        {
            if (!SeamBlend)
            {
                return;
            }
            Map m = Map;
            foreach (IntVec3 c in Rect)
            {
                if (!domain.OnEdge(c))
                {
                    continue;
                }
                CellTerrain self = TerrainAt(c, false);
                if (self.def == null || m.terrainGrid.FoundationAt(c) != null)
                {
                    continue;
                }
                blends.Clear();
                var outside = new bool[8];
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 nc = c + GenAdj.AdjacentCellsAroundBottom[i];
                    outside[i] = domain.Drawn(nc);
                    if (outside[i] || domain.Inside(nc))
                    {
                        around[i] = TerrainAt(nc, true);
                        if (around[i].def == null)
                        {
                            around[i] = self;
                        }
                    }
                    else
                    {
                        around[i] = self;
                    }
                    CellTerrain other = around[i];
                    if (outside[i] && !surface.HasFoundation(nc) && !other.Equals(self) && other.def.edgeType != TerrainDef.TerrainEdgeType.Hard
                        && other.def.renderPrecedence >= self.def.renderPrecedence)
                    {
                        blends.Add(other);
                    }
                }
                foreach (CellTerrain blend in blends)
                {
                    LayerSubMesh fm = GetSubMesh(MaterialFor(blend));
                    if (fm == null)
                    {
                        continue;
                    }
                    for (int j = 0; j < 8; j++)
                    {
                        fanOn[j] = false;
                    }
                    for (int k = 0; k < 8; k++)
                    {
                        if (!outside[k] || !around[k].Equals(blend))
                        {
                            continue;
                        }
                        if (k % 2 == 0)
                        {
                            fanOn[(k + 7) % 8] = true;
                            fanOn[k] = true;
                            fanOn[(k + 1) % 8] = true;
                        }
                        else
                        {
                            fanOn[k] = true;
                        }
                    }
                    int n = fm.verts.Count;
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z + 0.5f));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z + 0.5f));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z + 0.5f));
                    for (int l = 0; l < 8; l++)
                    {
                        fm.colors.Add(WithAlpha(fanOn[l], 1f));
                    }
                    fm.colors.Add(WithAlpha(false, 1f));
                    for (int t = 0; t < 8; t++)
                    {
                        fm.tris.Add(n + t);
                        fm.tris.Add(n + (t + 1) % 8);
                        fm.tris.Add(n + 8);
                    }
                }
            }
        }

        private static Color32 WithAlpha(bool on, float alpha)
        {
            return new Color32(255, 255, 255, on ? (byte)Mathf.RoundToInt(alpha * 255f) : (byte)0);
        }

        private void AddBridgeProps()
        {
            TerrainDef Bridge(IntVec3 c)
            {
                TerrainDef t = surface.Floor(c);
                return t?.bridge == true ? t : null;
            }
            bool Below(IntVec3 c, out TerrainDef bridge)
            {
                var south = new IntVec3(c.x, 0, c.z - 1);
                bridge = Bridge(c);
                if (bridge == null) return false;
                bool inside = domain.Inside(c), southInside = domain.Inside(south);
                if (inside && southInside || !inside && !domain.Drawn(c)) return false;
                if (!southInside && !domain.InGrid(south) || Bridge(south) != null) return false;
                TerrainDef under = surface.Top(south);
                return under != null && (under.passability == Traversability.Impassable
                    || under.affordances?.Contains(TerrainDefOf.Bridge.terrainAffordanceNeeded) == true);
            }
            float y = AltitudeLayer.TerrainScatter.AltitudeFor();
            foreach (IntVec3 c in Rect)
            {
                if (!Below(c, out TerrainDef bridge)) continue;
                Graphic graphic = Below(new IntVec3(c.x + 1, 0, c.z), out _) ? bridge.bridgePropsLoopGraphic : bridge.bridgePropsRightGraphic;
                LayerSubMesh sm = GetSubMesh(graphic?.MatSingle);
                if (sm == null) continue;
                int n = sm.verts.Count;
                sm.verts.Add(new Vector3(c.x, y, c.z - 1)); sm.verts.Add(new Vector3(c.x, y, c.z));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z)); sm.verts.Add(new Vector3(c.x + 1, y, c.z - 1));
                sm.uvs.Add(new Vector2(0f, 0f)); sm.uvs.Add(new Vector2(0f, 1f));
                sm.uvs.Add(new Vector2(1f, 1f)); sm.uvs.Add(new Vector2(1f, 0f));
                sm.tris.Add(n); sm.tris.Add(n + 1); sm.tris.Add(n + 2);
                sm.tris.Add(n); sm.tris.Add(n + 2); sm.tris.Add(n + 3);
            }
        }

        public void Regenerate()
        {
            Dispose();
            Map m = Map;
            float y = AltitudeLayer.Terrain.AltitudeFor();
            var around = new CellTerrain[8];
            var blends = new HashSet<CellTerrain>();
            var fanOn = new bool[8];
            foreach (IntVec3 c in Rect)
            {
                if (!domain.Drawn(c))
                {
                    continue;
                }
                CellTerrain self = TerrainAt(c, false);
                if (self.def == null)
                {
                    continue;
                }
                LayerSubMesh sm = GetSubMesh(self.def.dontRender ? (depthPass ? null : MatBases.ShadowMask) : MaterialFor(self));
                if (sm != null)
                {
                    if (self.def.IsWater) water.Add(sm);
                    int n = sm.verts.Count;
                    sm.verts.Add(new Vector3(c.x, y, c.z));
                    sm.verts.Add(new Vector3(c.x, y, c.z + 1));
                    sm.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                    sm.verts.Add(new Vector3(c.x + 1, y, c.z));
                    for (int k = 0; k < 4; k++) sm.colors.Add(WithAlpha(true, 1f));
                    sm.tris.Add(n); sm.tris.Add(n + 1); sm.tris.Add(n + 2);
                    sm.tris.Add(n); sm.tris.Add(n + 2); sm.tris.Add(n + 3);
                }

                blends.Clear();
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 nc = c + GenAdj.AdjacentCellsAroundBottom[i];
                    if (!domain.Inside(nc) && !domain.InGrid(nc))
                    {
                        around[i] = self;
                        continue;
                    }
                    CellTerrain other = TerrainAt(nc, true);
                    if (other.def == null)
                    {
                        around[i] = self;
                        continue;
                    }
                    around[i] = other;
                    bool foundationHere = surface.HasFoundation(c);
                    bool foundationThere = surface.HasFoundation(nc);
                    if (!other.Equals(self) && other.def.edgeType != TerrainDef.TerrainEdgeType.Hard && !foundationHere && !foundationThere
                        && other.def.renderPrecedence >= self.def.renderPrecedence)
                    {
                        blends.Add(other);
                    }
                }
                foreach (CellTerrain blend in blends)
                {
                    LayerSubMesh fm = GetSubMesh(MaterialFor(blend));
                    if (fm == null)
                    {
                        continue;
                    }
                    int n = fm.verts.Count;
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z + 0.5f));
                    fm.verts.Add(new Vector3(c.x, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z + 1));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z + 0.5f));
                    fm.verts.Add(new Vector3(c.x + 1, 0f, c.z));
                    fm.verts.Add(new Vector3(c.x + 0.5f, 0f, c.z + 0.5f));
                    for (int j = 0; j < 8; j++)
                    {
                        fanOn[j] = false;
                    }
                    for (int k = 0; k < 8; k++)
                    {
                        if (!around[k].Equals(blend))
                        {
                            continue;
                        }
                        if (k % 2 == 0)
                        {
                            fanOn[(k + 7) % 8] = true;
                            fanOn[k] = true;
                            fanOn[(k + 1) % 8] = true;
                        }
                        else
                        {
                            fanOn[k] = true;
                        }
                    }
                    for (int l = 0; l < 8; l++)
                    {
                        fm.colors.Add(WithAlpha(fanOn[l], 1f));
                    }
                    fm.colors.Add(WithAlpha(false, 1f));
                    for (int t = 0; t < 8; t++)
                    {
                        fm.tris.Add(n + t);
                        fm.tris.Add(n + (t + 1) % 8);
                        fm.tris.Add(n + 8);
                    }
                }
            }
            AddSeamFans(around, blends, fanOn);
            if (!depthPass) AddBridgeProps();
            VertexCount = 0;
            foreach (LayerSubMesh sm in subMeshes)
            {
                VertexCount += sm.verts.Count;
            }
            HasGeometry = VertexCount > 0;
            subMeshes.RemoveAll(sm => sm.verts.Count == 0);
            Bounds bounds = Rect.ExpandedBy(2).ToBounds();
            foreach (LayerSubMesh sm in subMeshes)
            {
                sm.FinalizeMesh(MeshParts.All);
                sm.mesh.bounds = bounds;
            }
            if (depthPass)
            {
                int layer = SubcameraDefOf.WaterDepth.LayerId;
                foreach (LayerSubMesh sm in subMeshes)
                {
                    sm.renderLayer = layer;
                }
            }
        }
    }
}
