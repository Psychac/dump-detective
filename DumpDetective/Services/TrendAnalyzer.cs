using DumpDetective.Models;
using DumpDetective.Services.Comparers;

namespace DumpDetective.Services
{
    internal sealed class TrendAnalyzer
    {
        private readonly IReadOnlyDictionary<string, IAnalyzerTrendComparer> _comparers;

        public TrendAnalyzer()
        {
            var list = new List<IAnalyzerTrendComparer>
            {
                new MemoryAnalyzerTrendComparer(),
                new GCGenerationTrendComparer(),
                new ModuleTrendComparer(),
                new CrashTrendComparer(),
                new HangTrendComparer(),
                new MemoryLeakTrendComparer(),
                new CollectionTrendComparer(),
                new StaticRootTrendComparer(),
                new ReferenceChainTrendComparer(),
                new ThreadTrendComparer(),
                new GCHandleTrendComparer(),
                new LohFragmentationTrendComparer(),
                new DependentHandleTrendComparer(),
                new ThreadStackClusterTrendComparer(),
                new EventLeakTrendComparer()
            };
            _comparers = list.ToDictionary(c => c.AnalyzerName, StringComparer.Ordinal);
        }

        public IReadOnlyList<AnalyzerTrendResult> CompareAll(AnalysisSnapshot baseline, AnalysisSnapshot current)
        {
            var results = new List<AnalyzerTrendResult>();
            foreach (var (analyzerName, baselineDomain) in baseline.DomainResults)
            {
                if (!current.DomainResults.TryGetValue(analyzerName, out var currentDomain))
                    continue;
                if (!_comparers.TryGetValue(analyzerName, out var comparer))
                    continue;
                var deltas = comparer.Compare(baselineDomain, currentDomain);
                if (deltas.Count > 0)
                    results.Add(new AnalyzerTrendResult(analyzerName, deltas));
            }
            return results;
        }

        public IReadOnlyList<IReadOnlyList<AnalyzerTrendResult>> CompareSeries(IReadOnlyList<AnalysisSnapshot> snapshots)
        {
            if (snapshots.Count < 2)
                return [];

            var steps = new List<IReadOnlyList<AnalyzerTrendResult>>(snapshots.Count - 1);
            for (int i = 1; i < snapshots.Count; i++)
                steps.Add(CompareAll(snapshots[i - 1], snapshots[i]));
            return steps;
        }

        public FindingLifecycleResult CompareFindings(AnalysisSnapshot baseline, AnalysisSnapshot current)
        {
            var baselineByKey = baseline.Findings
                .GroupBy(f => f.EffectiveFingerprint, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var currentByKey = current.Findings
                .GroupBy(f => f.EffectiveFingerprint, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var allKeys = new HashSet<string>(baselineByKey.Keys, StringComparer.Ordinal);
            allKeys.UnionWith(currentByKey.Keys);

            var newFindings = new List<InsightFinding>();
            var persistentFindings = new List<InsightFinding>();
            var resolvedFindings = new List<InsightFinding>();

            foreach (string key in allKeys)
            {
                bool inBaseline = baselineByKey.ContainsKey(key);
                bool inCurrent = currentByKey.ContainsKey(key);

                if (inCurrent && !inBaseline) newFindings.Add(currentByKey[key]);
                else if (inCurrent && inBaseline) persistentFindings.Add(currentByKey[key]);
                else resolvedFindings.Add(baselineByKey[key]);
            }

            return new FindingLifecycleResult(newFindings, persistentFindings, resolvedFindings);
        }
    }

    internal sealed record FindingLifecycleResult(
        IReadOnlyList<InsightFinding> NewFindings,
        IReadOnlyList<InsightFinding> PersistentFindings,
        IReadOnlyList<InsightFinding> ResolvedFindings);
}
