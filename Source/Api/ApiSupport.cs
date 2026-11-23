using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Verse;

namespace EndlessHorizon.Api
{
    internal static class ApiSupport
    {
        private static readonly HashSet<string> logged = new HashSet<string>(StringComparer.Ordinal);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Failed(string member, Exception ex)
        {
            string caller = "unknown";
            foreach (StackFrame frame in new StackTrace().GetFrames())
            {
                MethodBase method = frame.GetMethod();
                Type type = method?.DeclaringType;
                if (type == null || type.Namespace == typeof(ApiSupport).Namespace) continue;
                caller = $"{type.Assembly.GetName().Name} {type.FullName}.{method.Name}";
                break;
            }
            if (logged.Add(member + " " + caller)) Log.Error($"{HorizonLog.Prefix}API {member} failed (caller {caller}). {ex}");
        }

        private sealed class MapOnly
        {
            internal HorizonSurface Surface;
        }

        private static readonly ConditionalWeakTable<Map, MapOnly> mapOnly = new ConditionalWeakTable<Map, MapOnly>();

        internal static HorizonSurface SurfaceOf(Map map)
        {
            if (map == null) return null;
            HorizonSurface surface = SurfaceTable.Current(map);
            if (surface != null) return surface;
            HorizonDomain domain = DomainBeforeBuild(map);
            MapOnly kept = mapOnly.GetOrCreateValue(map);
            if (kept.Surface?.Domain != domain) kept.Surface = new HorizonSurface(map, domain, HorizonSettings.Current?.fogStart ?? 0);
            return kept.Surface;
        }

        internal static HorizonDomain DomainOf(Map map)
        {
            if (map == null) return null;
            return SurfaceTable.Current(map)?.Domain ?? DomainBeforeBuild(map);
        }

        private static HorizonDomain DomainBeforeBuild(Map map) =>
            HorizonDomain.Of(map, HorizonActivity.IsActive(map) ? HorizonActivity.RingWidth : 0);
    }
}
