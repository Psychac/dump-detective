using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Services;

internal interface ISynthesisRuleCatalog
{
    IReadOnlyList<Type> RuleTypes { get; }
}

/// <summary>
/// Empty by construction — no <see cref="ISynthesisRule"/> exists anywhere in the codebase yet
/// (Phase 5 step 2 hasn't migrated a single <c>IFindingGenerator</c> to one; see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's "Before step 2" prerequisites).
/// Deliberately not a per-plugin-package aggregation like <c>AnalyzerFeatureModule</c> yet — that
/// shape should follow real rule authoring, not precede it; wiring it in now would be guessing at
/// a registration story with nothing real to test it against.
/// </summary>
internal sealed class DefaultSynthesisRuleCatalog : ISynthesisRuleCatalog
{
    public IReadOnlyList<Type> RuleTypes { get; } = [];
}
