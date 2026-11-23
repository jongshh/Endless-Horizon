using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace EndlessHorizon
{
    internal sealed class EdgeRun
    {
        internal readonly List<IntVec3> Cells = new List<IntVec3>();
        internal Vector2 Centre, Normal, Tangent;
        internal int Width;

        internal Rot4 Side => Mathf.Abs(Normal.x) > Mathf.Abs(Normal.y) ? (Normal.x > 0f ? Rot4.East : Rot4.West)
            : Normal.y > 0f ? Rot4.North : Rot4.South;

        internal IntVec3 Cell(int t, int d)
        {
            Vector2 p = Centre + Tangent * t - Normal * d;
            return new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.y));
        }

        internal static List<EdgeRun> Find(HorizonDomain domain, Func<IntVec3, bool> wanted, int gap)
        {
            var open = new HashSet<IntVec3>();
            var order = new List<IntVec3>();
            foreach (int i in domain.Edge)
            {
                IntVec3 c = domain.Cell(i);
                if (wanted(c) && open.Add(c)) order.Add(c);
            }
            var runs = new List<EdgeRun>();
            int reach = gap + 1;
            foreach (IntVec3 first in order)
            {
                if (!open.Remove(first)) continue;
                var found = new List<IntVec3> { first };
                for (int k = 0; k < found.Count; k++)
                {
                    for (int dz = -reach; dz <= reach; dz++)
                    {
                        for (int dx = -reach; dx <= reach; dx++)
                        {
                            var next = new IntVec3(found[k].x + dx, 0, found[k].z + dz);
                            if (open.Remove(next)) found.Add(next);
                        }
                    }
                }
                runs.Add(Frame(domain, found));
            }
            return runs;
        }

        private static EdgeRun Frame(HorizonDomain domain, List<IntVec3> cells)
        {
            Vector2 out_ = Vector2.zero, sum = Vector2.zero;
            foreach (IntVec3 c in cells)
            {
                sum += new Vector2(c.x + 0.5f, c.z + 0.5f);
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 d = GenAdj.CardinalDirections[i];
                    if (!domain.Inside(c + d)) out_ += new Vector2(d.x, d.z);
                }
            }
            Vector2 middle = sum / cells.Count;
            if (out_.sqrMagnitude < 0.01f)
                out_ = middle - new Vector2(domain.W / 2f, domain.H / 2f);
            var run = new EdgeRun { Normal = out_.sqrMagnitude < 0.01f ? new Vector2(0f, -1f) : out_.normalized };
            run.Tangent = new Vector2(-run.Normal.y, run.Normal.x);
            float Along(IntVec3 c) => Vector2.Dot(new Vector2(c.x + 0.5f, c.z + 0.5f) - middle, run.Tangent);
            run.Cells.AddRange(cells.OrderBy(Along));
            float lo = Along(run.Cells[0]), hi = Along(run.Cells[run.Cells.Count - 1]);
            run.Centre = middle + run.Tangent * ((lo + hi) / 2f);
            run.Width = Mathf.RoundToInt(hi - lo) + 1;
            return run;
        }
    }
}
