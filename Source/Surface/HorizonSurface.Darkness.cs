using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonSurface
    {

        internal float DarknessLight(int x, int z)
        {
            if (!Answers(new IntVec3(x, 0, z)) || DarknessFogged(x, z)) return 0f;
            float best = DarknessOpen(x, z);
            if (!DarknessBlocks(x, z)) return best;
            bool inside = Domain.Inside(x, z);
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = new IntVec3(x, 0, z) + GenAdj.AdjacentCells[i];
                if ((inside ? Domain.Inside(n) : Answers(n)) && !DarknessFogged(n.x, n.z) && !DarknessBlocks(n.x, n.z)) best = Mathf.Max(best, DarknessOpen(n.x, n.z));
            }
            return best;
        }

        private bool DarknessFogged(int x, int z)
        {
            var c = new IntVec3(x, 0, z);
            if (Domain.Inside(x, z)) return Map.fogGrid.IsFogged(c);
            return !Built || land.OutsideFogged(c);
        }

        private bool DarknessBlocks(int x, int z)
        {
            if (Domain.Inside(x, z)) return Map.edificeGrid.InnerArray[Domain.MapIndex(x, z)]?.def.blockLight == true;
            return OutsideRock(x, z);
        }

        private float DarknessOpen(int x, int z)
        {
            if (Domain.Inside(x, z)) return Map.glowGrid.GroundGlowAt(new IntVec3(x, 0, z), false, true);
            Color32 g = Glow(x, z);
            if (g.a == 1) return 1f;
            return Mathf.Min(0.5f, Mathf.Max(Mathf.Max(g.r, g.g), g.b) / 255f * 3.6f);
        }
    }
}
