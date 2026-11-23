using System;
using System.Collections.Generic;
using Verse;

namespace EndlessHorizon
{
    internal sealed partial class HorizonField
    {
        private PollutionModule pollution;

        private PollutionModule Pollution => pollution ??= new PollutionModule(map, domain, grid);

        private Func<IntVec3, int> MirrorOf => Gen.MirrorOf;

        private IEnumerable<bool> ReadPollutionSteps() => Pollution.ReadSteps();

        internal bool PollutedAt(IntVec3 c) => Pollution.GroundPollutedAt(c) && Mountains?.IsRock(c.x, c.z) != true;

        private IEnumerable<bool> BuildPollution() => Pollution.BuildSteps(MirrorOf);

    }
}
