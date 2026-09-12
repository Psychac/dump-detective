using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Types;

/// <summary>
/// Runs the retyped <see cref="BoxingAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-1 mop-up batch.
/// </summary>
public sealed class BoxingAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<BoxingAnalyzer>
{
    public BoxingAnalyzerLegacyAdapter() : base(new BoxingAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.BoxingAnalysis;
}
