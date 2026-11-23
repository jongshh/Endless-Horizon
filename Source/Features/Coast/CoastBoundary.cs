using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace EndlessHorizon
{
    internal sealed class CoastBoundaryModule : ILiveModule
    {
        private readonly Map map;
        private readonly HorizonDomain domain;
        private readonly LandGrid grid;
        private readonly GeneratorModule gen;
        private readonly int r, width, height;
        private readonly Dictionary<int, TerrainDef> coastBoundarySnapshot = new Dictionary<int, TerrainDef>();
        public int CoastBoundaryCells { get; private set; }

        internal CoastBoundaryModule(Map map, HorizonDomain domain, LandGrid grid, GeneratorModule gen)
        {
            this.map = map;
            this.domain = domain;
            this.grid = grid;
            this.gen = gen;
            r = grid.R;
            width = grid.Width;
            height = grid.Height;
        }

        private TerrainDef[] cells => grid.Cells;
        private int[] samples => grid.Samples;
        private int[] rawSamples => grid.RawSamples;
        private bool[] genRock => grid.GenRock;
        private ThingDef[] genRockDef => grid.GenRockDef;
        private byte[] genRoofLevel => grid.GenRoofLevel;
        private int Index(int x, int z) => grid.Index(x, z);
        private GenReconstruction Generator => gen.Generator;
        private static bool Usable(TerrainDef t) => GeneratorModule.Usable(t);
        private TerrainDef Dominant(IntVec3 edgeCell) => gen.Dominant(edgeCell);
        private IntVec3 Clamp(IntVec3 c) => domain.Nearest(c);

        internal bool BoundaryBaseChanged(IntVec3 c)
        {
            if (!domain.Inside(c)) return false;
            int index = map.cellIndices.CellToIndex(c);
            return coastBoundarySnapshot.TryGetValue(index, out TerrainDef old) && map.terrainGrid.BaseTerrainAt(c) != old;
        }

        internal void CaptureBaseBoundary()
        {
            int w = domain.W, h = domain.H;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    if (domain.InBand(x, z, 3))
                        coastBoundarySnapshot[z * w + x] = map.terrainGrid.BaseTerrainAt(new IntVec3(x, 0, z));
        }

        internal void Apply(CoastNoise.Source coast, CoastFallback CoastFallback, RiverExtension River)
        {
            TerrainDef[] generated = Generator?.OwnsCoast == true ? Generator.Predicted : null;
            if (generated == null && coast == null && CoastFallback == null) return;
            if (generated != null && Generator.Mutators?.OwnsCoast != true && !OceanAtEdge(generated)) return;
            int w = domain.W, h = domain.H;
            Func<TerrainDef, bool> sea = CoastNoise.SeaPredicate(map);
            var current = new bool[width * height];
            var predicted = new bool[current.Length];
            var valid = new bool[w * h];
            var natural = new TerrainDef[w * h];
            using (generated == null && coast != null ? new CoastNoise.Scope(coast) : (IDisposable)null)
            {
                for (int z = 0; z < h; z++)
                    for (int x = 0; x < w; x++)
                    {
                        var c = new IntVec3(x, 0, z);
                        int j = z * w + x, i = Index(x, z);
                        if (!domain.Inside(x, z)) continue;
                        TerrainDef t = natural[j] = map.terrainGrid.BaseTerrainAt(c);
                        current[i] = sea(t);
                        valid[j] = Usable(t) && !t.IsRiver && !CoastNoise.Ignorable(map, c);
                        bool boundary = domain.InBand(x, z, 3);
                        predicted[i] = !valid[j] || !boundary ? current[i]
                            : generated != null ? sea(generated[j])
                            : coast != null ? CoastNoise.Value(coast, c) < CoastNoise.ShallowMax(coast.worker)
                            : CoastFallback.PredictedSea(c);
                    }
            }
            var west = new bool[h]; var east = new bool[h]; var south = new bool[w]; var north = new bool[w];
            bool mismatch = false;
            bool[] wrong = domain.Rectangle ? null : new bool[w * h];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    int i = Index(x, z);
                    if (!valid[z * w + x] || current[i] == predicted[i]) continue;
                    if (wrong != null)
                    {
                        if (domain.InBand(x, z, 3)) { wrong[z * w + x] = true; mismatch = true; }
                        continue;
                    }
                    if (x < 3) { west[z] = true; mismatch = true; }
                    if (x >= w - 3) { east[z] = true; mismatch = true; }
                    if (z < 3) { south[x] = true; mismatch = true; }
                    if (z >= h - 3) { north[x] = true; mismatch = true; }
                }
            if (!mismatch) return;
            for (int z = -r; z < h + r; z++)
                for (int x = -r; x < w + r; x++)
                {
                    if (domain.Inside(x, z)) continue;
                    int i = Index(x, z);
                    predicted[i] = sea(cells[i]);
                    domain.Reflect(x, z, out int sx, out int sz);
                    current[i] = sea(natural[sz * w + sx]);
                }
            float[] observedDistance = SignedCoast(current, width, height);
            float[] predictedDistance = SignedCoast(predicted, width, height);
            for (int z = -Math.Min(r, 13); z < h + Math.Min(r, 13); z++)
                for (int x = -Math.Min(r, 13); x < w + Math.Min(r, 13); x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (domain.Inside(x, z) || cells[Index(x, z)]?.IsRiver == true) continue;
                    if (River != null && River.TryPathAt(x, z, out _, out _)) continue;
                    float influence = 0f;
                    if (wrong != null) influence = RegionInfluence(wrong, x, z);
                    else if (x < 0) influence = Math.Max(influence, CoastInfluence(west, z));
                    if (wrong == null)
                    {
                        if (x >= w) influence = Math.Max(influence, CoastInfluence(east, z));
                        if (z < 0) influence = Math.Max(influence, CoastInfluence(south, x));
                        if (z >= h) influence = Math.Max(influence, CoastInfluence(north, x));
                    }
                    int distance = domain.Steps(x, z);
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

        private bool OceanAtEdge(TerrainDef[] generated)
        {
            int w = domain.W, h = domain.H;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    if (!domain.InBand(x, z, 3)) continue;
                    if (generated[z * w + x]?.IsOcean == true || map.terrainGrid.BaseTerrainAt(new IntVec3(x, 0, z))?.IsOcean == true) return true;
                }
            return false;
        }

        private float RegionInfluence(bool[] wrong, int x, int z)
        {
            domain.Nearest(x, z, out int nx, out int nz);
            float weight = 0f;
            for (int dz = -6; dz <= 6; dz++)
                for (int dx = -6; dx <= 6; dx++)
                {
                    int xx = nx + dx, zz = nz + dz;
                    if (!domain.InMap(xx, zz) || !wrong[domain.MapIndex(xx, zz)]) continue;
                    int along = Math.Max(0, Math.Max(Math.Abs(dx), Math.Abs(dz)) - domain.Depth(xx, zz));
                    if (along <= 4) weight = Math.Max(weight, 1f - along / 5f);
                }
            return weight;
        }

        internal static float[] SignedCoast(bool[] water, int width, int height)
        {
            float[] land = DistanceField.Chamfer(width, height, i => !water[i], 16f);
            float[] sea = DistanceField.Chamfer(width, height, i => water[i], 16f);
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

        string ILiveModule.Name => "coast boundary";
        string ILiveModule.OnEdit(IntVec3 cell, bool surfaceChanged) => surfaceChanged && BoundaryBaseChanged(cell) ? "boundary base terrain" : null;
        string ILiveModule.RebuildReason(bool rebuildRequested) => null;
        bool ILiveModule.Due() => false;
        bool ILiveModule.Immediate => false;
        IEnumerable<bool> ILiveModule.UpdateSteps(List<IntVec3> changed) { yield break; }
        bool ILiveModule.ChangesWater => false;
        void ILiveModule.Announce(CellRect changed, Action<string, CellRect> publish) { }
        bool ILiveModule.Busy => false;
    }
}
