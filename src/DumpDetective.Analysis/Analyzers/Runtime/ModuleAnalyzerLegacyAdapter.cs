using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Runtime;

/// <summary>
/// Runs the retyped <see cref="ModuleAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Plain adapter, no scan-participant interfaces — unlike <c>StringAnalyzer</c>/<c>CrashAnalyzer</c>/
/// <c>HangAnalyzer</c>, nothing in <see cref="ModuleAnalyzer"/> needs data accumulated during a
/// shared heap-index or thread-stack scan pass; every fact comes from <c>IRuntimeModuleQuery</c> or
/// the already-available <c>IHeapTypeStatisticsQuery</c>, both resolved by the base
/// <see cref="LegacyAnalyzerAdapter{TSdkAnalyzer}.BuildSdkContext"/>.
/// </remarks>
public sealed class ModuleAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<ModuleAnalyzer>
{
    public ModuleAnalyzerLegacyAdapter() : base(new ModuleAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.ModuleAnalysis;
}
