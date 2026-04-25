using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Trend;

internal sealed record FindingLifecycleResult(
    IReadOnlyList<InsightFinding> NewFindings,
    IReadOnlyList<InsightFinding> PersistentFindings,
    IReadOnlyList<InsightFinding> ResolvedFindings);

internal static class FindingLifecycleComparer
{
    public static FindingLifecycleResult Compare(AnalysisSnapshot baseline, AnalysisSnapshot current)
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
