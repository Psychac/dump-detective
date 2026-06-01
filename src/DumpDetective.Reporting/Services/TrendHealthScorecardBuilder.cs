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

        // Also accumulate baseline counts for delta calculations
        var baselineCounts = new Dictionary<string, (int FindingCount, int CriticalCount, int WarningCount)>(StringComparer.Ordinal);
        foreach (InsightFinding f in snapshots[0].Findings)
        {
            string domain = SectionIdDomainMap.GetDomain(f.Analyzer);
            if (string.IsNullOrEmpty(domain)) continue;
            if (!baselineCounts.TryGetValue(domain, out var cnt))
                cnt = (0, 0, 0);
            int critical = f.Severity == FindingSeverity.Critical ? 1 : 0;
            int warning  = f.Severity == FindingSeverity.Warning  ? 1 : 0;
            baselineCounts[domain] = (cnt.FindingCount + 1, cnt.CriticalCount + critical, cnt.WarningCount + warning);
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
            baselineCounts.TryGetValue(domain, out var baseCnt);
            int? baselineCrit = hasBaseline ? baseCnt.CriticalCount : null;
            int? baselineWarn = hasBaseline ? baseCnt.WarningCount : null;
            int? deltaCrit = hasBaseline ? counts.CriticalCount - baseCnt.CriticalCount : null;
            int? deltaWarn = hasBaseline ? counts.WarningCount - baseCnt.WarningCount : null;

            // Compute peaks across all snapshots for this domain
            int peakCrit = int.MinValue; int peakCritIdx = -1;
            int peakWarn = int.MinValue; int peakWarnIdx = -1;
            for (int si = 0; si < snapshots.Count; si++)
            {
                int cCrit = snapshots[si].Findings.Count(f => string.Equals(SectionIdDomainMap.GetDomain(f.Analyzer), domain, StringComparison.Ordinal));
                int cWarn = snapshots[si].Findings.Count(f => string.Equals(SectionIdDomainMap.GetDomain(f.Analyzer), domain, StringComparison.Ordinal) && f.Severity == FindingSeverity.Warning);
                // Note: cCrit counts all findings in domain (not only criticals); need critical-only
                int cCritOnly = snapshots[si].Findings.Count(f => string.Equals(SectionIdDomainMap.GetDomain(f.Analyzer), domain, StringComparison.Ordinal) && f.Severity == FindingSeverity.Critical);
                if (cCritOnly > peakCrit) { peakCrit = cCritOnly; peakCritIdx = snapshots[si].Index; }
                if (cWarn > peakWarn) { peakWarn = cWarn; peakWarnIdx = snapshots[si].Index; }
            }
            int? peakCritVal = peakCrit == int.MinValue ? null : peakCrit;
            int? peakCritSnapshot = peakCritIdx >= 0 ? peakCritIdx : (int?)null;
            int? peakWarnVal = peakWarn == int.MinValue ? null : peakWarn;
            int? peakWarnSnapshot = peakWarnIdx >= 0 ? peakWarnIdx : (int?)null;

            // Build per-snapshot severity history; compute velocity/volatility for 2+ snapshots
            IReadOnlyList<DomainSeverity>? history = null;
            double? velocityScore = null;
            double? volatilityScore = null;
            string? confidenceTrend = null;
            if (snapshots.Count >= 2)
            {
                var valsList = new List<double>(snapshots.Count);
                for (int i = 0; i < snapshots.Count; i++)
                {
                    var sev = snapshotSeverities[i].TryGetValue(domain, out DomainSeverity s) ? s : DomainSeverity.Unknown;
                    if (hasIntermediates)
                    {
                        // Only expose full history when there are intermediates
                        // (3+ snapshots)
                        // We'll still compute numeric stats for 2 snapshots.
                        if (history is null) history = new DomainSeverity[snapshots.Count];
                        ((DomainSeverity[])history)[i] = sev;
                    }
                    valsList.Add(sev switch { DomainSeverity.Critical => 2.0, DomainSeverity.Warning => 1.0, _ => 0.0 });
                }

                var vals = valsList.ToArray();
                if (vals.Length >= 2)
                {
                    int len = vals.Length;
                    double slopeRaw = (vals[^1] - vals[0]) / (len - 1);
                    velocityScore = Math.Max(-1.0, Math.Min(1.0, slopeRaw / 2.0));

                    double mean = vals.Average();
                    double sumsq = vals.Select(v => (v - mean) * (v - mean)).Sum();
                    double stddev = Math.Sqrt(sumsq / vals.Length);
                    volatilityScore = Math.Min(1.0, stddev / 2.0);

                    if (snapshots.Count >= 5 && volatilityScore < 0.15)
                        confidenceTrend = "High";
                    else if (snapshots.Count >= 3 && volatilityScore < 0.35)
                        confidenceTrend = "Medium";
                    else
                        confidenceTrend = "Low";
                }
            }

            var entry = new DomainHealthEntry(
                Domain:          domain,
                Severity:        hasCurrent ? curSev : DomainSeverity.Unknown,
                FindingCount:    counts.FindingCount,
                CriticalCount:   counts.CriticalCount,
                WarningCount:    counts.WarningCount,
                BaselineCriticalCount: baselineCrit,
                DeltaCritical:         deltaCrit,
                BaselineWarningCount:  baselineWarn,
                DeltaWarning:          deltaWarn,
                PeakCriticalCount:     peakCritVal,
                PeakCriticalSnapshotIndex: peakCritSnapshot,
                PeakWarningCount:      peakWarnVal,
                PeakWarningSnapshotIndex: peakWarnSnapshot,
                BaselineSeverity: hasBaseline ? baseSev : null,
                Change:           change,
                SeverityHistory:  history,
                VelocityScore:    velocityScore,
                VolatilityScore:  volatilityScore,
                ConfidenceTrend:  confidenceTrend);

            entries.Add(entry);

            if (entry.Severity > overallSeverity)
                overallSeverity = entry.Severity;
        }

        // Compute trend aggregates
        int domainsRegressed = 0, domainsImproved = 0, domainsNew = 0, domainsRemoved = 0;
        int newCriticals = 0, resolvedCriticals = 0, newWarnings = 0, resolvedWarnings = 0;

        foreach (string domain in orderedDomains)
        {
            bool hasBaseline = baselineCounts.TryGetValue(domain, out var baseCnt);
            currentCounts.TryGetValue(domain, out var curCnt);

            bool hasCurrent = currentCounts.ContainsKey(domain);

            if (!hasBaseline && hasCurrent)
                domainsNew++;
            else if (hasBaseline && !hasCurrent)
                domainsRemoved++;
            else if (hasBaseline && hasCurrent)
            {
                // Compare severity maps computed earlier
                var baseSev = snapshotSeverities[0].TryGetValue(domain, out DomainSeverity b) ? b : DomainSeverity.Unknown;
                var curSev = snapshotSeverities[^1].TryGetValue(domain, out DomainSeverity c) ? c : DomainSeverity.Unknown;
                if (curSev > baseSev) domainsRegressed++;
                else if (curSev < baseSev) domainsImproved++;
            }

            int baseCrit = hasBaseline ? baseCnt.CriticalCount : 0;
            int curCrit = curCnt.CriticalCount;
            int deltaCrit = curCrit - baseCrit;
            if (deltaCrit > 0) newCriticals += deltaCrit; else resolvedCriticals += -Math.Min(0, deltaCrit);

            int baseWarn = hasBaseline ? baseCnt.WarningCount : 0;
            int curWarn = curCnt.WarningCount;
            int deltaWarn = curWarn - baseWarn;
            if (deltaWarn > 0) newWarnings += deltaWarn; else resolvedWarnings += -Math.Min(0, deltaWarn);
        }

        int netCriticalChange = newCriticals - resolvedCriticals;
        int netWarningChange = newWarnings - resolvedWarnings;

        var trend = new TrendSummary(
            DomainsRegressed: domainsRegressed,
            DomainsImproved:  domainsImproved,
            DomainsNew:       domainsNew,
            DomainsRemoved:   domainsRemoved,
            NewCriticals:     newCriticals,
            ResolvedCriticals: resolvedCriticals,
            NetCriticalChange: netCriticalChange,
            NewWarnings:      newWarnings,
            ResolvedWarnings: resolvedWarnings,
            NetWarningChange: netWarningChange);

        return new HealthScorecard(entries, overallSeverity, trend);
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
