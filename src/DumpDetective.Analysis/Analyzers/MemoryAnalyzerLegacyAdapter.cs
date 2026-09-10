using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Runs the retyped <see cref="MemoryAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
public sealed class MemoryAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<MemoryAnalyzer>
{
    public MemoryAnalyzerLegacyAdapter() : base(new MemoryAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.MemoryAnalysis;
}
