using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers.Gc;

/// <summary>
/// Runs the retyped <see cref="FinalizableObjectAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Carries the pre-retyping analyzer's own <c>IRequiresReachableGraphIndex</c>/
/// <c>IRequiresDominatorTreeIndex</c> markers directly, same as <c>GCRootAnalyzerLegacyAdapter</c> —
/// <c>DiskBackedObjectIndexWriter.Build</c>'s Stage B gating checks the registered
/// <c>Core.Abstractions.IAnalyzer</c> instances (this adapter, post-retyping), not the inner
/// <see cref="FinalizableObjectAnalyzer"/>, which the pipeline never sees directly.
/// </remarks>
public sealed class FinalizableObjectAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<FinalizableObjectAnalyzer>, IRequiresReachableGraphIndex, IRequiresDominatorTreeIndex
{
    public FinalizableObjectAnalyzerLegacyAdapter() : base(new FinalizableObjectAnalyzer())
    {
    }
}
