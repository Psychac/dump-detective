using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Abstractions;

public interface IAnalyzer
{
    string Name { get; }
    string Category => AnalyzerCategory.Infer(Name);
    IReadOnlyCollection<string> Tags => [];
    int Order => 0;
    /// <summary>
    /// Declares whether this analyzer is safe to execute concurrently with other thread-safe analyzers.
    /// <c>false</c> by default — the pipeline runs the analyzer sequentially.
    /// Set to <c>true</c> only when the analyzer holds no shared mutable state and all <c>ClrMD</c>
    /// accesses are read-only and do not mutate heap/runtime state.
    /// Required by <c>AnalysisPipeline</c> before Phase 2 parallelization (PERF-MED-07).
    /// </summary>
    bool IsThreadSafe => false;
    ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken);
}

internal static class AnalyzerCategory
{
    public static string Infer(string analyzerName)
    {
        string name = analyzerName.ToLowerInvariant();
        if (name.Contains("memory")) return "Memory";
        if (name.Contains("thread")) return "Threads";
        if (name.Contains("handle")) return "Handles";
        if (name.Contains("event")) return "Events";
        if (name.Contains("lock")) return "Locks";
        if (name.Contains("module")) return "Modules";
        if (name.Contains("crash")) return "Crash";
        if (name.Contains("hang")) return "Hang";
        if (name.Contains("gc")) return "GC";
        return "General";
    }
}

/// <summary>
/// Stamps <see cref="AnalyzerDomainResult.AnalyzerName"/> and
/// <see cref="AnalyzerDomainResult.Category"/> from the owning analyzer.
/// Call this at the end of every <see cref="IAnalyzer.AnalyzeAsync"/> implementation.
/// </summary>
internal static class AnalyzerDomainResultExtensions
{
    public static AnalyzerDomainResult Stamp(this AnalyzerDomainResult result, IAnalyzer analyzer) =>
        result with { AnalyzerName = analyzer.Name, Category = analyzer.Category };
}
