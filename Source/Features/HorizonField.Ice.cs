using System.Collections.Generic;
using RimWorld;
using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonField
    {
        private IceModule ice;
        private IceModule IceState => ice ??= new IceModule(domain, grid);
        public OutsideIce Ice => ice?.Calculator;
        public int FrozenCount => IceState.FrozenCount;
        public bool FrozenAt(IntVec3 c) => IceState.FrozenAt(c);
        public TerrainDef TopAt(IntVec3 c) => FloodAt(c) ?? RoadTopAt(c) ?? (FrozenAt(c) ? TerrainDefOf.ThinIce : At(c));
        internal List<IntVec3> SetFrozen(bool[] next) => IceState.SetFrozen(next);
        public void BuildIce() => IceState.Build(map, Mountains);
    }
}
