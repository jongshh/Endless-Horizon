using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace EndlessHorizon
{
    public sealed partial class HorizonField
    {
        private readonly Dictionary<int, TerrainDef> coastBoundarySnapshot = new Dictionary<int, TerrainDef>();
        public int CoastBoundaryCells { get; private set; }

        internal bool BoundaryBaseChanged(IntVec3 c)
        {
            if (!c.InBounds(map)) return false;
            int index = map.cellIndices.CellToIndex(c);
            return coastBoundarySnapshot.TryGetValue(index, out TerrainDef old) && map.terrainGrid.BaseTerrainAt(c) != old;
        }

        private void CaptureBaseBoundary()
        {
            int w = map.Size.x, h = map.Size.z;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    if (x < 3 || z < 3 || x >= w - 3 || z >= h - 3)
                        coastBoundarySnapshot[z * w + x] = map.terrainGrid.BaseTerrainAt(new IntVec3(x, 0, z));
        }

        private void ApplyCoastBoundary(CoastNoise.Source coast)
        {
            if (coast == null && CoastFallback == null) return;
            int w = map.Size.x, h = map.Size.z;
            Func<TerrainDef, bool> sea = CoastNoise.SeaPredicate(map);
            var current = new bool[width * height];
            var predicted = new bool[current.Length];
            var valid = new bool[w * h];
            var natural = new TerrainDef[w * h];
            using (coast != null ? new CoastNoise.Scope(coast) : (IDisposable)null)
            {
                for (int z = 0; z < h; z++)
                    for (int x = 0; x < w; x++)
                    {
                        var c = new IntVec3(x, 0, z);
                        int j = z * w + x, i = Index(x, z);
                        TerrainDef t = natural[j] = map.terrainGrid.BaseTerrainAt(c);
                        current[i] = sea(t);
                        valid[j] = Usable(t) && !t.IsRiver && !CoastNoise.Ignorable(map, c);
                        bool boundary = x < 3 || z < 3 || x >= w - 3 || z >= h - 3;
                        predicted[i] = valid[j] && boundary ? coast != null ? CoastNoise.Value(coast, c) < CoastNoise.ShallowMax(coast.worker)
                            : CoastFallback.PredictedSea(c) : current[i];
                    }
            }
            var west = new bool[h]; var east = new bool[h]; var south = new bool[w]; var north = new bool[w];
            bool mismatch = false;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    int i = Index(x, z);
                    if (!valid[z * w + x] || current[i] == predicted[i]) continue;
                    if (x < 3) { west[z] = true; mismatch = true; }
                    if (x >= w - 3) { east[z] = true; mismatch = true; }
                    if (z < 3) { south[x] = true; mismatch = true; }
                    if (z >= h - 3) { north[x] = true; mismatch = true; }
                }
            if (!mismatch) return;
            for (int z = -r; z < h + r; z++)
                for (int x = -r; x < w + r; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (c.InBounds(map)) continue;
                    int i = Index(x, z);
                    predicted[i] = sea(cells[i]);
                    current[i] = sea(natural[Mirror(z, h) * w + Mirror(x, w)]);
                }
            float[] observedDistance = SignedCoast(current, width, height);
            float[] predictedDistance = SignedCoast(predicted, width, height);
            for (int z = -Math.Min(r, 13); z < h + Math.Min(r, 13); z++)
                for (int x = -Math.Min(r, 13); x < w + Math.Min(r, 13); x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (c.InBounds(map) || cells[Index(x, z)]?.IsRiver == true) continue;
                    if (River != null && River.TryPathAt(x, z, out _, out _)) continue;
                    float influence = 0f;
                    if (x < 0) influence = Math.Max(influence, CoastInfluence(west, z));
                    if (x >= w) influence = Math.Max(influence, CoastInfluence(east, z));
                    if (z < 0) influence = Math.Max(influence, CoastInfluence(south, x));
                    if (z >= h) influence = Math.Max(influence, CoastInfluence(north, x));
                    int distance = Math.Max(Math.Max(-x, x - w + 1), Math.Max(-z, z - h + 1));
                    int i = Index(x, z);
                    bool water = CorrectedSea(predictedDistance[i], observedDistance[i], distance, influence);
                    if (water == predicted[i]) continue;
                    IntVec3 nearest = Clamp(c);
                    int source = -1, best = int.MaxValue;
                    for (int zz = Math.Max(0, nearest.z - 16); zz <= Math.Min(h - 1, nearest.z + 16); zz++)
                        for (int xx = Math.Max(0, nearest.x - 16); xx <= Math.Min(w - 1, nearest.x + 16); xx++)
                        {
                            int j = zz * w + xx, d = (xx - nearest.x) * (xx - nearest.x) + (zz - nearest.z) * (zz - nearest.z);
                            if (valid[j] && sea(natural[j]) == water && d < best) { source = j; best = d; }
                        }
                    TerrainDef replacement = source >= 0 ? natural[source] : water ? MapGenUtility.ShallowOceanWaterTerrainAt(nearest, map) : Dominant(nearest);
                    if (replacement == null || sea(replacement) != water) continue;
                    cells[i] = replacement;
                    samples[i] = rawSamples[i] = source;
                    if (water && genRock != null)
                    {
                        genRock[i] = false; genRockDef[i] = null;
                        if (genRoofLevel != null) genRoofLevel[i] = 0;
                    }
                    CoastBoundaryCells++;
                }
        }

        internal static float[] SignedCoast(bool[] water, int width, int height)
        {
            float[] land = CompatSections.Distance(width, height, i => !water[i], 16f);
            float[] sea = CompatSections.Distance(width, height, i => water[i], 16f);
            for (int i = 0; i < land.Length; i++) land[i] = water[i] ? land[i] - 0.5f : 0.5f - sea[i];
            return land;
        }
        internal static float CoastInfluence(bool[] mismatch, int along)
        {
            float weight = 0f;
            for (int t = Math.Max(0, along - 4); t <= Math.Min(mismatch.Length - 1, along + 4); t++)
                if (mismatch[t]) weight = Math.Max(weight, 1f - Math.Abs(t - along) / 5f);
            return weight;
        }
        internal static bool CorrectedSea(float predicted, float observed, int distance, float influence)
        {
            if (distance >= 14 || influence <= 0f) return predicted > 0f;
            float t = Math.Clamp((distance - 1) / 13f, 0f, 1f);
            float weight = (1f - t * t * (3f - 2f * t)) * influence;
            return predicted + (observed - predicted) * weight > 0f;
        }
    }
}
