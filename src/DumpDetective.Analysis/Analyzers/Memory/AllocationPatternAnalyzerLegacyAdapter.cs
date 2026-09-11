using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Memory;

/// <summary>
/// Runs the retyped <see cref="AllocationPatternAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-1 mop-up batch.
/// </summary>
public sealed class AllocationPatternAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<AllocationPatternAnalyzer>
{
    public AllocationPatternAnalyzerLegacyAdapter() : base(new AllocationPatternAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.AllocationPatternAnalysis;
}
