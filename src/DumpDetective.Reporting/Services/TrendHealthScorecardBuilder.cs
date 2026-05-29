using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds a <see cref="HealthScorecard"/> for trend mode.
/// Considers ALL snapshots, not just baseline vs current, so that intermediate
/// regressions/improvements are visible in the scorecard history.
/// </summary>
internal static class TrendHealthScorecardBuilder
{
    public static HealthScorecard Build(IReadOnlyList<AnalysisSnapshot> snapshots)
    {
        // Per-snapshot domain-severity maps (index 0 = baseline, last = current)
        var snapshotSeverities = new Dictionary<string, DomainSeverity>[snapshots.Count];
        for (int i = 0; i < snapshots.Count; i++)
            snapshotSeverities[i] = ComputeDomainSeverities(snapshots[i].Findings);

        var baselineDomainSeverity = snapshotSeverities[0];
        var currentDomainSeverity  = snapshotSeverities[^1];

        // Union of all domains that appeared in any snapshot
        var allDomains = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < snapshots.Count; i++)
        {
            foreach (string d in snapshotSeverities[i].Keys) allDomains.Add(d);
        }

        // Accumulate counts per domain from current (latest) findings
        var currentCounts = new Dictionary<string, (int FindingCount, int CriticalCount, int WarningCount)>(StringComparer.Ordinal);
        foreach (InsightFinding f in snapshots[^1].Findings)
        {
            string domain = SectionIdDomainMap.GetDomain(f.Analyzer);
            if (string.IsNullOrEmpty(domain)) continue;
            if (!currentCounts.TryGetValue(domain, out var cnt))
                cnt = (0, 0, 0);
            int critical = f.Severity == FindingSeverity.Critical ? 1 : 0;
            int warning  = f.Severity == FindingSeverity.Warning  ? 1 : 0;
            currentCounts[domain] = (cnt.FindingCount + 1, cnt.CriticalCount + critical, cnt.WarningCount + warning);
        }

        var entries = new List<DomainHealthEntry>(allDomains.Count);
        DomainSeverity overallSeverity = DomainSeverity.Unknown;

        // Emit in the canonical domain order, then any remainder
        var orderedDomains = new List<string>(SectionIdDomainMap.DomainsInOrder.Count + 2);
        foreach (string d in SectionIdDomainMap.DomainsInOrder)
        {
            if (allDomains.Contains(d)) orderedDomains.Add(d);
        }
        foreach (string d in allDomains)
        {
            if (!orderedDomains.Contains(d)) orderedDomains.Add(d);
        }

        bool hasIntermediates = snapshots.Count > 2;

        foreach (string domain in orderedDomains)
        {
            bool hasBaseline = baselineDomainSeverity.TryGetValue(domain, out DomainSeverity baseSev);
            bool hasCurrent  = currentDomainSeverity.TryGetValue(domain, out DomainSeverity curSev);

            if (!hasBaseline && !hasCurrent) continue;

            DomainSeverityChange change;
            if (!hasBaseline)
                change = DomainSeverityChange.NewDomain;
            else if (!hasCurrent)
                change = DomainSeverityChange.Removed;
            else if (curSev > baseSev)
                change = DomainSeverityChange.Regressed;
            else if (curSev < baseSev)
                change = DomainSeverityChange.Improved;
            else
                change = DomainSeverityChange.Stable;

            currentCounts.TryGetValue(domain, out var counts);

            // Build per-snapshot severity history when there are 3+ snapshots
            IReadOnlyList<DomainSeverity>? history = null;
            if (hasIntermediates)
            {
                var hist = new DomainSeverity[snapshots.Count];
                for (int i = 0; i < snapshots.Count; i++)
                {
                    hist[i] = snapshotSeverities[i].TryGetValue(domain, out DomainSeverity s)
                        ? s
                        : DomainSeverity.Unknown;
                }
                history = hist;
            }

            var entry = new DomainHealthEntry(
                Domain:          domain,
                Severity:        hasCurrent ? curSev : DomainSeverity.Unknown,
                FindingCount:    counts.FindingCount,
                CriticalCount:   counts.CriticalCount,
                WarningCount:    counts.WarningCount,
                BaselineSeverity: hasBaseline ? baseSev : null,
                Change:           change,
                SeverityHistory:  history);

            entries.Add(entry);

            if (entry.Severity > overallSeverity)
                overallSeverity = entry.Severity;
        }

        return new HealthScorecard(entries, overallSeverity);
    }

    private static Dictionary<string, DomainSeverity> ComputeDomainSeverities(IReadOnlyList<InsightFinding> findings)
    {
        // domain → maxSeverity among findings with at least one finding
        var map = new Dictionary<string, DomainSeverity>(StringComparer.Ordinal);

        foreach (InsightFinding f in findings)
        {
            string domain = SectionIdDomainMap.GetDomain(f.Analyzer);
            if (string.IsNullOrEmpty(domain)) continue;

            DomainSeverity fSev = f.Severity switch
            {
                FindingSeverity.Critical => DomainSeverity.Critical,
                FindingSeverity.Warning  => DomainSeverity.Warning,
                _                        => DomainSeverity.OK
            };

            if (!map.TryGetValue(domain, out DomainSeverity existing) || fSev > existing)
                map[domain] = fSev;
        }

        return map;
    }
}
