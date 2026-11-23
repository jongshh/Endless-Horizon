using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonField
    {
        private readonly Map map;
        private readonly int r;
        private readonly HorizonDomain domain;
        public int RingWidth => r;
        internal HorizonDomain Domain => domain;
        private readonly int width, height;
        private readonly LandGrid grid;
        internal LandGrid Land => grid;
        private TerrainDef[] cells => grid.Cells;
        private int[] samples => grid.Samples;
        private int[] rawSamples => grid.RawSamples;
        private bool[] genRock { get => grid.GenRock; set => grid.GenRock = value; }
        private ThingDef[] genRockDef { get => grid.GenRockDef; set => grid.GenRockDef = value; }
        private byte[] genRoofLevel { get => grid.GenRoofLevel; set => grid.GenRoofLevel = value; }

        public RiverExtension River;
        private readonly bool[] fogSnapshot;

        public HorizonField(Map map, HorizonDomain domain)
        {
            this.map = map;
            this.domain = domain;
            r = domain.R;
            width = domain.Stride;
            height = domain.Rows;
            grid = new LandGrid(domain);
            fogSnapshot = new bool[map.cellIndices.NumGridCells];
            for (int i = 0; i < fogSnapshot.Length; i++)
            {
                fogSnapshot[i] = map.fogGrid.IsFogged(i);
            }
        }

        private int Index(int x, int z) => grid.Index(x, z);

        private CoastSource coastSource;
        private CoastSource Coast { get => coastSource ??= new CoastSource(); set => coastSource = value; }
        public string CoastOrigin => Coast.Origin;
        public CoastSearch PendingCoastSearch { get => Coast.Pending; set => Coast.Pending = value; }
        public CoastFallback CoastFallback => Coast.Fallback;
        private CoastBoundaryModule coastEdge;
        private CoastBoundaryModule CoastEdge => coastEdge ??= new CoastBoundaryModule(map, domain, grid, Gen);
        public int CoastBoundaryCells => CoastEdge.CoastBoundaryCells;
        private void CaptureBaseBoundary() => CoastEdge.CaptureBaseBoundary();
        private void ApplyCoastBoundary(CoastNoise.Source coast) => CoastEdge.Apply(coast, CoastFallback, River);
        internal static float[] SignedCoast(bool[] water, int width, int height) => CoastBoundaryModule.SignedCoast(water, width, height);
        internal static float CoastInfluence(bool[] mismatch, int along) => CoastBoundaryModule.CoastInfluence(mismatch, along);
        internal static bool CorrectedSea(float predicted, float observed, int distance, float influence) =>
            CoastBoundaryModule.CorrectedSea(predicted, observed, distance, influence);

        private ILiveModule[] live;
        internal IReadOnlyList<ILiveModule> Live => live ??= new ILiveModule[] { Pollution, CoastEdge, Fog, IceState, FloodState };
        internal void RequestIce() => IceState.Request();

        internal MountainMask MakeMountains() => new MountainMask(map, domain, grid, Gen, RoadsBuilt, EdgeState);

        internal OutsideGlow MakeGlow(MountainMask mountains, float maxThingDist, OutsideGlow previous = null) =>
            new OutsideGlow(map, domain, grid, Fog, mountains, maxThingDist, previous);

        private RoofModule roof;
        private RoofModule Roof => roof ??= new RoofModule(map, domain, grid, Gen, RoadsBuilt, () => Mountains);
        public bool OutsideThickRoof(IntVec3 c) => Roof.OutsideThickRoof(c);
        internal byte OutsideRoofAt(IntVec3 c) => Roof.OutsideRoofAt(c);

        private GeneratorModule gen;
        internal GeneratorModule Gen => gen ??= new GeneratorModule(map, domain, grid, Pollution, () => Roads);
        public GenReconstruction Generator { get => Gen.Generator; set => Gen.Generator = value; }
        public string OutsideMode => Gen.OutsideMode;
        public string RiverPaintReport => Gen.RiverPaintReport;
        public string CoastMode => Gen.CoastMode;
        public int CoastCells => Gen.CoastCells;
        public int RiverCells => Gen.RiverCells;
        public int WildSubstitutions => Gen.WildSubstitutions;
        public MountainMask Mountains;
        public bool GenRock(IntVec3 c) => Gen.GenRock(c);
        public ThingDef GenRockDef(IntVec3 c) => Gen.GenRockDef(c);
        internal int ReplacedSource(IntVec3 c, TerrainDef t) => Gen.ReplacedSource(c, t);
        private int MirrorIndex(IntVec3 c) => Gen.MirrorIndex(c);
        private void ChooseGenerator() => Gen.ChooseGenerator();
        private TerrainDef Dominant(IntVec3 edgeCell) => Gen.Dominant(edgeCell);
        private static bool Usable(TerrainDef t) => GeneratorModule.Usable(t);
        private IntVec3 Clamp(IntVec3 c) => domain.Nearest(c);

        public bool Covers(IntVec3 c) => domain.InGrid(c);

        public TerrainDef At(IntVec3 c) => Covers(c) ? cells[Index(c.x, c.z)] : null;

        public bool AnyWaterIn(CellRect rect)
        {
            foreach (IntVec3 c in rect)
            {
                TerrainDef t = domain.Drawn(c) ? At(c) : null;
                if (t != null && t.IsWater || domain.Drawn(c) && FloodAt(c)?.IsWater == true)
                {
                    return true;
                }
            }
            return false;
        }

        public void Build(bool useCoast, CoastNoise.Source overrideSource)
        {
            foreach (bool step in BuildSteps(useCoast, overrideSource)) { }
        }

        internal IEnumerable<bool> BuildSteps(bool useCoast, CoastNoise.Source overrideSource)
        {
            Coast = CoastSource.Resolve(map, useCoast, overrideSource);
            CoastNoise.Source coast = Coast.Source;
            bool delta = map.TileInfo?.Mutators?.Any(m => m.defName.Contains("Delta")) ?? false;
            try
            {
                River = new RiverExtension(map, domain);
                River.Build(delta);
            }
            catch (Exception ex)
            {
                River = null;
                Log.Error(HorizonLog.Prefix + "River extension failed: " + ex);
            }
            yield return false;
            FindRoads();
            yield return false;
            if (HorizonSettings.Current.useGenerator && !HorizonDebug.ForceMirror)
                foreach (bool step in GenReconstruction.Prepare(map)) yield return step;
            ChooseGenerator();
            yield return false;
            foreach (bool step in ReadPollutionSteps()) yield return step;
            foreach (bool step in Gen.FillSteps(coast, CoastFallback, River)) yield return step;
            yield return false;
            ApplyCoastBoundary(coast);
            yield return false;
            CaptureBaseBoundary();
            foreach (bool step in BuildRoads()) yield return step;
            foreach (bool step in BuildPollution()) yield return step;
        }

        public int SampleAt(IntVec3 c) => Covers(c) ? samples[Index(c.x, c.z)] : -1;

        public bool SourceFogged(int index) => index >= 0 && index < fogSnapshot.Length && fogSnapshot[index];

        public int RawSampleAt(IntVec3 c) => Covers(c) ? rawSamples[Index(c.x, c.z)] : -1;

    }
}
