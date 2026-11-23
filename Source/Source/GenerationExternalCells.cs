using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.Noise;

namespace EndlessHorizon
{
    internal sealed class GenerationExternalCells
    {
        private const int RoadShoulder = 3;
        private const int BankMargin = 2;
        private readonly Map map;
        private readonly bool[] owned;
        private readonly bool unresolvedWater;

        private GenerationExternalCells(Map map, bool[] owned, bool unresolvedWater)
        {
            this.map = map; this.owned = owned; this.unresolvedWater = unresolvedWater;
        }

        internal static GenerationExternalCells For(Map map, GenReconstruction generator)
        {
            int w = map.Size.x, h = map.Size.z;
            var river = new List<int>();
            var road = new List<int>();
            bool roads = map.roadInfo != null && map.roadInfo.roadEdgeTiles.Count > 0;
            bool rivers = !generator.OwnsTerrain && generator.Mutators?.RiverReplayed != true;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (rivers && map.terrainGrid.BaseTerrainAt(c)?.IsRiver == true) river.Add(z * w + x);
                    if (roads && map.terrainGrid.TerrainAt(c)?.HasTag("Road") == true) road.Add(z * w + x);
                }
            var owned = new bool[w * h];
            int bank = Math.Clamp(map.Biome.riverbankSizeRange.max, 1, 8) + BankMargin;
            Spread(owned, river, w, h, bank);
            Spread(owned, road, w, h, RoadShoulder);
            try { FrozenRuinsIce(map, owned, w, h); }
            catch (Exception ex)
            {
                Log.ErrorOnce(HorizonLog.Prefix + "The frozen ruins' ice patch could not be marked; its cells count as misses. " + ex, 0x45484952);
            }
            return new GenerationExternalCells(map, owned, generator.Mutators?.UnresolvedWater == true);
        }

        internal static int FrozenRuinsIce(Map map, bool[] owned, int w, int h)
        {
            LayoutDef glacier = DefDatabase<LayoutDef>.GetNamedSilentFail("AncientRuinsGlacier");
            if (glacier == null || map.layoutStructureSketches == null) return 0;
            int marked = 0;
            GenMorphology.GenPatchParms parms = GenMorphology.GenPatchParms.For(ThingDefOf.SolidIce, TerrainDefOf.Ice);
            float threshold = Math.Min(parms.terrainThreshold, Math.Min(parms.edificeThreshold, parms.roofThreshold));
            foreach (LayoutStructureSketch sketch in map.layoutStructureSketches)
            {
                if (sketch?.layoutDef != glacier || sketch.structureLayout == null) continue;
                List<CellRect> rects = sketch.structureLayout.Rooms.SelectMany(room => room.rects).Select(r => r.ExpandedBy(24)).ToList();
                if (rects.Count == 0) continue;
                ModuleBase noise = PatchNoise(map.ConstantRandSeed, rects, parms);
                foreach (CellRect rect in rects)
                    foreach (IntVec3 c in rect.Cells)
                    {
                        if (c.x < 0 || c.z < 0 || c.x >= w || c.z >= h) continue;
                        int i = c.z * w + c.x;
                        if (owned[i] || noise.GetValue(c) < threshold) continue;
                        owned[i] = true; marked++;
                    }
            }
            return marked;
        }

        internal static ModuleBase PatchNoise(int seed, List<CellRect> rects, GenMorphology.GenPatchParms parms)
        {
            var lhs = new Perlin(parms.macroFrequency, 1.25, 0.5, 6, seed, QualityMode.Medium);
            ModuleBase rhs = new Perlin(parms.microFrequency, 2.0, 0.5, 6, seed + 1, QualityMode.Medium);
            ModuleBase input = new Blend(lhs, rhs, new Const(parms.blendMacroMicro));
            input = new ScaleBias(0.5, 0.6000000238418579, input);
            ModuleBase falloff = new DistFromPointRects(rects);
            falloff = new Clamp(0.0, 1.0, falloff);
            falloff = new ScaleBias(2.200000047683716, -0.5, falloff);
            return new Blend(falloff, input, new Const(parms.blendFalloff));
        }

        internal static void Spread(bool[] owned, List<int> seeds, int w, int h, int radius)
        {
            var seen = new bool[owned.Length];
            var front = new List<int>();
            foreach (int i in seeds) { seen[i] = owned[i] = true; front.Add(i); }
            for (int step = 0; step < radius && front.Count > 0; step++)
            {
                var next = new List<int>();
                foreach (int i in front)
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = i % w + dx, z = i / w + dz;
                            if (x < 0 || z < 0 || x >= w || z >= h) continue;
                            int j = z * w + x;
                            if (seen[j]) continue;
                            seen[j] = owned[j] = true; next.Add(j);
                        }
                front = next;
            }
        }

        internal bool Contains(IntVec3 c, TerrainDef actual, TerrainDef predicted, bool actualRock, bool expectedRock)
        {
            if (owned[c.z * map.Size.x + c.x]) return true;
            if (unresolvedWater && actual != null && actual.IsWater && !actual.IsOcean && predicted?.IsWater != true) return true;
            return expectedRock && !actualRock && c.GetRoof(map)?.isNatural == true;
        }
    }
}
