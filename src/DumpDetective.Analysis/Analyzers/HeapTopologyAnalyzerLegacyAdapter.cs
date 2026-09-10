using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Runs the retyped <see cref="HeapTopologyAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md. No <c>ResolveOptions</c>
/// override — this analyzer never read a per-run options record even before retyping (only the
/// static <c>HeapTopologyAnalyzerOptions</c> constants).
/// </summary>
public sealed class HeapTopologyAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<HeapTopologyAnalyzer>
{
    public HeapTopologyAnalyzerLegacyAdapter() : base(new HeapTopologyAnalyzer())
    {
    }
}
