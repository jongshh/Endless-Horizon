using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace EndlessHorizon
{
    internal sealed class RoadBuild
    {
        private readonly Map map;
        private readonly HorizonDomain domain;
        private readonly LandGrid grid;
        private TerrainDef[] roadTop;
        private bool[] roadCleared;
        private Dictionary<IntVec3, byte> tunnel;
        private Dictionary<IntVec3, ThingDef> buildings;

        internal int RoadCells, BridgeCells, RoadCutCells;
        internal IntVec3 FirstBridge = IntVec3.Invalid;

        internal RoadBuild(Map map, HorizonDomain domain, LandGrid grid)
        {
            this.map = map;
            this.domain = domain;
            this.grid = grid;
        }

        internal int TunnelCells => tunnel?.Count ?? 0;

        internal int BuildingCells => buildings?.Count ?? 0;

        internal ThingDef BuildingAt(IntVec3 c) => buildings != null && buildings.TryGetValue(c, out ThingDef def) ? def : null;

        internal TerrainDef RoadTopAt(IntVec3 c) => roadTop != null && domain.Outside(c) ? roadTop[grid.Index(c.x, c.z)] : null;

        internal byte TunnelRoof(IntVec3 c) => tunnel != null && tunnel.TryGetValue(c, out byte roof) ? roof : (byte)0;

        internal bool InTunnel(IntVec3 c) => TunnelRoof(c) > 0;

        internal bool RoadCleared(IntVec3 c) => roadCleared != null && domain.InGrid(c) && roadCleared[grid.Index(c.x, c.z)];

        internal IEnumerable<bool> BuildSteps(RoadExtension roads, Func<int, int, bool> rockBefore, Func<IntVec3, byte> roofBefore,
                                              Func<IntVec3, TerrainDef, int> replacedSource, Action tunnelsRead)
        {
            if (roads == null || !roads.Any) yield break;
            TerrainDef[] cells = grid.Cells;
            var built = new List<RoadSurface>();
            using (IEnumerator<bool> work = roads.BuildSteps((x, z) => cells[grid.Index(x, z)], rockBefore, built).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        more = work.MoveNext();
                        HorizonPerf.Record("roads unit", (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(HorizonLog.Prefix + "Continuing roads failed; the outside is built without them. " + ex);
                        roads.Notes.Add("failed: " + ex.GetType().Name + ": " + ex.Message);
                        yield break;
                    }
                    if (!more) break;
                    yield return false;
                }
            }
            if (built.Count == 0) yield break;
            RoadSurface surface = built[0];
            long tunnelStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            FindTunnels(roads, surface, roofBefore, tunnelsRead);
            HorizonPerf.Record("road tunnels", (System.Diagnostics.Stopwatch.GetTimestamp() - tunnelStarted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            yield return false;
            roadTop = new TerrainDef[grid.Width * grid.Height];
            roadCleared = new bool[grid.Width * grid.Height];
            for (int n = 0; n < surface.Changed.Count; n++)
            {
                int x = surface.X(surface.Changed[n]), z = surface.Z(surface.Changed[n]);
                WriteRoadCell(surface, x, z, rockBefore, replacedSource);
                if (n % 64 == 63) yield return false;
            }
            yield return false;
            foreach ((int x, int z, ThingDef def) in surface.Things)
            {
                var c = new IntVec3(x, 0, z);
                if (!domain.Outside(c)) continue;
                buildings ??= new Dictionary<IntVec3, ThingDef>();
                buildings[c] = def;
            }
            int done = 0;
            foreach (IntVec3 c in roads.OpenSky)
            {
                OpenToSky(c, rockBefore, replacedSource);
                if (++done % 64 == 0) yield return false;
            }
            if (tunnel == null) yield break;
            foreach (IntVec3 c in tunnel.Keys)
            {
                int i = grid.Index(c.x, c.z);
                if (roadCleared[i] || !rockBefore(c.x, c.z)) grid.Samples[i] = -1;
                if (++done % 64 == 0) yield return false;
            }
        }

        private void FindTunnels(RoadExtension roads, RoadSurface surface, Func<IntVec3, byte> roofBefore, Action tunnelsRead)
        {
            try
            {
                List<IntVec3> exits = roads.Crossings.SelectMany(c => c.RoofedExit).ToList();
                if (exits.Count == 0) return;
                var band = new HashSet<IntVec3>(roads.OpenSky);
                foreach (int changed in surface.Changed)
                {
                    int x = surface.X(changed), z = surface.Z(changed);
                    if (surface.ClearedAt(x, z)) band.Add(new IntVec3(x, 0, z));
                }
                foreach (RoadCrossing crossing in roads.Crossings)
                    foreach (IntVec3 exit in crossing.RoofedExit)
                        for (int d = 0; d < 4; d++)
                        {
                            IntVec3 ahead = exit + GenAdj.CardinalDirections[d];
                            if (roofBefore(ahead) > 0) crossing.CoveredAhead.Add(ahead);
                        }
                Dictionary<IntVec3, byte> found = RoadExtension.Tunnel(exits, band, roofBefore);
                if (found.Count > 0) tunnel = found;
            }
            catch (Exception ex)
            {
                tunnel = null;
                Log.Error(HorizonLog.Prefix + "Continuing road tunnels failed; those roads are open cuts. " + ex);
                roads.Notes.Add("tunnel failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                tunnelsRead();
            }
        }

        private void OpenToSky(IntVec3 c, Func<int, int, bool> rockBefore, Func<IntVec3, TerrainDef, int> replacedSource)
        {
            int i = grid.Index(c.x, c.z);
            if (!roadCleared[i] && rockBefore(c.x, c.z)) return;
            int raw = grid.RawSamples[i];
            if (raw < 0 || map.roofGrid.RoofAt(raw) == null) return;
            grid.RawSamples[i] = grid.Cells[i] != null ? replacedSource(c, grid.Cells[i]) : -1;
            if (roadTop[i] == null && grid.Samples[i] == raw) grid.Samples[i] = grid.RawSamples[i];
        }

        private void WriteRoadCell(RoadSurface surface, int x, int z, Func<int, int, bool> rockBefore, Func<IntVec3, TerrainDef, int> replacedSource)
        {
            var c = new IntVec3(x, 0, z);
            int i = grid.Index(x, z);
            TerrainDef old = grid.Cells[i];
            TerrainDef seen = surface.TerrainAt(x, z);
            TerrainDef ground = surface.BaseAt(x, z);
            bool bridge = surface.FoundationAt(x, z)?.bridge == true;
            if (bridge && old?.IsWater == true) ground = old;
            bool changed = ground != old || seen != ground;
            grid.Cells[i] = ground;
            if (seen != ground) roadTop[i] = seen;
            if (changed) RoadCells++;
            if (bridge && BridgeCells++ == 0) FirstBridge = c;

            bool cut = surface.ClearedAt(x, z);
            if (cut)
            {
                roadCleared[i] = true;
                if (rockBefore(x, z)) RoadCutCells++;
                if (grid.GenRock != null) grid.GenRock[i] = false;
                if (grid.GenRoofLevel != null && !InTunnel(c)) grid.GenRoofLevel[i] = 0;
                int raw = grid.RawSamples[i];
                if (raw < 0 || map.roofGrid.RoofAt(raw) != null || map.edificeGrid.InnerArray[raw] != null)
                    grid.RawSamples[i] = old != null ? replacedSource(c, old) : -1;
            }
            if (seen != null && (seen.bridge || seen.HasTag("Road") || changed && seen.fertility <= 0f)) grid.Samples[i] = -1;
            else if (changed || cut && grid.Samples[i] < 0) grid.Samples[i] = replacedSource(c, ground);
        }
    }
}
