using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Runs the retyped <see cref="StaticRootLeakDetector"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Plain adapter, no scan-participant interfaces — like <c>ModuleAnalyzer</c>, nothing in
/// <see cref="StaticRootLeakDetector"/> needs data accumulated during a shared heap-index or
/// thread-stack scan pass; it iterates a bounded root population via <c>IHeapRootQuery</c>.
/// </remarks>
public sealed class StaticRootLeakDetectorLegacyAdapter : LegacyAnalyzerAdapter<StaticRootLeakDetector>
{
    public StaticRootLeakDetectorLegacyAdapter() : base(new StaticRootLeakDetector())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.StaticRootLeakAnalysis;
}
