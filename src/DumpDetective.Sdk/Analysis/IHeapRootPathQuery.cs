namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Bounds governing a single root-path search — mirrors the dump-side
/// <c>RootPathSearchLimits</c> struct every pre-retyping caller of <c>RootPathFinder</c> already
/// constructs its own values for (<c>DbConnectionAnalyzer</c>, <c>TimerLeakAnalyzer</c>). Exposed
/// as call-time options rather than baked into the capability, since the two real consumers
/// already use different numeric limits for their own workloads while agreeing on every other
/// policy (see <see cref="IHeapRootPathQuery"/>'s remarks).
/// </summary>
public readonly record struct RootPathSearchOptions(
    int MaxCandidateNodes,
    int MaxCandidateDepth,
    int MaxRootExpansionDepth,
    int LargeFanoutThreshold);

/// <summary>
/// The <c>heap.root-paths</c> capability — "is this object reachable from a GC root, and if so,
/// what's the chain?" (the <c>!gcroot</c> workflow). Heap-graph-structural, like
/// <see cref="IHeapDominatorQuery"/>/<see cref="IHeapReachabilityQuery"/> — dump-only in every
/// implementation that exists today, but not dump-only *in shape* the way
/// <see cref="IDumpObjectFieldQuery"/> is, so it lives under <c>heap.*</c>, not <c>dump.*</c>.
/// </summary>
/// <remarks>
/// Built for the typed-resource quartet's remaining two members (<c>DbConnectionAnalyzer</c>,
/// <c>TimerLeakAnalyzer</c>, docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) —
/// both pre-retyping callers construct <c>RootPathFinder</c> with identical noise-type filtering
/// (<c>RootPathSearchSupport.IsNoisyType</c>) and force-expand policy (<c>static _ =&gt; false</c>,
/// i.e. neither ever force-expands a type), differing only in <see cref="RootPathSearchOptions"/>'s
/// numeric limits and which candidates they choose to enrich. Since both real consumers agree on
/// everything except the bounds, the capability bakes in the shared policy dump-side and exposes
/// only the bounds as a parameter — not a third, speculative knob neither caller has ever needed.
///
/// Returns a fully-formatted path string (<c>"RootKind: Type@0xAddr -&gt; ... -&gt; Type@0xAddr"</c>,
/// mirroring <c>RootPathSearchSupport.FormatPath</c>'s output, which every real consumer's own
/// domain-result field already stores as a plain string), not a raw address list — resolving each
/// hop's <c>ClrType</c> for display requires the same dump-only type-resolution machinery the
/// capability boundary already exists to hide, and no consumer has ever needed the raw addresses
/// for anything beyond formatting them once.
///
/// <b>A caller-supplied force-expand predicate was added, then removed, the same session</b>
/// (<c>CollectionAnalyzer</c>'s retyping, 2026-09-12): its pre-retyping search force-expanded types
/// matching <c>ReferenceChainOptions.KnownLeakTypePatterns</c>, whose *defaults* are
/// <c>"System.Collections.Generic.List"</c>/<c>"...Dictionary"</c> — i.e. exactly the type family
/// this analyzer's own targets belong to. Wiring that predicate through
/// (<c>IndexBackedBidirectionalSearch</c>'s <c>forceExpand</c> parameter bypasses
/// <c>LargeFanoutThreshold</c> entirely for a matching type) hung a real 3.35 GB dump's root-path
/// search indefinitely: on a real heap, force-expanding every List/Dictionary-shaped node defeats
/// the hub-fanout cap this exact machinery exists to enforce, almost everywhere in the graph. No
/// prior real-dump test exercised this analyzer's root-path search at all, so this was a latent,
/// pre-existing cost the retyping's own real-dump gate caught for the first time — not a regression
/// introduced by moving to this capability. Fixed by keeping this interface single-overload, "never
/// force-expand" for every consumer, same policy <c>DbConnectionAnalyzer</c>/<c>TimerLeakAnalyzer</c>
/// already use.
/// </remarks>
public interface IHeapRootPathQuery
{
    /// <summary>
    /// Searches for any path from a GC root to <paramref name="targetAddress"/>, bounded by
    /// <paramref name="options"/>. <paramref name="searchTruncated"/> is <c>true</c> when the
    /// search hit its bound before concluding no path exists — <c>false</c> alongside a <c>null</c>
    /// <paramref name="formattedPath"/> means "confirmed unreachable within the search space", not
    /// "inconclusive".
    /// </summary>
    bool TryFindRootPath(ulong targetAddress, RootPathSearchOptions options, out string? formattedPath, out bool searchTruncated);
}
