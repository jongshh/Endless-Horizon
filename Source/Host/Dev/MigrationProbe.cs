#if EH_DEV
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal static class MigrationProbe
    {
        internal static List<string> Lines(HorizonState s)
        {
            var found = new Dictionary<string, List<IntVec3>>();
            void Add(string name, IntVec3 c)
            {
                if (!found.TryGetValue(name, out List<IntVec3> cells)) found[name] = cells = new List<IntVec3>();
                cells.Add(c);
            }
            HorizonDomain d = s.Domain;
            int zone = Mathf.Clamp(s.FogStart + (s.RingWidth - s.FogStart) / 2, 0, s.RingWidth);
            for (int z = -d.R; z < d.H + d.R; z++)
            {
                for (int x = -d.R; x < d.W + d.R; x++)
                {
                    if (d.Inside(x, z)) continue;
                    var c = new IntVec3(x, 0, z);
                    bool was = s.Field.OutsideThickRoof(c) || s.Field.InTunnel(c), now = s.Surface.Roof(c) != null;
                    bool inZone = d.Steps(x, z) <= zone;
                    if (was != now)
                    {
                        Add(now ? "R2.livingwaters.roofed+" : "R2.livingwaters.roofed-", c);
                        if (inZone && !s.Surface.OutsideRock(x, z)) Add("R2.quietwonders.roofed", c);
                        if (HorizonSurface.KindOf(s.Surface.Ground(c)) != HorizonSurface.Land && !s.Surface.OutsideRock(x, z))
                            Add("R2.livingwaters.roofedWater", c);
                    }
                    if (inZone && s.Field.OutsideFogAt(c) != s.Surface.Fogged(c)) Add("R3.quietwonders.fogged", c);
                    if (now && !s.Surface.RoofShades(c) && !s.Surface.OutsideRock(x, z)) Add("roof.openButNotShaded", c);
                }
            }
            var lines = new List<string>();
            foreach (KeyValuePair<string, List<IntVec3>> entry in found.OrderBy(e => e.Key))
            {
                var text = new StringBuilder($"EH_MIGRATION {entry.Key} count={entry.Value.Count}");
                text.Append($" x={entry.Value.Min(c => c.x)}..{entry.Value.Max(c => c.x)} z={entry.Value.Min(c => c.z)}..{entry.Value.Max(c => c.z)} rows=");
                int written = 0;
                List<IntVec3> cells = entry.Value;
                for (int i = 0; i < cells.Count; i++)
                {
                    int from = i;
                    while (i + 1 < cells.Count && cells[i + 1].z == cells[i].z && cells[i + 1].x == cells[i].x + 1) i++;
                    text.Append($"{cells[from].z}:{cells[from].x}-{cells[i].x},");
                    if (++written % 250 != 0) continue;
                    lines.Add(text.ToString());
                    text = new StringBuilder($"EH_MIGRATION {entry.Key} more=");
                }
                lines.Add(text.ToString());
            }
            if (lines.Count == 0) lines.Add("EH_MIGRATION none");
            return lines;
        }
    }
}
#endif
