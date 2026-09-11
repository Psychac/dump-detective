using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Types;

/// <summary>
/// Runs the retyped <see cref="ArrayAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
public sealed class ArrayAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<ArrayAnalyzer>
{
    public ArrayAnalyzerLegacyAdapter() : base(new ArrayAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.ArrayAnalysis;
}
