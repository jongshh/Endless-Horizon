using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class OutsideGlow
    {
        private readonly Map map;
        private readonly LandGrid grid;
        private readonly FogModule fog;
        private readonly MountainMask mountains;
        private readonly HorizonDomain domain;
        private readonly int w, h, r, gw;
        private readonly ColorInt[] sum;
        private readonly bool[] overlit;
        private readonly ColorInt[] fixedSum;
        private readonly bool[] fixedOverlit;
        private bool fixedAny;
        public bool Any { get; private set; }
        public int MapLightCount { get; private set; }
        public int OutsideLightCount { get; private set; }

        private static AccessTools.FieldRef<GlowGrid, HashSet<CompGlower>> litGlowers;
        private static AccessTools.FieldRef<GlowGrid, HashSet<IntVec3>> litTerrain;
        private static bool reflectTried, reflectWarned;

        private struct Light
        {
            public IntVec3 Pos;
            public float Radius;
            public ColorInt Color;
            public float Overlight;
        }

        public OutsideGlow(Map map, HorizonDomain domain, LandGrid grid, FogModule fog, MountainMask mountains, float maxThingDist, OutsideGlow previous = null)
        {
            this.map = map;
            this.grid = grid;
            this.fog = fog;
            this.mountains = mountains;
            this.domain = domain;
            w = domain.W;
            h = domain.H;
            r = domain.R;
            gw = w + 2 * r;
            if (previous != null && previous.sum.Length == gw * (h + 2 * r))
            {
                fixedSum = previous.fixedSum;
                fixedOverlit = previous.fixedOverlit;
                fixedAny = previous.fixedAny;
            }
            else
            {
                sum = new ColorInt[gw * (h + 2 * r)];
                overlit = new bool[sum.Length];
                var outside = new List<Light>();
                OutsideLights(outside, maxThingDist);
                foreach (Light l in outside) Flood(l);
                OutsideLightCount = outside.Count;
                fixedSum = sum;
                fixedOverlit = overlit;
                fixedAny = Any;
            }
            sum = new ColorInt[gw * (h + 2 * r)];
            overlit = new bool[sum.Length];
            Any = fixedAny;
            if (previous != null) OutsideLightCount = previous.OutsideLightCount;
            var lights = new List<Light>();
            MapLights(lights);
            MapLightCount = lights.Count;
            foreach (Light l in lights) Flood(l);
        }

        private bool InMap(int x, int z) => domain.Inside(x, z);

        private bool InGrid(int x, int z) => domain.InGrid(x, z);

        private int Index(int x, int z) => (z + r) * gw + (x + r);

        public Color32 At(int x, int z)
        {
            if (!Any || InMap(x, z) || !InGrid(x, z)) return new Color32(0, 0, 0, 0);
            int i = Index(x, z);
            ColorInt c = sum[i] + fixedSum[i];
            c.a = overlit[i] || fixedOverlit[i] ? 1 : 0;
            return c.ProjectToColor32();
        }

        private static float EdgeDistance(IntVec3 p, HorizonDomain domain)
        {
            int depth = domain.Depth(p.x, p.z);
            return depth < 0 ? float.MaxValue : depth + 1;
        }

        private static void EnsureReflection()
        {
            if (reflectTried) return;
            reflectTried = true;
            try
            {
                litGlowers = AccessTools.FieldRefAccess<GlowGrid, HashSet<CompGlower>>("litGlowers");
                litTerrain = AccessTools.FieldRefAccess<GlowGrid, HashSet<IntVec3>>("litTerrain");
            }
            catch (Exception ex)
            {
                litGlowers = null;
                litTerrain = null;
                Log.Error(HorizonLog.Prefix + "Map lights could not be read; light from the map does not reach past its edge. " + ex);
            }
        }

        private void MapLights(List<Light> lights)
        {
            EnsureReflection();
            if (litGlowers == null) return;
            try
            {
                foreach (CompGlower g in litGlowers(map.glowGrid))
                {
                    if (g?.parent == null || !g.parent.Spawned) continue;
                    IntVec3 p = g.parent.Position;
                    float radius = g.GlowRadius;
                    if (radius <= 0f || EdgeDistance(p, domain) > radius + 1f) continue;
                    lights.Add(new Light { Pos = p, Radius = radius, Color = g.GlowColor, Overlight = g.Props.overlightRadius });
                }
                if (litTerrain != null)
                {
                    foreach (IntVec3 p in litTerrain(map.glowGrid))
                    {
                        TerrainDef t = domain.Inside(p) ? map.terrainGrid.TerrainAt(p) : null;
                        if (t == null || t.glowRadius <= 0f || EdgeDistance(p, domain) > t.glowRadius + 1f) continue;
                        lights.Add(new Light { Pos = p, Radius = t.glowRadius, Color = t.glowColor });
                    }
                }
            }
            catch (Exception ex)
            {
                if (!reflectWarned)
                {
                    reflectWarned = true;
                    Log.Error(HorizonLog.Prefix + "Reading map lights failed; light from the map does not reach past its edge. " + ex);
                }
            }
        }

        private void OutsideLights(List<Light> lights, float maxThingDist)
        {
            for (int z = -r; z < h + r; z++)
            {
                for (int x = -r; x < w + r; x++)
                {
                    if (InMap(x, z)) continue;
                    var c = new IntVec3(x, 0, z);
                    if (mountains != null && mountains.IsRock(x, z)) continue;
                    TerrainDef t = (domain.InGrid(c) ? grid.Cells[grid.Index(c.x, c.z)] : null);
                    if (t != null && t.glowRadius > 0f) lights.Add(new Light { Pos = c, Radius = t.glowRadius, Color = t.glowColor });
                    float d = domain.Steps(x, z);
                    if (d > maxThingDist || !HorizonDebug.Things || !HorizonSettings.Current.things) continue;
                    int sample = (domain.InGrid(c) ? grid.Samples[grid.Index(c.x, c.z)] : -1);
                    if (sample < 0) continue;
                    IntVec3 src = map.cellIndices.IndexToCell(sample);
                    List<Thing> things = map.thingGrid.ThingsListAtFast(sample);
                    bool fogged = fog.Fogged(c);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing th = things[i];
                        if (th.Position != src || !OutsideSources.IsShown(th) || (fogged && !th.def.seeThroughFog)) continue;
                        CompGlower g = th.TryGetComp<CompGlower>();
                        if (g == null || !g.Glows || g.GlowRadius <= 0f) continue;
                        lights.Add(new Light { Pos = c, Radius = g.GlowRadius, Color = g.GlowColor, Overlight = g.Props.overlightRadius });
                    }
                }
            }
        }

        private bool Blocks(int x, int z)
        {
            if (InMap(x, z))
            {
                Building b = map.edificeGrid[new IntVec3(x, 0, z)];
                return b != null && b.def.blockLight;
            }
            return mountains != null && mountains.IsRock(x, z);
        }

        private static readonly int[] Dx = { 0, 1, 0, -1, 1, 1, -1, -1 };
        private static readonly int[] Dz = { -1, 0, 1, 0, -1, 1, 1, -1 };

        private void Flood(Light l)
        {
            int rad = Mathf.CeilToInt(l.Radius);
            int dia = 2 * rad + 1;
            int maxCost = Mathf.RoundToInt(l.Radius * 100f);
            var dist = new int[dia * dia];
            var done = new bool[dia * dia];
            for (int i = 0; i < dist.Length; i++) dist[i] = int.MaxValue;
            int L(int dx, int dz) => (dz + rad) * dia + (dx + rad);
            var queue = new SortedSet<(int d, int i)>();
            dist[L(0, 0)] = 100;
            queue.Add((100, L(0, 0)));
            float slope = -1f / l.Radius;
            var blockers = new bool[4];
            while (queue.Count > 0)
            {
                (int d0, int i0) = queue.Min;
                queue.Remove(queue.Min);
                if (done[i0]) continue;
                done[i0] = true;
                int dx0 = i0 % dia - rad, dz0 = i0 / dia - rad;
                int cx = l.Pos.x + dx0, cz = l.Pos.z + dz0;
                float dc = d0 / 100f;
                if (dc <= l.Radius && !InMap(cx, cz) && InGrid(cx, cz))
                {
                    float k = Mathf.Lerp(1f + slope * dc, 1f / (dc * dc), 0.4f);
                    ColorInt add = l.Color * k;
                    add.a = 0;
                    add.ClampToNonNegative();
                    if (add.r > 0 || add.g > 0 || add.b > 0)
                    {
                        int gi = Index(cx, cz);
                        sum[gi] += add;
                        if (dc < l.Overlight) overlit[gi] = true;
                        Any = true;
                    }
                }
                for (int n = 0; n < 8; n++)
                {
                    int ndx = dx0 + Dx[n], ndz = dz0 + Dz[n];
                    if (ndx < -rad || ndz < -rad || ndx > rad || ndz > rad) continue;
                    int ni = L(ndx, ndz);
                    if (done[ni]) continue;
                    int nx = l.Pos.x + ndx, nz = l.Pos.z + ndz;
                    if (!InGrid(nx, nz)) continue;
                    bool blocked = Blocks(nx, nz);
                    if (n < 4) blockers[n] = blocked;
                    if (blocked) continue;
                    int nd = d0 + (n < 4 ? 100 : 141);
                    if (nd > maxCost) continue;
                    if ((n == 4 && blockers[0] && blockers[1]) || (n == 5 && blockers[1] && blockers[2])
                        || (n == 6 && blockers[2] && blockers[3]) || (n == 7 && blockers[0] && blockers[3])) continue;
                    if (nd < dist[ni])
                    {
                        dist[ni] = nd;
                        queue.Add((nd, ni));
                    }
                }
            }
        }

        public static int Signature(Map map, HorizonDomain domain)
        {
            int hash = 17;
            EnsureReflection();
            if (litGlowers != null)
            {
                try
                {
                    foreach (CompGlower g in litGlowers(map.glowGrid))
                    {
                        if (g?.parent == null || !g.parent.Spawned) continue;
                        IntVec3 p = g.parent.Position;
                        if (EdgeDistance(p, domain) > g.GlowRadius + 1f) continue;
                        ColorInt c = g.GlowColor;
                        hash = Gen.HashCombineInt(hash, p.x * 7919 + p.z * 104729 + (int)(g.GlowRadius * 10f) + c.r * 3 + c.g * 5 + c.b * 11);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce(HorizonLog.Prefix + "Reading map lights for the outside light check failed. " + ex, 0x45484753);
                }
            }
            foreach (int i in domain.Edge)
            {
                hash = Gen.HashCombineInt(hash, Glow(map, i % domain.W, i / domain.W));
            }
            return hash;
        }

        private static int Glow(Map map, int x, int z)
        {
            Color32 c = map.glowGrid.VisualGlowAt(map.cellIndices.CellToIndex(x, z));
            return c.r | (c.g << 8) | (c.b << 16) | (c.a << 24);
        }
    }
}
