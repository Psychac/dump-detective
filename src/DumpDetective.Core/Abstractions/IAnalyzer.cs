using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Abstractions;

public interface IAnalyzer
{
    string Name { get; }
    string Category => AnalyzerCategory.Infer(Name);
    IReadOnlyCollection<string> Tags => [];
    int Order => 0;
    ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken);
}

public class AnalysisContext
{
    public required ClrRuntime Runtime { get; init; }
    public required ClrHeap Heap { get; init; }
    public required IHeapAnalysisCache Cache { get; init; }
    public DiagnosticsOptions Diagnostics { get; init; } = new();
    public IReadOnlyDictionary<string, object?> Options { get; init; } = new Dictionary<string, object?>();
    public IAnalysisDiagnosticsSink DiagnosticsSink { get; init; } = NullAnalysisDiagnosticsSink.Instance;
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
