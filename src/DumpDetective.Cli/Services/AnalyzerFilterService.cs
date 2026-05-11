using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Cli.Services;

/// <summary>
/// Pure static service: validates, filters, and orders the analyzer list.
/// Contains no mutable state and requires no DI — unit-testable without infrastructure.
/// </summary>
internal static class AnalyzerFilterService
{
    /// <summary>
    /// Throws <see cref="ConfigurationException"/> if any include/exclude name does not match a known analyzer.
    /// </summary>
    public static void Validate(ResolvedExecutionOptions resolved, IReadOnlyList<IAnalyzer> analyzers)
    {
        HashSet<string> known = analyzers.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> unknownIncludes = resolved.IncludeAnalyzers.Where(name => !known.Contains(name)).ToList();
        List<string> unknownExcludes = resolved.ExcludeAnalyzers.Where(name => !known.Contains(name)).ToList();

        if (unknownIncludes.Count > 0 || unknownExcludes.Count > 0)
        {
            List<string> messages = [];
            if (unknownIncludes.Count > 0)
                messages.Add($"Unknown include analyzers: {string.Join(", ", unknownIncludes)}");
            if (unknownExcludes.Count > 0)
                messages.Add($"Unknown exclude analyzers: {string.Join(", ", unknownExcludes)}");

            throw new ConfigurationException(string.Join(Environment.NewLine, messages));
        }
    }

    /// <summary>
    /// Applies include/exclude name filters and returns the surviving analyzers.
    /// </summary>
    public static IReadOnlyList<IAnalyzer> Apply(ResolvedExecutionOptions resolved, IReadOnlyList<IAnalyzer> analyzers)
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

        return filtered.ToList();
    }

    /// <summary>
    /// Orders analyzers by pipeline stage rank, then by <see cref="IAnalyzer.Order"/>, then by name.
    /// </summary>
    public static IReadOnlyList<IAnalyzer> Order(IReadOnlyList<IAnalyzer> analyzers)
        => analyzers
            .OrderBy(GetStageRank)
            .ThenBy(a => a.Order)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<AnalyzerRunResult> BuildSkippedByFilterResults(
        IReadOnlyList<IAnalyzer> allAnalyzers,
        IReadOnlyList<IAnalyzer> activeAnalyzers)
    {
        HashSet<string> activeNames = activeAnalyzers.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<AnalyzerRunResult> skipped = [];

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
                Findings: [],
                FindingCount: 0,
                WarningCount: 0,
                ObjectScanCount: 0,
                CacheHits: 0,
                CacheMisses: 0,
                Artifacts: [],
                FindingGeneratorError: null,
                MemoryStats: null));
        }

        return skipped;
    }

    private static int GetStageRank(IAnalyzer analyzer)
    {
        string typeName = analyzer.GetType().Name;
        return typeName switch
        {
            // Stage 0 — Profiling heap and GC
            nameof(Analysis.Analyzers.MemoryAnalyzer)
            or nameof(Analysis.Analyzers.GCGenerationAnalyzer)
            or nameof(Analysis.Analyzers.AllocationPatternAnalyzer)
            or nameof(Analysis.Analyzers.ObjectShapeAnalyzer)
            or nameof(Analysis.Analyzers.GCRootAnalyzer)
            or nameof(Analysis.Analyzers.SegmentAnalyzer)
            or nameof(Analysis.Analyzers.ModuleAnalyzer)
                => 0,

            // Stage 1 — Analyzing crash and hang signals
            nameof(Analysis.Analyzers.CrashAnalyzer)
            or nameof(Analysis.Analyzers.HangAnalyzer)
            or nameof(Analysis.Analyzers.AsyncTaskAnalyzer)
                => 1,

            // Stage 2+3 — Detecting memory leaks (both map to the same console stage name so they stay consecutive)
            nameof(Analysis.Analyzers.RetentionAnalyzer)
            or nameof(Analysis.Analyzers.LeakCandidateAnalyzer)
            or nameof(Analysis.Analyzers.CollectionAnalyzer)
            or nameof(Analysis.Analyzers.StringAnalyzer)
                => 2,

            nameof(Analysis.Analyzers.StaticRootLeakDetector)
            or nameof(Analysis.Analyzers.ReferenceChainAnalyzer)
                => 3,

            // Stage 4 — Inspecting handles and fragmentation (ThreadStackClusterAnalyzer runs last in this
            //            rank but maps to the next console stage, causing a clean break)
            nameof(Analysis.Analyzers.GCHandleAnalyzer)
            or nameof(Analysis.Analyzers.DependentHandleAnalyzer)
            or nameof(Analysis.Analyzers.LohFragmentationAnalyzer)
            or nameof(Analysis.Analyzers.ThreadStackClusterAnalyzer)
                => 4,

            // Stage 5 — Analyzing threads and concurrency (continues from ThreadStackClusterAnalyzer)
            nameof(Analysis.Analyzers.ThreadAnalyzer)
            or nameof(Analysis.Analyzers.LockGraphAnalyzer)
            or nameof(Analysis.Analyzers.EventLeakAnalyzer)
                => 5,

            // Stage 6 — Deep object and runtime inspection
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
