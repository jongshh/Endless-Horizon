using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EndlessHorizon
{
    public static class HorizonPerf
    {
        private sealed class Entry
        {
            public double Last, Max, Total;
            public int Count;
        }

        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();

        public static void Record(string name, double ms)
        {
            if (!entries.TryGetValue(name, out Entry e))
            {
                e = new Entry();
                entries[name] = e;
            }
            e.Last = ms;
            if (ms > e.Max) e.Max = ms;
            e.Total += ms;
            e.Count++;
        }

        public static void Report(StringBuilder b)
        {
            if (entries.Count == 0)
            {
                b.AppendLine("(nothing measured yet)");
                return;
            }
            foreach (KeyValuePair<string, Entry> kv in entries.OrderBy(kv => kv.Key))
            {
                Entry e = kv.Value;
                b.AppendLine($"{kv.Key}: last={e.Last:0.0}ms max={e.Max:0.0}ms count={e.Count} total={e.Total:0}ms");
            }
        }
    }
}
