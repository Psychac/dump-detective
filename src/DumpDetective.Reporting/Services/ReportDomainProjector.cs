using System.Linq;

using DumpDetective.Core.Enums;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Groups sections and findings into domains (Leaks, Memory, GC, ...) and derives
/// cross-domain insights. Owns the shared domain/severity ordering primitives used
/// by other report-serialization collaborators.
/// </summary>
internal static class ReportDomainProjector
{
    public static IReadOnlyList<ReportDomainSection> BuildDomainSections(
        IReadOnlyList<AnalyzerDetailSection> sections,
        IReadOnlyList<FindingRecord> findings)
    {
        var domainOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groupedSections = new Dictionary<string, List<AnalyzerDetailSection>>(StringComparer.OrdinalIgnoreCase);
        var domainInsights = new Dictionary<string, List<FindingRecord>>(StringComparer.OrdinalIgnoreCase);
        var domainSeverity = new Dictionary<string, FindingSeverity?>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < sections.Count; i++)
        {
            AnalyzerDetailSection section = sections[i];
            if (string.IsNullOrWhiteSpace(section.Domain) || string.Equals(section.Domain, "CrossDomain", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!groupedSections.TryGetValue(section.Domain, out List<AnalyzerDetailSection>? list))
            {
                list = [];
                groupedSections[section.Domain] = list;
                domainOrder[section.Domain] = DomainOrder(section.Domain);
            }

            list.Add(section);
            if (!domainSeverity.TryGetValue(section.Domain, out FindingSeverity? current) || LeadSeverityOrder(section.LeadSeverity) < LeadSeverityOrder(current))
                domainSeverity[section.Domain] = section.LeadSeverity;
        }

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord finding = findings[i];
            if (ShouldSuppressInfoInsight(finding))
                continue;

            if (finding.Tags.Any(tag => string.Equals(tag, "cross-analyzer", StringComparison.OrdinalIgnoreCase)))
                continue;

            string domain = InferFindingDomain(finding);
            if (string.IsNullOrWhiteSpace(domain))
                continue;

            if (!domainInsights.TryGetValue(domain, out List<FindingRecord>? list))
            {
                list = [];
                domainInsights[domain] = list;
                if (!domainOrder.ContainsKey(domain))
                    domainOrder[domain] = DomainOrder(domain);
            }

            list.Add(finding);
        }

        var domains = new List<ReportDomainSection>(groupedSections.Count);
        foreach (var pair in groupedSections)
        {
            string domain = pair.Key;
            List<FindingRecord> sortedInsights = [];
            if (domainInsights.TryGetValue(domain, out List<FindingRecord>? insights) && insights is not null)
            {
                sortedInsights = [.. insights];

                sortedInsights.Sort(static (a, b) =>
                {
                    int sev = SeverityOrdinal(b.Severity).CompareTo(SeverityOrdinal(a.Severity));
                    if (sev != 0) return sev;

                    int analyzer = StringComparer.OrdinalIgnoreCase.Compare(a.Analyzer, b.Analyzer);
                    if (analyzer != 0) return analyzer;

                    return StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title);
                });
            }

            domains.Add(new ReportDomainSection(
                Domain: domain,
                LeadSeverity: domainSeverity.TryGetValue(domain, out FindingSeverity? severity) ? severity : null,
                Sections: pair.Value,
                DomainInsights: sortedInsights));
        }

        domains.Sort((a, b) =>
        {
            // Spec: "Domains ordered by MaxSeverityInDomain descending" — severity first
            int sevA = LeadSeverityOrder(a.LeadSeverity);
            int sevB = LeadSeverityOrder(b.LeadSeverity);
            if (sevA != sevB) return sevA.CompareTo(sevB);

            // Within equal severity, use the canonical domain priority order as a tiebreaker
            int orderA = domainOrder.TryGetValue(a.Domain, out int oa) ? oa : 99;
            int orderB = domainOrder.TryGetValue(b.Domain, out int ob) ? ob : 99;
            if (orderA != orderB) return orderA.CompareTo(orderB);

            return StringComparer.OrdinalIgnoreCase.Compare(a.Domain, b.Domain);
        });

        return domains;
    }

    public static IReadOnlyList<FindingRecord> BuildCrossDomainInsights(IReadOnlyList<FindingRecord> findings)
    {
        var cross = new List<FindingRecord>();
        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord finding = findings[i];
            if (ShouldSuppressInfoInsight(finding))
                continue;

            if (finding.Tags.Any(tag => string.Equals(tag, "cross-analyzer", StringComparison.OrdinalIgnoreCase))
                || string.Equals(finding.Analyzer, "InsightEngine", StringComparison.OrdinalIgnoreCase))
            {
                cross.Add(finding);
            }
        }

        cross.Sort(static (a, b) =>
        {
            int sev = SeverityOrdinal(b.Severity).CompareTo(SeverityOrdinal(a.Severity));
            if (sev != 0) return sev;

            int analyzer = StringComparer.OrdinalIgnoreCase.Compare(a.Analyzer, b.Analyzer);
            if (analyzer != 0) return analyzer;

            return StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title);
        });

        return cross;
    }

    public static string InferFindingDomain(FindingRecord finding)
    {
        string domain = SectionIdDomainMap.GetDomain(finding.Analyzer);
        if (!string.IsNullOrWhiteSpace(domain))
            return domain;

        // Fallback for uncommon/custom analyzer names where category is still reliable.
        return finding.Category switch
        {
            "Leak" => "Leaks",
            "Memory" => "Memory",
            "GC" => "GC",
            "TypeSystem" => "TypeSystem",
            "Threads" => "Threads",
            "Async" => "Async",
            "Exceptions" => "Exceptions",
            "Runtime" => "Runtime",
            "Infrastructure" => "Infrastructure",
            _ => string.Empty
        };
    }

    public static int DomainOrder(string domain) => domain switch
    {
        "Leaks"      => 0,
        "Memory"     => 1,
        "GC"         => 2,
        "TypeSystem" => 3,   // Domain C — before Threads (D) per spec A/B/C/D/E/F/G order
        "Threads"    => 4,
        "Async"          => 5,
        "Exceptions"     => 6,
        "Runtime"        => 7,
        "Infrastructure" => 8,
        _                => 99   // unmapped / cross-cutting sections go last
    };

    public static int LeadSeverityOrder(FindingSeverity? s) => s switch
    {
        FindingSeverity.Critical => 0,
        FindingSeverity.Warning  => 1,
        FindingSeverity.Info     => 2,
        null                     => 3,
        _                        => 3
    };

    public static int SeverityOrdinal(string severity) => severity switch
    {
        nameof(FindingSeverity.Critical) => 2,
        nameof(FindingSeverity.Warning) => 1,
        _ => 0
    };

    public static string NormalizeSortKey(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    public static bool ShouldSuppressInfoInsight(FindingRecord finding)
    {
        if (!string.Equals(finding.Severity, nameof(FindingSeverity.Info), StringComparison.OrdinalIgnoreCase))
            return false;

        string category = NormalizeSortKey(finding.Category);
        return string.Equals(category, "Confidence", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "Diagnostics", StringComparison.OrdinalIgnoreCase);
    }
}
