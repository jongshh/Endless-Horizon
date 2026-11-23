using Verse;

namespace EndlessHorizon
{
    internal sealed class LandGrid
    {
        internal readonly int R, Width, Height;
        internal readonly TerrainDef[] Cells;
        internal readonly int[] Samples;
        internal readonly int[] RawSamples;
        internal bool[] GenRock;
        internal ThingDef[] GenRockDef;
        internal byte[] GenRoofLevel;

        internal LandGrid(HorizonDomain domain)
        {
            R = domain.R;
            Width = domain.Stride;
            Height = domain.Rows;
            Cells = new TerrainDef[Width * Height];
            Samples = new int[Width * Height];
            RawSamples = new int[Width * Height];
            for (int i = 0; i < Samples.Length; i++) Samples[i] = RawSamples[i] = -1;
        }

        internal int Index(int x, int z) => (z + R) * Width + (x + R);
    }
}
