using DumpDetective.Core.Models;

namespace DumpDetective.Core.Utilities;

internal static class FindingTagger
{
    public static void Normalize(List<InsightFinding> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        var normalized = findings
            .DistinctBy(f => f.EffectiveFingerprint)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Analyzer, StringComparer.Ordinal)
            .ToArray();

        findings.Clear();
        findings.AddRange(normalized);
    }
}
