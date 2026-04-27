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

    private static int GetStageRank(IAnalyzer analyzer)
    {
        string typeName = analyzer.GetType().Name;
        return typeName switch
        {
            nameof(Analysis.Analyzers.MemoryAnalyzer)
            or nameof(Analysis.Analyzers.GCGenerationAnalyzer)
            or nameof(Analysis.Analyzers.ModuleAnalyzer)
                => 0,

            nameof(Analysis.Analyzers.CrashAnalyzer)
            or nameof(Analysis.Analyzers.HangAnalyzer)
                => 1,

            nameof(Analysis.Analyzers.MemoryLeakAnalyzer)
            or nameof(Analysis.Analyzers.CollectionAnalyzer)
                => 2,

            nameof(Analysis.Analyzers.StaticRootLeakDetector)
            or nameof(Analysis.Analyzers.ReferenceChainAnalyzer)
                => 3,

            nameof(Analysis.Analyzers.GCHandleAnalyzer)
            or nameof(Analysis.Analyzers.DependentHandleAnalyzer)
            or nameof(Analysis.Analyzers.LohFragmentationAnalyzer)
            or nameof(Analysis.Analyzers.ThreadStackClusterAnalyzer)
                => 4,

            nameof(Analysis.Analyzers.ThreadAnalyzer)
            or nameof(Analysis.Analyzers.LockGraphAnalyzer)
            or nameof(Analysis.Analyzers.EventLeakAnalyzer)
                => 5,

            _ => 99
        };
    }
}
