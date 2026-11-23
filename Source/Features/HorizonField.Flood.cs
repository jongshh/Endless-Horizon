using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonField
    {
        private FloodModule flood;
        private FloodModule FloodState => flood ??= new FloodModule(domain, grid);
        internal FloodModule Flood => FloodState;
        public TerrainDef FloodAt(IntVec3 c) => FloodState.FloodAt(c);
        public void BuildFlood() => FloodState.Build(map, Mountains, RoadsBuilt);
    }
}
