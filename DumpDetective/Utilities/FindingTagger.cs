using DumpDetective.Models;

namespace DumpDetective.Utilities
{
    internal static class FindingTagger
    {
        public static void Normalize(List<InsightFinding> findings)
        {
            if (findings.Count == 0)
            {
                return;
            }

            var normalized = findings
                .DistinctBy(f => (f.Analyzer, f.Title, f.Evidence))
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Analyzer, StringComparer.Ordinal)
                .ToList();

            findings.Clear();
            findings.AddRange(normalized);
        }
    }
}
