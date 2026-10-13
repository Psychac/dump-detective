using DumpDetective.Core.Models;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Reporting.Trend;

internal sealed record CoverageChange(string Analyzer, string Text);

internal sealed record CoverageLifecycleResult(IReadOnlySet<string> NotComparableAnalyzers, IReadOnlyList<CoverageChange> Changes);

/// <summary>
/// Finds the analyzers whose coverage differs between two dumps (docs/ReportStructure/m3-coverage-plan.md D6). An analyzer's coverage here
/// is the set of limits and failures its records name, by capability and status; the size of a limit ("8 of 10") is not part of it, since
/// that varies between runs of the same dump. Two dumps limited the same way are comparable, because the same limit stands on both sides.
/// </summary>
internal static class CoverageLifecycleComparer
{
    private const string AnalyzerFailure = "analyzer failure";

    public static CoverageLifecycleResult Compare(AnalysisSnapshot baseline, AnalysisSnapshot current)
    {
        Dictionary<string, SortedSet<string>> before = LimitsByAnalyzer(baseline);
        Dictionary<string, SortedSet<string>> after = LimitsByAnalyzer(current);

        var analyzers = new SortedSet<string>(before.Keys, StringComparer.Ordinal);
        analyzers.UnionWith(after.Keys);

        var notComparable = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<CoverageChange>();

        foreach (string analyzer in analyzers)
        {
            SortedSet<string> was = before.GetValueOrDefault(analyzer) ?? [];
            SortedSet<string> now = after.GetValueOrDefault(analyzer) ?? [];
            if (was.SetEquals(now))
                continue;

            notComparable.Add(analyzer);
            foreach (string limit in now.Except(was, StringComparer.Ordinal))
                changes.Add(new CoverageChange(analyzer, $"{analyzer}: {limit} in the last dump, not in the first."));
            foreach (string limit in was.Except(now, StringComparer.Ordinal))
                changes.Add(new CoverageChange(analyzer, $"{analyzer}: {limit} in the first dump, not in the last."));
        }

        return new CoverageLifecycleResult(notComparable, changes);
    }

    private static Dictionary<string, SortedSet<string>> LimitsByAnalyzer(AnalysisSnapshot snapshot)
    {
        var limits = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        if (snapshot.Coverage is null)
            return limits;

        foreach (CoverageRecord record in snapshot.Coverage)
        {
            if (record.Status is not (CoverageStatus.Partial or CoverageStatus.Failed or CoverageStatus.Degraded or CoverageStatus.Skipped))
                continue;

            string limit = record.Capability is { } capability
                ? $"{capability} {record.Status.ToString().ToLowerInvariant()}"
                : AnalyzerFailure;

            foreach (CoverageImpact impact in record.Impacts)
            {
                if (impact.Kind is not (CoverageImpactKind.Limited or CoverageImpactKind.Failed))
                    continue;

                if (!limits.TryGetValue(impact.Analyzer, out SortedSet<string>? set))
                    limits[impact.Analyzer] = set = new SortedSet<string>(StringComparer.Ordinal);

                set.Add(limit);
            }
        }

        return limits;
    }
}
