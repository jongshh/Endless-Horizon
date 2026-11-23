using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace EndlessHorizon
{
    internal static class GenerationBiomeTransition
    {
        internal static bool Root(TerrainPatchMaker p, TerrainDef output, float threshold)
        {
            if (p.thresholds.Count <= 1) return false;
            foreach (TerrainThreshold t in p.thresholds)
            {
                if (t.min >= threshold) return true;
                if (t.terrain == output) return false;
            }
            return false;
        }
        internal static bool Override(TerrainPatchMaker p, TerrainDef terrain, TerrainDef other, float threshold)
        {
            foreach (TerrainThreshold t in p.thresholds)
            {
                if (t.min >= threshold) return true;
                if (t.terrain == terrain) return false;
            }
            if (other == null) return true;
            if (terrain.passability != other.passability) return terrain.passability > other.passability;
            return terrain.pathCost > other.pathCost;
        }
        private static TerrainDef Possible(GenerationContext context, BiomeDef biome, IntVec3 c, out TerrainPatchMaker source)
        {
            foreach (TerrainPatchMaker maker in biome.terrainPatchMakers)
            {
                TerrainDef t = context.PatchAt(maker, c, maker.minFertility + 0.5f);
                if (t != null) { source = maker; return t; }
            }
            source = null; return null;
        }
        internal static IEnumerable<bool> Process(GenerationContext context, GenerationSnapshot snapshot, float root, float pass, GenerationRandom random)
        {
            for (int phase = 0; phase < 2; phase++)
            {
                int low = phase == 0 ? 0 : -snapshot.Ring, maxX = phase == 0 ? snapshot.Width : snapshot.Width + snapshot.Ring;
                for (int x = low; x < maxX; x++)
                {
                    using (random.Enter())
                    {
                        context.InitializingPatches = true;
                        try
                        {
                            int minZ = phase == 0 ? 0 : -snapshot.Ring, maxZ = phase == 0 ? snapshot.Height : snapshot.Height + snapshot.Ring;
                            for (int z = minZ; z < maxZ; z++)
                            {
                                bool inside = x >= 0 && z >= 0 && x < snapshot.Width && z < snapshot.Height;
                                if (phase == 1 && inside) continue;
                                var at = new IntVec3(x,0,z);
                                BiomeDef biome = snapshot.Biomes[snapshot.Index(x,z)];
                                TerrainDef output = Possible(context,biome,at,out TerrainPatchMaker maker);
                                if (output == null || !Root(maker,output,root)) continue;
                                var visited = new HashSet<(int,int)>();
                                var queue = new Queue<IntVec3>();
                                foreach (IntVec3 offset in GenAdj.CardinalDirections) queue.Enqueue(at+offset);
                                while (queue.Count > 0)
                                {
                                    IntVec3 c = queue.Dequeue();
                                    if (!snapshot.Covers(c.x,c.z) || !visited.Add((c.x,c.z))) continue;
                                    bool inMap = c.x >= 0 && c.z >= 0 && c.x < snapshot.Width && c.z < snapshot.Height;
                                    if ((phase == 0) != inMap) continue;
                                    int i = snapshot.Index(c.x,c.z);
                                    BiomeDef previous = snapshot.Biomes[i];
                                    if (previous == biome) continue;
                                    TerrainDef t = context.PatchAt(maker,c,maker.minFertility+0.5f);
                                    if (t == null || !Override(maker,t,Possible(context,previous,c,out _),pass)) continue;
                                    snapshot.Biomes[i] = biome;
                                    foreach (IntVec3 offset in GenAdj.CardinalDirections) queue.Enqueue(c+offset);
                                }
                            }
                        }
                        finally { context.InitializingPatches = false; }
                    }
                    yield return false;
                }
            }
        }
    }
}
