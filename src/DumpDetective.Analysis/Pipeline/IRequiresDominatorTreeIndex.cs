namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// §3/§10.3 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): opt-in marker for
/// analyzers that need Stage B's exact dominator tree (<c>idom[]</c>/retained bytes) specifically, not
/// just the reachable graph/reverse-edge index — the narrow interface. Implies
/// <see cref="IRequiresReachableGraphIndex"/> (every implementer should implement both), since Stage B
/// only ever runs on top of Stage A succeeding.
///
/// This is the interface <c>DiskBackedObjectIndexWriter.Build</c>'s gating actually consumes (§10.3) —
/// <c>activeAnalyzers.Any(a => a is IRequiresDominatorTreeIndex)</c> is one term of
/// <c>buildStageB</c>, alongside <c>RetentionOptions.EnableExactDominatorTree</c> and Stage A having
/// actually run.
/// </summary>
internal interface IRequiresDominatorTreeIndex
{
}
