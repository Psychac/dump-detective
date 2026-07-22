using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Cli.Services;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Diagnostics;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Pure static service: validates, filters, and orders the analyzer list.
/// Contains no mutable state and requires no DI — unit-testable without infrastructure.
/// </summary>
internal static class AnalyzerFilterService
{
    public static void Validate(ResolvedExecutionOptions resolved, IReadOnlyList<IAnalyzer> analyzers)
    {
        HashSet<string> known = analyzers.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknownIncludesEnum = resolved.IncludeAnalyzers.Where(name => !known.Contains(name));
        var unknownExcludesEnum = resolved.ExcludeAnalyzers.Where(name => !known.Contains(name));

        if (unknownIncludesEnum.Any() || unknownExcludesEnum.Any())
        {
            var messages = new List<string>();
            if (unknownIncludesEnum.Any())
                messages.Add($"Unknown include analyzers: {string.Join(", ", unknownIncludesEnum)}");
            if (unknownExcludesEnum.Any())
                messages.Add($"Unknown exclude analyzers: {string.Join(", ", unknownExcludesEnum)}");

            throw new ConfigurationException(string.Join(Environment.NewLine, messages));
        }
    }

    public static IEnumerable<IAnalyzer> Apply(ResolvedExecutionOptions resolved, IReadOnlyList<IAnalyzer> analyzers)
    {
        IEnumerable<IAnalyzer> filtered = analyzers;

        if (resolved.IncludeAnalyzers.Count > 0)
        {
            HashSet<string> include = resolved.IncludeAnalyzers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(a => include.Contains(a.Name));
        }

        if (resolved.ExcludeAnalyzers.Count > 0)
        {
            HashSet<string> exclude = resolved.ExcludeAnalyzers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(a => !exclude.Contains(a.Name));
        }

        return filtered;
    }

    public static IReadOnlyList<IAnalyzer> Order(IEnumerable<IAnalyzer> analyzers)
    {
        // materialize once after ordering to avoid multiple enumerations
        return analyzers
            .OrderBy(a => GetDomainRank(a))
            .ThenBy(GetStageRank)
            .ThenBy(a => a.Order)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static int GetDomainRank(IAnalyzer analyzer)
    {
        // Prefer the canonical report domain mapping; fall back to analyzer.Category.
        string domain = DumpDetective.Reporting.Services.SectionIdDomainMap.GetDomain(analyzer.Name);
        if (string.IsNullOrWhiteSpace(domain))
            domain = analyzer.Category ?? string.Empty;

        // DomainsInOrder defines preferred ordering; use index if present.
        var domains = DumpDetective.Reporting.Services.SectionIdDomainMap.DomainsInOrder;
        for (int i = 0; i < domains.Count; i++)
        {
            if (string.Equals(domains[i], domain, StringComparison.Ordinal))
                return i;
        }
        return int.MaxValue;
    }

    public static IReadOnlyList<AnalyzerRunResult> BuildSkippedByFilterResults(
        IReadOnlyList<IAnalyzer> allAnalyzers,
        IReadOnlyList<IAnalyzer> activeAnalyzers)
    {
        HashSet<string> activeNames = activeAnalyzers.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<AnalyzerRunResult>();

        foreach (IAnalyzer analyzer in allAnalyzers)
        {
            if (activeNames.Contains(analyzer.Name))
                continue;

            skipped.Add(new AnalyzerRunResult(
                analyzer.Name,
                AnalyzerExecutionStatus.SkippedByFilter,
                TimeSpan.Zero,
                null,
                "Excluded by --include-analyzers / --exclude-analyzers filter.",
                null,
                SkipReason: "Excluded by analyzer filter.",
                Findings: Array.Empty<InsightFinding>(),
                FindingCount: 0,
                WarningCount: 0,
                Artifacts: Array.Empty<ReportArtifact>(),
                Diagnostics: new AnalyzerExecutionDiagnostics(
                    ObjectScanCount: 0,
                    CacheHits: 0,
                    CacheMisses: 0)));
        }

        return skipped;
    }

    internal static int GetStageRank(IAnalyzer analyzer)
    {
        string typeName = analyzer.GetType().Name;
        return typeName switch
        {
            nameof(Analysis.Analyzers.MemoryAnalyzer)
            or nameof(Analysis.Analyzers.GCGenerationAnalyzer)
            or nameof(Analysis.Analyzers.AllocationPatternAnalyzer)
            or nameof(Analysis.Analyzers.ObjectShapeAnalyzer)
            or nameof(Analysis.Analyzers.GCRootAnalyzer)
            or nameof(Analysis.Analyzers.HeapTopologyAnalyzer)
            or nameof(Analysis.Analyzers.ModuleAnalyzer)
                => 0,

            nameof(Analysis.Analyzers.CrashAnalyzer)
            or nameof(Analysis.Analyzers.HangAnalyzer)
            or nameof(Analysis.Analyzers.AsyncTaskAnalyzer)
                => 1,

            nameof(Analysis.Analyzers.DominatorAnalyzer)
            or nameof(Analysis.Analyzers.LeakCandidateAnalyzer)
            or nameof(Analysis.Analyzers.CollectionAnalyzer)
            or nameof(Analysis.Analyzers.StringAnalyzer)
                => 2,

            nameof(Analysis.Analyzers.StaticRootLeakDetector)
            or nameof(Analysis.Analyzers.ReferenceChainAnalyzer)
                => 3,

            nameof(Analysis.Analyzers.GCHandleAnalyzer)
            or nameof(Analysis.Analyzers.LohFragmentationAnalyzer)
            or nameof(Analysis.Analyzers.ThreadStackClusterAnalyzer)
                => 4,

            nameof(Analysis.Analyzers.ThreadAnalyzer)
            or nameof(Analysis.Analyzers.LockGraphAnalyzer)
            or nameof(Analysis.Analyzers.EventLeakAnalyzer)
                => 5,

            nameof(Analysis.Analyzers.FinalizableObjectAnalyzer)
            or nameof(Analysis.Analyzers.AsyncStateMachineAnalyzer)
            or nameof(Analysis.Analyzers.ArrayAnalyzer)
            or nameof(Analysis.Analyzers.AppDomainAnalyzer)
            or nameof(Analysis.Analyzers.SegmentReservationAnalyzer)
            or nameof(Analysis.Analyzers.WeakReferenceAnalyzer)
            or nameof(Analysis.Analyzers.BoxingAnalyzer)
            or nameof(Analysis.Analyzers.JitAnalyzer)
                => 6,

            _ => 99
        };
    }
}
