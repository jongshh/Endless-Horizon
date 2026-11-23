using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonField
    {
        private FogModule fog;

        private FogModule Fog => fog ??= new FogModule(map, domain, grid, () => Mountains, EdgeState);

        public bool OutsideFogAt(IntVec3 c) => Fog.FogAt(c);

        internal bool OutsideFogged(IntVec3 c) => Fog.Fogged(c);

        internal byte EdgeState(int x, int z)
        {
            var edge = new IntVec3(x, 0, z);
            Building b = edge.GetEdifice(map);
            return (byte)((map.fogGrid.IsFogged(edge) ? 1 : 0) | (b != null && b.def.mineable ? 2 : 0));
        }

        internal int FogWithoutMountain() => Fog.FogWithoutMountain();
    }
}
