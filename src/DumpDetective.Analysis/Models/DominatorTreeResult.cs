using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Models;

/// <summary>
/// Output of the exact dominator-tree computation (design doc
/// docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md §Output Model) — distinct from
/// <see cref="DominatorDomainResult"/>, which carries the existing top-K reference-count heuristic
/// this doc doesn't touch. Not yet wired into <c>DominatorAnalyzer</c> (see the implementation
/// plan's Phase 5) — these records exist now so Phase 4/5 have a stable target shape.
/// </summary>
internal sealed record DominatorTreeResult(
    DominatorTreeMode Mode,
    string? FallbackReason,
    /// <summary>Reachable population size (§D2) — excludes dead-not-yet-swept objects.</summary>
    int NodeCount,
    IReadOnlyList<DominatorNodeSnapshot> TopByRetainedBytes,
    IReadOnlyList<DominatorTypeRollup> TopTypesByRetainedBytes);

internal enum DominatorTreeMode
{
    Exact,
    HeuristicFallback,
}

internal sealed record DominatorNodeSnapshot(
    ulong Address,
    string TypeName,
    ulong ShallowSize,
    ulong ExactRetainedBytes,
    /// <summary>Null only for a direct child of the virtual root.</summary>
    ulong? ImmediateDominatorAddress,
    int DominatorTreeDepth,
    /// <summary>Report-filter only (§D1) — never decided graph/node membership.</summary>
    GenerationTag GenerationTag);

internal sealed record DominatorTypeRollup(
    string TypeName,
    int ObjectCount,
    ulong ExactRetainedBytes);
