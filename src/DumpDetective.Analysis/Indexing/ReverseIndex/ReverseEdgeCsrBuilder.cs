using System.Diagnostics;

using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.ReverseIndex;

/// <summary>
/// Phase B: builds the reverse-edge index as true CSR — docs/cache/cache-format-clean-slate-redesign.md
/// §2 — replacing the address-keyed hash-bucket-sort-directory format entirely. Resolves every raw
/// <c>(child, parent)</c> address pair Phase A extracted into <c>(childRow, parentRow)</c> against
/// the walk's own sorted reachable-address set, counts in-degree per row, prefix-sums it into
/// <c>Offsets[R+1]</c>, then fills <c>Children[E]</c> with parent row indices.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the resolver is a plain in-memory binary search, not the scratch-file pattern format doc
/// §2.2.1 specifies.</b> §2.2.1 was written for the general case (any address in the full N-object
/// space) and correctly rules out <see cref="ObjectAddressLookup"/> (needs a finished container) in
/// favor of the scratch-file metadata lookup Stage B already uses. The reverse index is narrower:
/// it only ever needs to resolve addresses this same walk already discovered, and
/// <c>ReachableGraphWalker</c> guarantees by construction that both endpoints of every edge it hands
/// to <see cref="ReverseEdgeExtractor.RecordEdge"/> end up in its own <c>ReachableAddresses</c> output
/// — a parent is only ever the walk's current node (already added when dequeued), and a child is
/// added to the visited set in the same statement the edge is recorded. That sorted array is already
/// sitting in memory as a walk result by the time this runs (also what
/// <see cref="Dominator.DominatorReachableAddressWriter"/> persists), so resolution needs no disk
/// access, no container, and — because it is total by that same construction argument — no
/// "resolver miss" fallback path either. A miss here means the invariant above broke, which is
/// exactly why <see cref="ResolveRow"/> throws instead of degrading.
/// </para>
/// <para>
/// <b>Why no locking is needed across buckets.</b> <see cref="ReverseIndexConstants.ChildBucketHash"/>
/// partitions edges by the child address, so every edge sharing a child address lands in exactly one
/// bucket. Row resolution is a bijection, so the same holds for row indices: two different buckets
/// never touch the same row of <c>degree</c>/<c>offsets</c>/<c>cursor</c>/<c>children</c>. Each
/// bucket's contribution during both the counting and filling passes is therefore a plain array
/// write into indices no other concurrently-running bucket can also be writing — the same
/// disjoint-ownership argument the dominator child index reader's in-memory inversion relies on
/// (docs/cache/cache-format-clean-slate-redesign.md §4), applied at build time instead of query time.
/// </para>
/// </remarks>
internal static class ReverseEdgeCsrBuilder
{
    private const long MaxBucketSize = 600 * 1024 * 1024;

    public static async Task<ReverseEdgeCsrResult> BuildAsync(
        string cacheDir,
        int bucketCount,
        ulong[] sortedReachableAddresses,
        CancellationToken ct,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        int rowCount = sortedReachableAddresses.Length;
        var degree = new int[rowCount];
        var resolvedBuckets = new ResolvedBucket[bucketCount];

        // Bounded concurrency for the same reason ReverseEdgeSorter capped it: each bucket loads its
        // whole raw file into memory to resolve it. Unlike the retired sorter, this array is kept
        // resident across the barrier below rather than discarded per bucket — see the type doc's
        // remarks on why that bound is a size cap, not a memory-safety requirement, at the scale
        // measured so far.
        int maxParallelism = Math.Min(Environment.ProcessorCount, 4);
        long completedResolve = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, bucketCount),
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct },
            async (i, token) =>
            {
                ResolvedBucket bucket = await Task.Run(
                    () => LoadAndResolveBucket(cacheDir, i, bucketCount, sortedReachableAddresses), token);
                resolvedBuckets[i] = bucket;

                int[] childRows = bucket.ChildRows;
                for (int e = 0; e < childRows.Length; e++)
                    degree[childRows[e]]++;

                long done = Interlocked.Increment(ref completedResolve);
                progress?.Report(new AnalyzerProgressReport(0, "building reverse-index CSR",
                    Detail: $"{done}/{bucketCount} buckets resolved (bucket {i + 1}: {bucket.ChildRows.Length:N0} edges, {bucket.ElapsedMs / 1000.0:F1}s)",
                    Elapsed: stopwatch.Elapsed));
            });

        progress?.Report(new AnalyzerProgressReport(0, "building reverse-index CSR",
            Detail: "prefix-summing offsets", Elapsed: stopwatch.Elapsed));

        var offsets = new int[rowCount + 1];
        for (int row = 0; row < rowCount; row++)
            offsets[row + 1] = offsets[row] + degree[row];

        int totalEdges = offsets[rowCount];
        var children = new int[totalEdges];
        var cursor = (int[])offsets.Clone();

        progress?.Report(new AnalyzerProgressReport(0, "building reverse-index CSR",
            Detail: $"filling {totalEdges:N0} child entries", Elapsed: stopwatch.Elapsed));

        Parallel.For(0, bucketCount, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct }, i =>
        {
            ResolvedBucket bucket = resolvedBuckets[i];
            int[] childRows = bucket.ChildRows;
            int[] parentRows = bucket.ParentRows;
            for (int e = 0; e < childRows.Length; e++)
            {
                int row = childRows[e];
                children[cursor[row]++] = parentRows[e];
            }
        });

        return new ReverseEdgeCsrResult(offsets, children, totalEdges);
    }

    private static ResolvedBucket LoadAndResolveBucket(
        string cacheDir, int bucketIdx, int bucketCount, ulong[] sortedReachableAddresses)
    {
        var sw = Stopwatch.StartNew();
        string path = Path.Combine(cacheDir, $"reverse_edges_bucket_{bucketIdx}{ReverseIndexConstants.TemporaryScratchSuffix}");

        if (!File.Exists(path))
            return new ResolvedBucket(Array.Empty<int>(), Array.Empty<int>(), (int)sw.ElapsedMilliseconds);

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaxBucketSize)
        {
            throw new InvalidOperationException(
                $"Bucket {bucketIdx}/{bucketCount} exceeds {MaxBucketSize} bytes ({fileInfo.Length}). " +
                "Increase bucket count and re-run extraction.");
        }

        long edgeCount = fileInfo.Length / 16;
        var childRows = new int[edgeCount];
        var parentRows = new int[edgeCount];

        using (var fs = File.OpenRead(path))
        using (var reader = new BinaryReader(fs))
        {
            for (long i = 0; i < edgeCount; i++)
            {
                ulong child = reader.ReadUInt64();
                ulong parent = reader.ReadUInt64();
                childRows[i] = ResolveRow(sortedReachableAddresses, child);
                parentRows[i] = ResolveRow(sortedReachableAddresses, parent);
            }
        }

        TryDelete(path);

        return new ResolvedBucket(childRows, parentRows, (int)sw.ElapsedMilliseconds);
    }

    private static int ResolveRow(ulong[] sortedReachableAddresses, ulong address)
    {
        int row = Array.BinarySearch(sortedReachableAddresses, address);
        if (row < 0)
        {
            throw new InvalidOperationException(
                $"Address 0x{address:X} recorded in a reverse-edge scratch bucket was not found in the " +
                "walk's own reachable-address set. Every edge ReachableGraphWalker records has both " +
                "endpoints guaranteed reachable by construction, so this indicates a correctness bug in " +
                "the walk or the extractor, not a legitimate resolver miss.");
        }

        return row;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private readonly record struct ResolvedBucket(int[] ChildRows, int[] ParentRows, int ElapsedMs);
}

/// <summary>Result of a completed CSR build — the exact bytes <c>ReverseEdgeContainerWriter</c> persists.</summary>
internal sealed class ReverseEdgeCsrResult(int[] offsets, int[] children, long totalEdges)
{
    /// <summary>Length = reachable-node count + 1. <c>Offsets[r]..Offsets[r+1]</c> slices <see cref="Children"/> for row <c>r</c>.</summary>
    public int[] Offsets { get; } = offsets;

    /// <summary>Flat column of parent row indices, grouped by child row.</summary>
    public int[] Children { get; } = children;

    public long TotalEdges { get; } = totalEdges;
}
