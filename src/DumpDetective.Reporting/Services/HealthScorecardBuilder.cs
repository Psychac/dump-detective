using DumpDetective.Sdk.Synthesis;
using DumpDetective.Core.Enums;
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
                    case Severity.Critical:
                        acc.CriticalCount++;
                        if (acc.MaxSeverity < Severity.Critical)
                            acc.MaxSeverity = Severity.Critical;
                        break;
                    case Severity.Warning:
                        acc.WarningCount++;
                        if (acc.MaxSeverity < Severity.Warning)
                            acc.MaxSeverity = Severity.Warning;
                        break;
                    default:
                        if (acc.MaxSeverity < Severity.Info)
                            acc.MaxSeverity = Severity.Info;
                        break;
                }
            }
        }

        // Build per-domain entries in priority order
        var domains = new Dictionary<string, DomainHealthEntry>(StringComparer.Ordinal);
        DomainSeverity overallSeverity = DomainSeverity.Unknown;

        foreach (string domain in SectionIdDomainMap.DomainsInOrder)
        {
            if (!domainData.TryGetValue(domain, out DomainAccumulator? acc))
                continue;  // no analyzer registered for this domain ran at all

            DomainSeverity sev = acc.HasSuccessfulRun
                ? FindingSeverityToDomain(acc.MaxSeverity, acc.FindingCount)
                : DomainSeverity.Unknown;

            var entry = new DomainHealthEntry(
                Domain:        domain,
                Severity:      sev,
                FindingCount:  acc.FindingCount,
                CriticalCount: acc.CriticalCount,
                WarningCount:  acc.WarningCount);

            domains[domain] = entry;

            if (sev > overallSeverity)
                overallSeverity = sev;
        }

        return new HealthScorecard(domains, overallSeverity);
    }

    private static DomainSeverity FindingSeverityToDomain(Severity maxSev, int findingCount)
    {
        if (findingCount == 0) return DomainSeverity.OK;
        return maxSev switch
        {
            Severity.Critical => DomainSeverity.Critical,
            Severity.Warning  => DomainSeverity.Warning,
            _                        => DomainSeverity.OK,
        };
    }

    private sealed class DomainAccumulator
    {
        public Severity MaxSeverity = Severity.Info;
        public int FindingCount;
        public int CriticalCount;
        public int WarningCount;
        public bool HasSuccessfulRun;
    }
}
