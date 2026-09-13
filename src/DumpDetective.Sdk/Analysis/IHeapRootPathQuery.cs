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
/// capability boundary already exists to hide. <see cref="TryFindRootPathAddresses"/> below is the
/// first consumer that genuinely needs the raw addresses for something beyond formatting.
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
///
/// <b>Re-confirmed, not re-attempted, during <c>ReferenceChainAnalyzer</c>'s own retyping
/// (2026-09-13)</b> — that analyzer is the actual owner of <c>KnownLeakTypePatterns</c>/force-expand
/// (<c>CollectionAnalyzer</c> above only shared the same option type). Its retyping confirmed, user
/// sign-off included, that force-expand is dropped for the same already-documented reason, not
/// reinvented: nothing here re-litigates the 2026-09-12 finding. The same batch also dropped
/// <c>ReferenceChainAnalyzer</c>'s own pre-retyping root-priority ordering (tried
/// stack-then-static-then-handle roots first, to report a "faster to resolve" root when an object
/// had several) — traced into <c>BidirectionalGraphSearch</c>/<c>IndexBackedBidirectionalSearch</c>
/// to confirm the real impact before dropping it: on the common, reverse-index-backed path (nearly
/// all real-dump analysis), every root seeds the search simultaneously and the *shortest* path wins
/// — root list order has no effect on the outcome there at all. It only ever mattered on the
/// uncommon no-reverse-index fallback (<c>RootPathFinder.TryFindAnyRootPath</c>'s non-index branch),
/// which tries roots one at a time in list order and returns the first hit — there, dropping the
/// order means a different, equally-valid root can be reported instead of the old "stack first"
/// pick, which itself was a hand-picked "fastest to resolve" guess, not a measured "most useful"
/// one. This capability has no per-call ordering knob and none was added for this — a real, if
/// narrow, behavior difference confined to that one uncommon path, accepted rather than adding a
/// speculative ordering parameter no other consumer needs.
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

    /// <summary>
    /// Same search as <see cref="TryFindRootPath"/>, but returns the per-hop type-name list instead
    /// of a single formatted string. Added 2026-09-13 for <c>DominatorAnalyzer</c>'s retyping: its
    /// root-chain summary (<c>RootChainSummary.HopTypeNames</c>) renders each hop as a list entry,
    /// which <see cref="TryFindRootPath"/>'s pre-joined string can't be reconstructed into. The two
    /// overloads deliberately coexist rather than one replacing the other — every other consumer
    /// only ever needed the formatted string for direct display.
    /// </summary>
    bool TryFindRootPathHops(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<string>? hopTypeNames, out bool searchTruncated);

    /// <summary>
    /// Same search as <see cref="TryFindRootPath"/>, but returns the raw per-hop address list
    /// (root-most first, target last) instead of a formatted string or type-name list. Added
    /// 2026-09-13 for <c>ReferenceChainAnalyzer</c>'s retyping: it needs the root object's own
    /// address (the first hop) for its shared-root grouping, and the last two hops' addresses to
    /// resolve the field name connecting them via <see cref="IHeapReferenceQuery.TryGetReferenceFieldName"/>
    /// — neither existing overload exposes addresses. Callers needing display text build it
    /// themselves from these addresses plus <see cref="IHeapObjectLookup"/>, the same way the
    /// pre-retyping analyzers that only needed a string already did internally.
    /// </summary>
    bool TryFindRootPathAddresses(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<ulong>? hopAddresses, out bool searchTruncated);
}
