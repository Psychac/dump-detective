using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// §4.1 prototype: the same reachability BFS as <see cref="ReachableGraphWalker"/>, but the
/// in-memory <c>edgeFrom</c>/<c>edgeTo</c> edge lists become direct writes into a
/// <see cref="ReverseEdgeExtractor"/>, mirroring exactly how the existing reverse-edge index streams
/// edges to per-bucket scratch files during the raw heap scan — here fed from the BFS instead. See
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §4.
///
/// Deliberately does not (yet) implement §4.2's hub-overflow cap removal — it reuses
/// <see cref="ReverseEdgeExtractor"/> unmodified, so <see cref="ReverseIndexConstants.MaxParentsPerChild"/>
/// still applies.
///
/// Does not build a CSR or resolve per-node metadata (§Architecture step 2-3) — that's Stage B's
/// job, downstream of whatever ends up consuming this walk reverse index, and out of scope for
/// this prototype's isolated measurement.
///
/// History (§7.3 item 2's measurement rounds, kept here because the design this class landed on is
/// the *product* of that history, not an arbitrary pick):
/// - v1 replaced <c>DenseIdMap</c> (13 bytes/reachable node, hash-based; since removed, see
///   ReachableGraphWalker.cs — deleted when it was replaced with a plain
///   <see cref="Dictionary{TKey,TValue}"/> there) with an
///   ordinal-indexed <c>BitArray</c> (via <c>ObjectAddressLookup.TryGetOrdinal</c>'s binary search) —
///   measured ~30% *slower* than <c>DenseIdMap</c> for the walk alone, because both approaches check
///   "have I seen this" once per edge occurrence, and a binary search costs more per call than an
///   O(1) hash probe.
/// - v2 added a bounded direct-mapped <c>address -&gt; ordinal</c> cache in front of the binary
///   search, catching real heaps' power-law in-degree distribution. This closed the walk-only gap
///   against <c>DenseIdMap</c>, but a same-shape ablation then showed a plain <see cref="HashSet{T}"/>
///   was still ~37% *faster* than the cached ordinal approach, with peak working set a wash across
///   every variant tried — the "avoid DenseIdMap's peak-memory cost" motivation this design started
///   from was never actually demonstrated on the 3.3GB test dump.
/// - v3 (this version) dropped the ordinal/bitset/cache apparatus entirely for plain
///   <see cref="HashSet{T}"/>-based visited-tracking — simpler, faster, and removed the
///   <c>ObjectAddressLookup</c> dependency altogether. A further ablation split the walk's cost into
///   successors()+visited-tracking (~3.5s) vs. <see cref="ReverseEdgeExtractor.RecordEdge"/> (~4.3s,
///   ~55% of the walk).
/// - A v4 attempt batched edges per bucket and flushed via
///   <see cref="ReverseEdgeExtractor.RecordEdgesBatch"/> instead of calling <c>RecordEdge</c> once
///   per edge, on the theory that RecordEdge's per-call lock was the dominant cost (as the
///   extractor's own doc comment suggests for its *parallel*, multi-worker caller during the raw heap
///   scan). Measured **slower** than v3 (11,033 ms vs. 10,045 ms total), not faster — this walker
///   runs single-threaded, so there's no lock contention for batching to amortize away; RecordEdge's
///   cost here is the `Dictionary&lt;ulong,int&gt;` fanout lookup/update plus the two
///   `BinaryWriter.Write` calls themselves, not the lock, and batching only added List&lt;T&gt;
///   bookkeeping overhead on top. Reverted — v3 (per-edge `RecordEdge`) is the current, best-measured
///   version.
/// </summary>
internal static class IncrementalReachableWalker
{
    /// <param name="NodeCount">Reachable nodes visited.</param>
    /// <param name="EdgeCount">Forward edges discovered and streamed to <see cref="ReverseEdgeExtractor"/>.</param>
    public readonly record struct Result(int NodeCount, long EdgeCount);

    public static Result Walk(
        IReadOnlyList<ulong> rootAddresses,
        SuccessorsFunc successors,
        ReverseEdgeExtractor edgeExtractor,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        var visited = new HashSet<ulong>();
        var frontier = new Queue<ulong>();
        var scanCounter = new ObjectScanCounter("computing exact dominator tree (incremental walk prototype)", progress);
        var childBuffer = new ulong[64];

        int nodeCount = 0;
        long edgeCount = 0;

        foreach (ulong rootAddr in rootAddresses)
        {
            if (rootAddr == 0)
                continue;

            if (visited.Add(rootAddr))
            {
                nodeCount++;
                frontier.Enqueue(rootAddr);
            }
        }

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong address = frontier.Dequeue();
            scanCounter.Tick();

            int childCount = successors(address, ref childBuffer);
            for (int c = 0; c < childCount; c++)
            {
                ulong childAddr = childBuffer[c];
                if (childAddr == 0)
                    continue;

                edgeExtractor.RecordEdge(address, childAddr);
                edgeCount++;

                if (visited.Add(childAddr))
                {
                    nodeCount++;
                    frontier.Enqueue(childAddr);
                }
            }
        }

        scanCounter.Complete();
        return new Result(nodeCount, edgeCount);
    }
}
