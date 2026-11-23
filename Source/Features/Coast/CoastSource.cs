using Verse;
using RimWorld;

namespace EndlessHorizon
{
    internal sealed class CoastSource
    {
        internal CoastNoise.Source Source;
        internal string Origin = "none";
        internal CoastSearch Pending;
        internal CoastFallback Fallback;

        internal static CoastSource Resolve(Map map, bool useCoast, CoastNoise.Source overrideSource)
        {
            var source = new CoastSource();
            if (useCoast)
            {
                source.Source = overrideSource ?? CoastNoise.Captured(map);
                TileMutatorWorker_Coast worker = CoastNoise.CoastWorkerOf(map);
                if (source.Source == null && worker != null)
                {
                    CoastSearch search = CoastSearch.For(map, worker);
                    if (search.Done && search.Result != null)
                    {
                        source.Source = search.Result;
                        source.Origin = search.Report;
                    }
                    else
                    {
                        source.Pending = search.Done ? null : search;
                        source.Fallback = CoastFallback.TryCreate(map);
                        source.Origin = (search.Done ? search.Report : "searching") + " → " + (source.Fallback?.Report ?? "no fallback");
                    }
                }
                else
                {
                    source.Origin = source.Source?.origin ?? "none";
                }
            }
            return source;
        }
    }
}
