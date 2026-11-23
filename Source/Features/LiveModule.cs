using System;
using System.Collections.Generic;
using Verse;

namespace EndlessHorizon
{
    internal interface ILiveModule
    {
        string Name { get; }

        string OnEdit(IntVec3 cell, bool surfaceChanged);

        string RebuildReason(bool rebuildRequested);

        bool Due();

        bool Immediate { get; }

        IEnumerable<bool> UpdateSteps(List<IntVec3> changed);

        bool ChangesWater { get; }

        void Announce(CellRect changed, Action<string, CellRect> publish);

        bool Busy { get; }
    }
}
