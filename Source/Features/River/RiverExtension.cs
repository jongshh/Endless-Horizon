using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal sealed class RiverCrossing
    {
        public Rot4 side;
        public float alongCenter;
        public float width;
        public float deepFraction;
        public TerrainDef deep, shallow;
        public Vector2 start;
        public Vector2 outDir;
        public Vector2 targetDir;
        public string link = "none";
        public int traceDepth;
        internal EdgeRun run;

        public override string ToString() =>
            $"{side.ToStringHuman()} at {alongCenter:F0} width={width:F0} deep={deepFraction:P0} trace={traceDepth} out=({outDir.x:F2},{outDir.y:F2}) target=({targetDir.x:F2},{targetDir.y:F2}) link={link} deepDef={deep?.defName} shallowDef={shallow?.defName}";
    }

    internal sealed class RiverExtension
    {
        private const int TraceDepth = 24;

        public static float MeanderScale = 1.6f;
        public static float BankScale = 1f;
        private const float Step = 0.5f;

        private readonly Map map;
        private readonly HorizonDomain domain;
        private readonly int r, w, h, gw, gh;
        private readonly float[] depth;
        private readonly byte[] owner;
        private readonly float[] pathS;
        private readonly int bankMax;
        private Perlin shallowizer, bankNoise;
        public readonly List<RiverCrossing> Crossings = new List<RiverCrossing>();

        public RiverExtension(Map map, HorizonDomain domain)
        {
            this.map = map;
            this.domain = domain;
            r = domain.R;
            w = domain.W;
            h = domain.H;
            gw = w + 2 * r;
            gh = h + 2 * r;
            depth = new float[gw * gh];
            owner = new byte[gw * gh];
            pathS = new float[gw * gh];
            for (int i = 0; i < depth.Length; i++)
            {
                depth[i] = float.NegativeInfinity;
            }
            IntRange bank = map.Biome?.riverbankSizeRange ?? new IntRange(2, 4);
            bankMax = Mathf.Clamp(bank.max, 1, 8);
        }

        private int Index(int x, int z) => (z + r) * gw + (x + r);

        private bool InGrid(int x, int z) => domain.InGrid(x, z);

        private int EdgeLength(Rot4 side) => side == Rot4.North || side == Rot4.South ? w : h;

        private IntVec3 Cell(Rot4 side, int t, int d)
        {
            if (side == Rot4.South) return new IntVec3(t, 0, d);
            if (side == Rot4.North) return new IntVec3(t, 0, h - 1 - d);
            if (side == Rot4.West) return new IntVec3(d, 0, t);
            return new IntVec3(w - 1 - d, 0, t);
        }

        private Vector2 Pos(Rot4 side, float t, float d)
        {
            if (side == Rot4.South) return new Vector2(t + 0.5f, d + 0.5f);
            if (side == Rot4.North) return new Vector2(t + 0.5f, h - 0.5f - d);
            if (side == Rot4.West) return new Vector2(d + 0.5f, t + 0.5f);
            return new Vector2(w - 0.5f - d, t + 0.5f);
        }

        private bool IsRiverAt(IntVec3 c)
        {
            if (!domain.Inside(c))
            {
                return false;
            }
            TerrainDef t = map.terrainGrid.BaseTerrainAt(c);
            return t != null && t.IsRiver;
        }

        public void Build(bool delta)
        {
            int seed = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode()) ^ 0x52495645;
            shallowizer = new Perlin(0.06, 2.0, 0.5, 2, seed + 7, QualityMode.Medium);
            bankNoise = new Perlin(0.05, 2.0, 0.5, 2, seed + 8, QualityMode.Medium);
            if (domain.Rectangle)
            {
                foreach (Rot4 side in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
                {
                    FindCrossings(side);
                }
            }
            else FindRegionCrossings();
            var links = (map.TileInfo as SurfaceTile)?.Rivers;
            for (int i = 0; i < Crossings.Count && i < 250; i++)
            {
                RiverCrossing c = Crossings[i];
                Trace(c);
                MatchWorldLink(c, links);
                var rng = new System.Random(Gen.HashCombineInt(seed, i));
                float widthBoost = delta ? 0.6f : 0f;
                if (delta && c.width >= 6f)
                {
                    float sign = rng.NextDouble() < 0.5 ? -1f : 1f;
                    StampPath(c, i, seed + i * 31, 0f, 0.8f, widthBoost);
                    StampPath(c, i, seed + i * 31 + 17, sign * 28f, 0.55f, widthBoost, startS: 10f);
                }
                else
                {
                    StampPath(c, i, seed + i * 31, 0f, 1f, widthBoost);
                }
            }
        }

        private void FindCrossings(Rot4 side)
        {
            int len = EdgeLength(side);
            int runStart = -1, gap = 0, last = -1;
            for (int t = 0; t <= len; t++)
            {
                bool river = t < len && IsRiverAt(Cell(side, t, 0));
                if (river)
                {
                    if (runStart < 0) runStart = t;
                    last = t;
                    gap = 0;
                }
                else if (runStart >= 0 && ++gap > 2 || t == len && runStart >= 0)
                {
                    Rot4 at = side;
                    AddCrossing(side, runStart, last, i => Cell(at, i, 0));
                    runStart = -1;
                    gap = 0;
                }
            }
        }

        private void FindRegionCrossings()
        {
            foreach (EdgeRun run in EdgeRun.Find(domain, IsRiverAt, 2))
            {
                int before = Crossings.Count;
                AddCrossing(run.Side, 0, run.Cells.Count - 1, i => run.Cells[i]);
                if (Crossings.Count == before) continue;
                RiverCrossing c = Crossings[before];
                c.run = run;
                c.start = run.Centre;
                c.alongCenter = 0f;
                c.width = Mathf.Max(2, run.Width);
            }
        }

        private void AddCrossing(Rot4 side, int a, int b, Func<int, IntVec3> cellAt)
        {
            int width = b - a + 1;
            if (width < 2)
            {
                return;
            }
            var mid = new Dictionary<TerrainDef, int>();
            var outer = new Dictionary<TerrainDef, int>();
            for (int t = a; t <= b; t++)
            {
                TerrainDef def = map.terrainGrid.BaseTerrainAt(cellAt(t));
                if (def == null || !def.IsRiver) continue;
                float u = (t - a + 0.5f) / width;
                var bucket = u > 1f / 3f && u < 2f / 3f ? mid : outer;
                bucket.TryGetValue(def, out int n);
                bucket[def] = n + 1;
            }
            TerrainDef shallow = outer.Count > 0 ? outer.MaxBy(kv => kv.Value).Key : mid.Keys.FirstOrDefault();
            TerrainDef deep = mid.Count > 0 ? mid.MaxBy(kv => kv.Value).Key : shallow;
            int deepCells = 0;
            if (deep != shallow)
            {
                for (int t = a; t <= b; t++)
                {
                    if (map.terrainGrid.BaseTerrainAt(cellAt(t)) == deep) deepCells++;
                }
            }
            float center = (a + b) / 2f;
            Crossings.Add(new RiverCrossing
            {
                side = side,
                alongCenter = center,
                width = width,
                deepFraction = deepCells / (float)width,
                deep = deep,
                shallow = shallow ?? deep,
                start = Pos(side, center, 0f),
            });
        }

        private void Trace(RiverCrossing c)
        {
            var ds = new List<float>();
            var ts = new List<float>();
            float prev = c.alongCenter;
            float halfWindow = c.width / 2f + 2f;
            int len = EdgeLength(c.side);
            for (int d = 0; d < TraceDepth; d++)
            {
                int lo = Mathf.FloorToInt(prev - halfWindow - 2);
                int hi = Mathf.CeilToInt(prev + halfWindow + 2);
                if (c.run == null)
                {
                    lo = Mathf.Max(0, lo);
                    hi = Mathf.Min(len - 1, hi);
                }
                float sum = 0f;
                int n = 0;
                for (int t = lo; t <= hi; t++)
                {
                    if (IsRiverAt(c.run?.Cell(t, d) ?? Cell(c.side, t, d)))
                    {
                        sum += t;
                        n++;
                    }
                }
                if (n == 0)
                {
                    break;
                }
                prev = sum / n;
                ds.Add(d);
                ts.Add(prev);
            }
            c.traceDepth = ds.Count;
            float slope = 0f;
            if (ds.Count >= 4)
            {
                float md = ds.Average(), mt = ts.Average();
                float num = 0f, den = 0f;
                for (int i = 0; i < ds.Count; i++)
                {
                    num += (ds[i] - md) * (ts[i] - mt);
                    den += (ds[i] - md) * (ds[i] - md);
                }
                slope = den > 0f ? Mathf.Clamp(num / den, -1.5f, 1.5f) : 0f;
            }
            c.width = Mathf.Max(2f, c.width / Mathf.Sqrt(1f + slope * slope));
            Vector2 p0 = Pos(c.side, 0f, 0f), pT = Pos(c.side, -slope, 0f), pD = Pos(c.side, 0f, -1f);
            Vector2 dir = c.run != null ? c.run.Normal - c.run.Tangent * slope : (pT - p0) + (pD - p0);
            c.outDir = dir.normalized;
            c.targetDir = c.outDir;
        }

        private void MatchWorldLink(RiverCrossing c, List<SurfaceTile.RiverLink> links)
        {
            if (links == null || links.Count == 0)
            {
                return;
            }
            float best = 0.3f;
            foreach (SurfaceTile.RiverLink link in links)
            {
                float heading = Find.WorldGrid.GetHeadingFromTo(map.Tile, link.neighbor) * Mathf.Deg2Rad;
                var v = new Vector2(Mathf.Sin(heading), Mathf.Cos(heading));
                float dot = Vector2.Dot(v, c.outDir);
                if (dot > best)
                {
                    best = dot;
                    c.targetDir = v;
                    c.link = $"{link.river?.defName}@{heading * Mathf.Rad2Deg:F0}°";
                }
            }
        }

        private const float WideRiver = 8f;

        internal static (float amplitude, float frequency, float widthVariation) Meander(float width)
        {
            float scale = Mathf.Min(1f, WideRiver / Mathf.Max(1f, width));
            return (Mathf.Min(12f, width * 0.5f + 5f) * MeanderScale * scale, 0.035f * scale, 0.15f * scale);
        }

        private void StampPath(RiverCrossing c, int index, int seed, float angleOffsetDeg, float widthScale, float widthBoost, float startS = 0f)
        {
            (float amp, float frequency, float widthVariation) = Meander(c.width);
            var bend = new Perlin(frequency, 2.0, 0.5, 2, seed, QualityMode.Medium);
            var widthNoise = new Perlin(0.05, 2.0, 0.5, 2, seed + 1, QualityMode.Medium);
            float length = r + 12f;
            Vector2 basePos = c.start;
            Vector2 dir = c.outDir;
            basePos -= c.outDir * 1.5f;
            for (float s = -1.5f; s <= length; s += Step)
            {
                float k = Mathf.Clamp01(s / 35f);
                k = k * k * (3f - 2f * k);
                dir = Vector2.Lerp(c.outDir, c.targetDir, k).normalized;
                if (angleOffsetDeg != 0f && s >= startS)
                {
                    float a = angleOffsetDeg * Mathf.Deg2Rad * Mathf.Clamp01((s - startS) / 12f);
                    dir = new Vector2(dir.x * Mathf.Cos(a) - dir.y * Mathf.Sin(a), dir.x * Mathf.Sin(a) + dir.y * Mathf.Cos(a));
                }
                basePos += dir * Step;
                if (s < startS)
                {
                    continue;
                }
                float ramp = Mathf.Clamp01(s / 25f);
                var normal = new Vector2(-dir.y, dir.x);
                Vector2 p = basePos + normal * ((float)bend.GetValue(s, 0, 0) * amp * ramp);
                float width = c.width * widthScale * (1f + widthVariation * ramp * (float)widthNoise.GetValue(s, 0, 0)) * (1f + widthBoost * Mathf.Clamp01(s / 40f));
                if (angleOffsetDeg != 0f)
                {
                    width *= Mathf.Clamp01((s - startS) / 10f);
                }
                Stamp(p, width / 2f, index, s);
            }
        }

        private void Stamp(Vector2 p, float halfWidth, int index, float s)
        {
            if (halfWidth <= 0.2f)
            {
                return;
            }
            float reach = halfWidth + bankMax * BankScale + 1f;
            int x0 = Mathf.FloorToInt(p.x - reach), x1 = Mathf.CeilToInt(p.x + reach);
            int z0 = Mathf.FloorToInt(p.y - reach), z1 = Mathf.CeilToInt(p.y + reach);
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!domain.Outside(x, z))
                    {
                        continue;
                    }
                    float dx = x + 0.5f - p.x, dz = z + 0.5f - p.y;
                    float inside = halfWidth - Mathf.Sqrt(dx * dx + dz * dz);
                    int i = Index(x, z);
                    if (inside > depth[i])
                    {
                        depth[i] = inside;
                        owner[i] = (byte)(index + 1);
                        pathS[i] = Mathf.Max(0f, s);
                    }
                }
            }
        }

        public bool TryPathAt(int x, int z, out int crossing, out float s)
        {
            crossing = -1;
            s = 0f;
            if (!InGrid(x, z)) return false;
            int i = Index(x, z);
            if (owner[i] == 0 || float.IsNegativeInfinity(depth[i])) return false;
            crossing = owner[i] - 1;
            s = pathS[i];
            return true;
        }

        public TerrainDef TerrainAt(IntVec3 c, TerrainDef baseTerrain, IntVec3 nearestInside)
        {
            if (!InGrid(c.x, c.z))
            {
                return null;
            }
            int i = Index(c.x, c.z);
            float v = depth[i];
            if (float.IsNegativeInfinity(v) || owner[i] == 0)
            {
                return null;
            }
            RiverCrossing rc = Crossings[owner[i] - 1];
            if (v > 0f)
            {
                float halfWidth = rc.width / 2f;
                bool deep = rc.deep != rc.shallow && v > halfWidth * (1f - rc.deepFraction) && shallowizer.GetValue(c.x, 0, c.z) > -0.35;
                return deep ? rc.deep : rc.shallow;
            }
            if (baseTerrain != null && baseTerrain.IsWater)
            {
                return null;
            }
            float bank = Mathf.Lerp(1f, bankMax, (float)(bankNoise.GetValue(c.x, 0, c.z) * 0.5 + 0.5)) * BankScale;
            if (v > -bank)
            {
                return MapGenUtility.RiverbankTerrainAt(nearestInside, map) ?? map.Biome?.riverbankTerrain;
            }
            return null;
        }
    }
}
