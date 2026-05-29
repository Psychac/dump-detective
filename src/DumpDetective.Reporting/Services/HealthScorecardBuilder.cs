using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds a <see cref="HealthScorecard"/> from a completed set of analyzer runs.
/// Groups findings by domain via <see cref="SectionIdDomainMap"/> and computes
/// the max severity per domain. Skipped/failed analyzers produce <see cref="DomainSeverity.Unknown"/>
/// when no other run in that domain succeeded.
/// </summary>
internal static class HealthScorecardBuilder
{
    public static HealthScorecard Build(IReadOnlyList<AnalyzerRunResult> runs)
    {
        // Collect domain → (maxSeverity, findingCount, criticalCount, warningCount, hasSuccessfulRun)
        var domainData = new Dictionary<string, DomainAccumulator>(StringComparer.Ordinal);

        foreach (AnalyzerRunResult run in runs)
        {
            string domain = SectionIdDomainMap.GetDomain(run.AnalyzerName);
            if (string.IsNullOrEmpty(domain))
                continue;

            if (!domainData.TryGetValue(domain, out DomainAccumulator? acc))
            {
                acc = new DomainAccumulator();
                domainData[domain] = acc;
            }

            if (run.Status == AnalyzerExecutionStatus.Success)
                acc.HasSuccessfulRun = true;

            foreach (InsightFinding finding in run.Findings)
            {
                acc.FindingCount++;
                switch (finding.Severity)
                {
                    case FindingSeverity.Critical:
                        acc.CriticalCount++;
                        if (acc.MaxSeverity < FindingSeverity.Critical)
                            acc.MaxSeverity = FindingSeverity.Critical;
                        break;
                    case FindingSeverity.Warning:
                        acc.WarningCount++;
                        if (acc.MaxSeverity < FindingSeverity.Warning)
                            acc.MaxSeverity = FindingSeverity.Warning;
                        break;
                    default:
                        if (acc.MaxSeverity < FindingSeverity.Info)
                            acc.MaxSeverity = FindingSeverity.Info;
                        break;
                }
            }
        }

        // Build per-domain entries in priority order
        var entries = new List<DomainHealthEntry>(SectionIdDomainMap.DomainsInOrder.Count);
        DomainSeverity overallSeverity = DomainSeverity.Unknown;

        foreach (string domain in SectionIdDomainMap.DomainsInOrder)
        {
            if (!domainData.TryGetValue(domain, out DomainAccumulator? acc))
                continue;  // no analyzer registered for this domain ran at all

            DomainSeverity sev = acc.HasSuccessfulRun
                ? FindingSeverityToDomain(acc.MaxSeverity, acc.FindingCount)
                : DomainSeverity.Unknown;

            entries.Add(new DomainHealthEntry(
                Domain:        domain,
                Severity:      sev,
                FindingCount:  acc.FindingCount,
                CriticalCount: acc.CriticalCount,
                WarningCount:  acc.WarningCount));

            if (sev > overallSeverity)
                overallSeverity = sev;
        }

        return new HealthScorecard(entries, overallSeverity);
    }

    private static DomainSeverity FindingSeverityToDomain(FindingSeverity maxSev, int findingCount)
    {
        if (findingCount == 0) return DomainSeverity.OK;
        return maxSev switch
        {
            FindingSeverity.Critical => DomainSeverity.Critical,
            FindingSeverity.Warning  => DomainSeverity.Warning,
            _                        => DomainSeverity.OK,
        };
    }

    private sealed class DomainAccumulator
    {
        public FindingSeverity MaxSeverity = FindingSeverity.Info;
        public int FindingCount;
        public int CriticalCount;
        public int WarningCount;
        public bool HasSuccessfulRun;
    }
}
