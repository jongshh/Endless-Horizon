using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class SeasonWatch
    {
        private const int CheckInterval = 120;
        private readonly Map map;
        private readonly HorizonField field;
        private readonly List<int> cells = new List<int>();
        private int nextCheck;
        private long snowSig, thingSig, iceSig;

        public SeasonWatch(Map map, HorizonField field)
        {
            this.map = map;
            this.field = field;
            var rng = new System.Random(map.Tile.GetHashCode());
            int r = field.RingWidth;
            for (int i = 0; i < 600 && cells.Count < 200; i++)
            {
                int x = rng.Next(-r, field.Domain.W + r), z = rng.Next(-r, field.Domain.H + r);
                var c = new IntVec3(x, 0, z);
                if (field.Domain.Inside(c))
                {
                    continue;
                }
                int s = field.RawSampleAt(c);
                if (s >= 0)
                {
                    cells.Add(s);
                }
            }
            Compute(out snowSig, out thingSig);
            iceSig = field.Ice?.Signature() ?? 0;
            nextCheck = Find.TickManager.TicksGame + CheckInterval;
        }

        private void Compute(out long snow, out long things)
        {
            float depth = 0f;
            bool any = false;
            int leafless = 0, snowy = 0;
            foreach (int s in cells)
            {
                IntVec3 c = map.cellIndices.IndexToCell(s);
                float d = map.snowGrid.GetDepth(c);
                depth += d;
                any |= d > 0.001f;
                List<Thing> list = map.thingGrid.ThingsListAtFast(s);
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is Plant p)
                    {
                        if (p.LeaflessNow) leafless++;
                        if (d > 0.8f) snowy++;
                    }
                }
            }
            snow = (cells.Count == 0 ? 0 : Mathf.RoundToInt(depth / cells.Count * 50f)) * 2 + (any ? 1 : 0);
            things = ((long)leafless << 32) | (uint)snowy;
        }

        public void Check(bool force, out bool snowChanged, out bool thingsChanged, out bool iceChanged)
        {
            snowChanged = thingsChanged = iceChanged = false;
            int now = Find.TickManager.TicksGame;
            if (!force && now < nextCheck)
            {
                return;
            }
            nextCheck = now + CheckInterval;
            Compute(out long snow, out long things);
            long ice = field.Ice?.Signature() ?? 0;
            iceChanged = force || ice != iceSig;
            iceSig = ice;
            snowChanged = force || snow != snowSig;
            thingsChanged = force || things != thingSig;
            snowSig = snow;
            thingSig = things;
        }
    }
}
