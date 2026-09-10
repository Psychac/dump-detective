using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Runs the retyped <see cref="LohFragmentationAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
public sealed class LohFragmentationAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<LohFragmentationAnalyzer>
{
    public LohFragmentationAnalyzerLegacyAdapter() : base(new LohFragmentationAnalyzer())
    {
    }
}
