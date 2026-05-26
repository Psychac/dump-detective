using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds a <see cref="HealthScorecard"/> for trend mode.
/// Compares baseline (snapshots[0]) findings against current (snapshots[^1]) findings
/// and annotates each <see cref="DomainHealthEntry"/> with <see cref="DomainSeverityChange"/>
/// and the baseline severity.
/// </summary>
internal static class TrendHealthScorecardBuilder
{
    public static HealthScorecard Build(
        IReadOnlyList<InsightFinding> baselineFindings,
        IReadOnlyList<InsightFinding> currentFindings)
    {
        var baselineDomainSeverity = ComputeDomainSeverities(baselineFindings);
        var currentDomainSeverity  = ComputeDomainSeverities(currentFindings);

        // Union of all domains that appeared in either snapshot
        var allDomains = new HashSet<string>(StringComparer.Ordinal);
        foreach (string d in baselineDomainSeverity.Keys) allDomains.Add(d);
        foreach (string d in currentDomainSeverity.Keys)  allDomains.Add(d);

        // Accumulate counts per domain from current findings
        var currentCounts = new Dictionary<string, (int FindingCount, int CriticalCount, int WarningCount)>(StringComparer.Ordinal);
        foreach (InsightFinding f in currentFindings)
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

            var entry = new DomainHealthEntry(
                Domain:          domain,
                Severity:        hasCurrent ? curSev : DomainSeverity.Unknown,
                FindingCount:    counts.FindingCount,
                CriticalCount:   counts.CriticalCount,
                WarningCount:    counts.WarningCount,
                BaselineSeverity: hasBaseline ? baseSev : null,
                Change:           change);

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

        // domains with no findings → DomainSeverity.OK
        // (we only receive findings for domains that had runs, so absent == not run / no findings)
        return map;
    }
}
