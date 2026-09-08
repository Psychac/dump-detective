namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// §3/§10.3 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): opt-in marker for
/// analyzers that need Stage A's reachability walk / reverse-edge index to have actually run — the
/// broad interface (every current <see cref="Core.Abstractions.IBackwardReferenceProvider"/> consumer
/// implements this, since Stage A now backs that interface for everyone, §7). No members: gating only
/// ever checks <c>activeAnalyzers.Any(a => a is IRequiresReachableGraphIndex)</c>, the same shape as
/// <see cref="IParallelHeapIndexScanParticipant"/>'s opt-in convention but without any behavior to
/// implement.
///
/// Not yet consumed by <c>DiskBackedObjectIndexWriter.Build</c>'s gating — Stage A's construction
/// stays unconditional (gated only by <c>SkipReverseIndexBuild</c>) for now; wiring this interface to
/// skip Stage A's build entirely when no analyzer wants it is deliberately deferred (§10.3), since that
/// would change already-shipped Stage A's behavior. Applying it to every analyzer that wants it now
/// means the information is correct and ready whenever that gating is added, rather than needing an
/// audit of every analyzer at that later point.
/// </summary>
internal interface IRequiresReachableGraphIndex
{
}
