using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Verse;

namespace EndlessHorizon
{
    internal static class ExtensionMaps
    {
        private sealed class PerMap
        {
            internal int All;
            internal readonly Dictionary<string, int> Revisions = new Dictionary<string, int>(StringComparer.Ordinal);
            internal CellRect View = CellRect.Empty;
            internal string ExternalOwner;
            internal ExtensionRing Ring;
        }

        private static readonly ConditionalWeakTable<Map, PerMap> maps = new ConditionalWeakTable<Map, PerMap>();

        private static PerMap Of(Map map) => maps.GetValue(map, _ => new PerMap());

        private static PerMap Known(Map map) => map != null && maps.TryGetValue(map, out PerMap found) ? found : null;

        internal static Func<Map, CellRect, bool> DrawMap;
        internal static Action<Map> RegionChanged;

        internal static void SetRing(Map map, ExtensionRing ring) => Of(map).Ring = ring;

        internal static void ClearRing(Map map, ExtensionRing ring)
        {
            PerMap per = Known(map);
            if (per?.Ring == ring && per != null) per.Ring = null;
        }

        internal static ExtensionRing RingOf(Map map) => Known(map)?.Ring;

        internal static void SetView(Map map, CellRect view) => Of(map).View = view;

        internal static CellRect ViewOf(Map map) => Known(map)?.View ?? CellRect.Empty;

        internal static Map DrawingMap;

        internal static string ExternalOwner(Map map) => Known(map)?.ExternalOwner;

        internal static bool SetExternal(Map map, string owner, bool external)
        {
            PerMap per = Of(map);
            if (per.ExternalOwner != null && per.ExternalOwner != owner) return false;
            per.ExternalOwner = external ? owner : null;
            return true;
        }

        private sealed class Observer : IDisposable
        {
            internal string Owner;
            internal Action<Map, string, CellRect> Changed;

            public void Dispose()
            {
                Changed = null;
                observers.Remove(this);
            }
        }

        private static readonly List<Observer> observers = new List<Observer>();

        internal static IDisposable Observe(string owner, Action<Map, string, CellRect> changed)
        {
            var observer = new Observer { Owner = owner, Changed = changed };
            observers.Add(observer);
            return observer;
        }

        internal static int Revision(Map map, string id)
        {
            if (!ExtensionRegistry.HasRevision(id)) return -1;
            PerMap per = Known(map);
            if (per == null) return 0;
            return per.All + (per.Revisions.TryGetValue(id, out int own) ? own : 0);
        }

        internal static void Publish(Map map, string id, CellRect area)
        {
            PerMap per = Of(map);
            if (id == Vocabulary.Built) per.All++;
            else if (ExtensionRegistry.HasRevision(id)) per.Revisions[id] = (per.Revisions.TryGetValue(id, out int n) ? n : 0) + 1;
            per.Ring?.Changed(id, area);
            if (observers.Count == 0) return;
            foreach (Observer observer in observers.ToArray())
            {
                try { observer.Changed?.Invoke(map, id, area); }
                catch (Exception ex)
                {
                    Log.Error($"{HorizonLog.Prefix}{observer.Owner}: a change observer failed and is off for this session. {ex}");
                    observer.Dispose();
                }
            }
        }

        private const int MaxReports = 200, MaxReportChars = 400;
        private static readonly SortedDictionary<string, string> reports = new SortedDictionary<string, string>(StringComparer.Ordinal);

        internal static void Report(string owner, string key, string value)
        {
            string name = owner + " " + key;
            if (value == null)
            {
                reports.Remove(name);
                return;
            }
            if (reports.Count >= MaxReports && !reports.ContainsKey(name)) return;
            reports[name] = value.Length > MaxReportChars ? value.Substring(0, MaxReportChars) : value;
        }

        internal static void AppendReports(StringBuilder b)
        {
            foreach (KeyValuePair<string, string> report in reports) b.AppendLine(report.Key + "=" + report.Value.Replace('\n', ' '));
        }
    }
}
