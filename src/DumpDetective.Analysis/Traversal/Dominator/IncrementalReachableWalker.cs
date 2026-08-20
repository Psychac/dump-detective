using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Breadth-first walk from the GC roots that streams every forward edge it crosses directly into
/// a <see cref="ReverseEdgeExtractor"/>, keyed by child — i.e. it builds the reverse-edge index as
/// a side effect of the walk, instead of the index being built by a separate per-object field scan.
///
/// This is the sole production feed for the reverse-edge index (see
/// <c>DiskBackedObjectIndexWriter.Build</c>, called right before the extractor's buckets are
/// sorted and written to disk). Because the walk only visits objects reachable from a root, only
/// reachable objects get reverse-index entries — see
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §7 for why every current
/// consumer of the index (all of which search backward from an object toward a root) is
/// unaffected by that: a garbage object has no path to any root by definition, so it could never
/// have answered such a query anyway.
///
/// Design history (measurement rounds that led to this implementation — kept in the design doc's
/// §8, not here): earlier prototypes tried a dense id-map and an ordinal-indexed bitset in place
/// of the plain <see cref="HashSet{T}"/> used for visited-tracking below, on the theory that
/// avoiding per-node hashing would matter more than it measured to. None beat this version, which
/// also does no batching of edges into <see cref="ReverseEdgeExtractor"/> — this walker runs
/// single-threaded, so there's no lock contention on <see cref="ReverseEdgeExtractor.RecordEdge"/>
/// for batching to amortize away.
/// </summary>
internal static class IncrementalReachableWalker
{
    /// <param name="NodeCount">Reachable nodes visited.</param>
    /// <param name="EdgeCount">Forward edges discovered and streamed to <see cref="ReverseEdgeExtractor"/>.</param>
    /// <param name="ReachableAddresses">
    /// Every visited node's address, sorted ascending — the walk's <c>HashSet&lt;ulong&gt;</c>
    /// visited-tracking already holds this for the walk's duration; returning it costs one sort,
    /// not new peak memory. Persisted as the <c>DominatorReachableAddresses</c> section (see
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §5) by
    /// <c>DominatorReachableAddressWriter</c>.
    /// </param>
    public readonly record struct Result(int NodeCount, long EdgeCount, ulong[] ReachableAddresses);

    public static Result Walk(
        IReadOnlyList<ulong> rootAddresses,
        SuccessorsFunc successors,
        ReverseEdgeExtractor edgeExtractor,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        var visited = new HashSet<ulong>();
        var frontier = new Queue<ulong>();
        var scanCounter = new ObjectScanCounter("building reverse-edge index (reachability walk)", progress);
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

        ulong[] reachableAddresses = visited.ToArray();
        Array.Sort(reachableAddresses);

        return new Result(nodeCount, edgeCount, reachableAddresses);
    }
}
