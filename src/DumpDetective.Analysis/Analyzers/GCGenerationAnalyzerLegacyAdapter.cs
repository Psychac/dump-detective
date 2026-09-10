using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Runs the retyped <see cref="GCGenerationAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. This is what
/// <c>DefaultAnalyzerFeatureModuleCatalog</c> and <c>GCGenerationAnalyzerBenchmark</c> register/
/// construct — the retyped analyzer itself is never referenced as an
/// <c>Core.Abstractions.IAnalyzer</c> directly. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
public sealed class GCGenerationAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<GCGenerationAnalyzer>
{
    public GCGenerationAnalyzerLegacyAdapter() : base(new GCGenerationAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.GCGenerationAnalysis;
}
