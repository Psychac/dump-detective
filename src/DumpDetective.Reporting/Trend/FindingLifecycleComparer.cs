using DumpDetective.Sdk.Synthesis;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.Trend;

internal sealed record FindingLifecycleResult(
    IReadOnlyList<Claim> NewFindings,
    IReadOnlyList<Claim> PersistentFindings,
    IReadOnlyList<Claim> ResolvedFindings,
    IReadOnlyList<FindingSeverityChange> SeverityChanges,
    IReadOnlyList<Claim>? NotComparableFindings = null)
{
    /// <summary>Findings that would read as new or resolved only because their analyzer's coverage differs between the two dumps.</summary>
    public IReadOnlyList<Claim> NotComparable { get; init; } = NotComparableFindings ?? [];
}

/// <summary>
/// A persistent finding (same <see cref="Claim.Fingerprint"/> in both snapshots)
/// whose severity differs between them — in either direction, not just escalation. Consolidates
/// what used to be three separate hand-rolled fingerprint joins doing the same Warning→Critical-only
/// check: <c>TrendRegressionDashboardBuilder.BuildSeverityEscalations</c>,
/// <c>TrendReportComposer</c>'s inline <c>hasEscalations</c> gate, and (for the
/// growth-rather-than-severity case) <c>TrendAnalyzer.ComputeNewLeakSignals</c>. One generic
/// mechanism, computed once here.
/// </summary>
internal sealed record FindingSeverityChange(Claim Current, Severity BaselineSeverity)
{
    public bool IsEscalation => (int)Current.Severity > (int)BaselineSeverity;
}

internal static class FindingLifecycleComparer
{
    public static FindingLifecycleResult Compare(AnalysisSnapshot baseline, AnalysisSnapshot current)
    {
        var baselineByKey = baseline.Findings
            .GroupBy(f => f.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var currentByKey = current.Findings
            .GroupBy(f => f.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var allKeys = new HashSet<string>(baselineByKey.Keys, StringComparer.Ordinal);
        allKeys.UnionWith(currentByKey.Keys);

        var newFindings = new List<Claim>();
        var persistentFindings = new List<Claim>();
        var resolvedFindings = new List<Claim>();
        var severityChanges = new List<FindingSeverityChange>();

        IReadOnlySet<string> notComparableAnalyzers = CoverageLifecycleComparer.Compare(baseline, current).NotComparableAnalyzers;
        var notComparableFindings = new List<Claim>();

        foreach (string key in allKeys)
        {
            bool inBaseline = baselineByKey.ContainsKey(key);
            bool inCurrent = currentByKey.ContainsKey(key);

            // Present in only one dump, and its analyzer was covered differently in the two: the absence may be the coverage, not the process.
            if (inCurrent != inBaseline && notComparableAnalyzers.Contains((inCurrent ? currentByKey[key] : baselineByKey[key]).Analyzer))
            {
                notComparableFindings.Add(inCurrent ? currentByKey[key] : baselineByKey[key]);
            }
            else if (inCurrent && !inBaseline)
            {
                newFindings.Add(currentByKey[key]);
            }
            else if (inCurrent && inBaseline)
            {
                Claim currentFinding = currentByKey[key];
                Claim baselineFinding = baselineByKey[key];
                persistentFindings.Add(currentFinding);

                if (currentFinding.Severity != baselineFinding.Severity)
                    severityChanges.Add(new FindingSeverityChange(currentFinding, baselineFinding.Severity));
            }
            else
            {
                resolvedFindings.Add(baselineByKey[key]);
            }
        }

        return new FindingLifecycleResult(newFindings, persistentFindings, resolvedFindings, severityChanges, notComparableFindings);
    }
}
