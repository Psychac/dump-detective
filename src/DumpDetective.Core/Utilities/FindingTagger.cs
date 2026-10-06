using DumpDetective.Sdk.Synthesis;
using DumpDetective.Core.Models;

namespace DumpDetective.Core.Utilities;

internal static class FindingTagger
{
    public static void Normalize(List<Claim> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        var normalized = findings
            .DistinctBy(f => f.Fingerprint)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Analyzer, StringComparer.Ordinal)
            .ToArray();

        findings.Clear();
        findings.AddRange(normalized);
    }
}
