using System.Numerics;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// The reachability walk, keyed by object row instead of by a hash of every address it has seen —
/// R2/R3 of docs/cache/cache-ideal-design.md §3.1.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReachableGraphWalker"/> identifies nodes with a <c>Dictionary&lt;ulong,int&gt;</c>
/// that assigns dense ids in discovery order. That dictionary measured **2,325.9 MB resident** at
/// the 27.5 GB dump's 87.1M objects (§7.2), and it forced three more structures to exist: an
/// <c>addresses</c> array to recover an id's address, and <c>edgeFrom</c>/<c>edgeTo</c>
/// <see cref="ChunkedBuffer{T}"/>s (~1.1 GB) to hold every edge until ids were all assigned.
/// </para>
/// <para>
/// Object rows replace all of it. A row already names its address, so no id→address array is needed
/// beyond what Stage B consumes; and membership is one bit, so the visited set is
/// <c>objectCount / 8</c> bytes — 10.4 MB where the dictionary was 2.3 GB.
/// </para>
/// <para><b>Two phases, because dense reachable-row ids cannot be assigned until the set is known:</b></para>
/// <list type="number">
///   <item>BFS over object rows, recording membership in a bitmap. Nothing else is retained.</item>
///   <item>Rank the bitmap into dense reachable rows, then walk each reachable row's successors
///   again to build the CSR directly in reachable-row space.</item>
/// </list>
/// <para>
/// Phase 2 costs one extra pass over successor data already on disk, and buys the CSR *already
/// keyed by row*: ids ascend by address, so <see cref="DominatorRowMapping"/> becomes the identity
/// and Stage B's re-keying disappears. Iterating parents in ascending order also means the forward
/// CSR needs no sort — the edges come out grouped by parent by construction.
/// </para>
/// <para>
/// Lengauer–Tarjan is unaffected by the id ordering change: it does its own DFS numbering, and
/// <c>DominatorTreeComputer</c> notes that "predecessor list [order] is irrelevant to LT — the
/// semidominator loop takes a minimum over the whole" list. <see cref="LeafFolder"/> assigns its
/// own dense ids "preserving relative order", so it does not require a particular one either.
/// </para>
/// </remarks>
internal static class RowKeyedGraphWalker
{
    /// <param name="rootAddresses">GC root object addresses to seed from.</param>
    /// <param name="successors">
    /// Successor lookup by address — the persisted forward-edge loose files, or a live ClrMD walk.
    /// Called once per reachable node in phase 1 and again in phase 2.
    /// </param>
    /// <param name="rows">Mid-build <c>address ↔ object row</c> resolver over the scratch columns.</param>
    /// <param name="objectCount">Total object rows, sizing the bitmap.</param>
    /// <param name="buildCsr">
    /// When false only phase 1 runs, yielding membership and nothing else — the cheap Stage-A-only
    /// shape. The CSR is built separately from the same bitmap when a caller needs it, so unlike
    /// <c>ReachableGraphWalker.WalkWithoutCsr</c> this does not leave the reverse index without a
    /// source (Part F §F.2's objection to the earlier C.2 framing).
    /// </param>
    public static RowKeyedWalkResult Walk(
        IReadOnlyList<ulong> rootAddresses,
        SuccessorsFunc successors,
        IObjectRowResolver rows,
        long objectCount,
        bool buildCsr,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        var visited = new ulong[(objectCount + 63) / 64];
        var childBuffer = new ulong[64];

        // Out-degree is recorded during phase 1 rather than re-counted in phase 2, which removes an
        // entire pass over successors. Sound because a child counts iff it resolves to a valid
        // object row, and that is independent of visit order: every child of a reached node is
        // itself reached, so "resolves to an object row" and "is in the reachable graph" coincide.
        // One byte per object row (10.4 MB at 87.1M) with a side table for the rare hub above 254.
        byte[]? outDegreeByRow = buildCsr ? new byte[objectCount] : null;
        Dictionary<long, int>? outDegreeOverflow = buildCsr ? new Dictionary<long, int>() : null;

        long reachableCount = WalkMembership(
            rootAddresses, successors, rows, visited, outDegreeByRow, outDegreeOverflow,
            ref childBuffer, cancellationToken, progress);

        if (!buildCsr)
            return RowKeyedWalkResult.MembershipOnly(visited, objectCount, reachableCount);

        return BuildCsr(
            rootAddresses, successors, rows, visited, outDegreeByRow!, outDegreeOverflow!,
            objectCount, reachableCount, ref childBuffer, cancellationToken, progress);
    }

    /// <summary>
    /// Phase 1: BFS recording only membership. The frontier holds object rows, so it is
    /// <c>int</c>-wide rather than address-wide, and nothing per-edge is retained at all.
    /// </summary>
    private static long WalkMembership(
        IReadOnlyList<ulong> rootAddresses,
        SuccessorsFunc successors,
        IObjectRowResolver rows,
        ulong[] visited,
        byte[]? outDegreeByRow,
        Dictionary<long, int>? outDegreeOverflow,
        ref ulong[] childBuffer,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress)
    {
        var scanCounter = new ObjectScanCounter("tracing heap graph (reachability)", progress);
        var frontier = new Queue<long>();
        long reachableCount = 0;

        foreach (ulong rootAddress in rootAddresses)
        {
            if (rootAddress == 0)
                continue;

            long row = rows.TryGetRow(rootAddress);
            if (row < 0)
                continue;

            if (TrySetBit(visited, row))
            {
                reachableCount++;
                frontier.Enqueue(row);
            }
        }

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long row = frontier.Dequeue();
            scanCounter.Tick();

            int childCount = successors(rows.GetAddress(row), ref childBuffer);
            int kept = 0;
            for (int c = 0; c < childCount; c++)
            {
                ulong childAddress = childBuffer[c];
                if (childAddress == 0)
                    continue;

                long childRow = rows.TryGetRow(childAddress);
                if (childRow < 0)
                    continue;

                kept++;

                if (TrySetBit(visited, childRow))
                {
                    reachableCount++;
                    frontier.Enqueue(childRow);
                }
            }

            if (outDegreeByRow is not null)
            {
                if (kept >= DegreeEscape)
                {
                    outDegreeByRow[row] = DegreeEscape;
                    outDegreeOverflow![row] = kept;
                }
                else
                {
                    outDegreeByRow[row] = (byte)kept;
                }
            }
        }

        scanCounter.Complete();
        return reachableCount;
    }

    /// <summary>
    /// Phase 2: assign dense reachable rows by rank, then rebuild the graph in that space. Walks
    /// parents in ascending row order, so the forward CSR falls out grouped by parent with no sort;
    /// the reverse CSR is then a counting sort over the forward targets, needing no successor pass
    /// of its own.
    /// </summary>
    private static RowKeyedWalkResult BuildCsr(
        IReadOnlyList<ulong> rootAddresses,
        SuccessorsFunc successors,
        IObjectRowResolver rows,
        ulong[] visited,
        byte[] outDegreeByRow,
        Dictionary<long, int> outDegreeOverflow,
        long objectCount,
        long reachableCount,
        ref ulong[] childBuffer,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress)
    {
        int nodeCount = checked((int)reachableCount);

        // objectRowOf is select(); reachableRowOf is rank(), as a sparse lookup over the bitmap's
        // own superblock sums rather than a second dense array over objectCount.
        var objectRowOf = new int[nodeCount];
        var superblockRanks = new int[(visited.Length + SuperblockWords - 1) / SuperblockWords];

        int assigned = 0;
        for (int w = 0; w < visited.Length; w++)
        {
            if (w % SuperblockWords == 0)
                superblockRanks[w / SuperblockWords] = assigned;

            ulong word = visited[w];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                objectRowOf[assigned++] = (w << 6) + bit;
                word &= word - 1;
            }
        }

        var addresses = new ulong[nodeCount];
        for (int id = 0; id < nodeCount; id++)
            addresses[id] = rows.GetAddress(objectRowOf[id]);

        var isRoot = new bool[nodeCount];
        foreach (ulong rootAddress in rootAddresses)
        {
            if (rootAddress == 0)
                continue;

            long objectRow = rows.TryGetRow(rootAddress);
            if (objectRow < 0)
                continue;

            int id = Rank(visited, superblockRanks, objectRow);
            if (id >= 0)
                isRoot[id] = true;
        }

        // Degrees came from phase 1, so this is arithmetic rather than a second traversal.
        var outDegree = new int[nodeCount];
        long edgeTotal = 0;
        for (int id = 0; id < nodeCount; id++)
        {
            long objectRow = objectRowOf[id];
            byte stored = outDegreeByRow[objectRow];
            int degree = stored == DegreeEscape ? outDegreeOverflow[objectRow] : stored;
            outDegree[id] = degree;
            edgeTotal += degree;
        }

        var scanCounter = new ObjectScanCounter("tracing heap graph (building reference graph)", progress);

        var fwdOffsets = new int[nodeCount + 1];
        for (int id = 0; id < nodeCount; id++)
            fwdOffsets[id + 1] = fwdOffsets[id] + outDegree[id];

        var fwdTargets = new int[checked((int)edgeTotal)];
        for (int id = 0; id < nodeCount; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanCounter.Tick();

            int childCount = successors(addresses[id], ref childBuffer);
            int cursor = fwdOffsets[id];
            for (int c = 0; c < childCount; c++)
            {
                ulong childAddress = childBuffer[c];
                if (childAddress == 0)
                    continue;

                long childObjectRow = rows.TryGetRow(childAddress);
                if (childObjectRow < 0)
                    continue;

                int childId = Rank(visited, superblockRanks, childObjectRow);
                if (childId >= 0)
                    fwdTargets[cursor++] = childId;
            }
        }

        scanCounter.Complete();

        // Pass 2b — reverse CSR by counting sort over the forward targets. No successor lookups.
        var inDegree = new int[nodeCount];
        for (int e = 0; e < fwdTargets.Length; e++)
            inDegree[fwdTargets[e]]++;

        var revOffsets = new int[nodeCount + 1];
        for (int id = 0; id < nodeCount; id++)
            revOffsets[id + 1] = revOffsets[id] + inDegree[id];

        var revTargets = new int[fwdTargets.Length];
        var revCursor = (int[])revOffsets.Clone();
        for (int id = 0; id < nodeCount; id++)
        {
            for (int e = fwdOffsets[id]; e < fwdOffsets[id + 1]; e++)
                revTargets[revCursor[fwdTargets[e]]++] = id;
        }

        return new RowKeyedWalkResult
        {
            ObjectRowCount = objectCount,
            VisitedBitmap = visited,
            NodeCount = nodeCount,
            EdgeCount = fwdTargets.Length,
            ObjectRowOf = objectRowOf,
            Addresses = addresses,
            OutDegree = outDegree,
            InDegree = inDegree,
            IsRoot = isRoot,
            FwdOffsets = fwdOffsets,
            FwdTargets = fwdTargets,
            RevOffsets = revOffsets,
            RevTargets = revTargets,
        };
    }

    private const int SuperblockWords = 8;

    /// <summary>Out-degrees at or above this escape to a side table. Hub objects only.</summary>
    private const byte DegreeEscape = byte.MaxValue;

    /// <summary>
    /// <paramref name="objectRow"/>'s dense reachable id, or -1 if it is not reachable. Superblock
    /// sums keep this O(1) without a dense rank array over every object row.
    /// </summary>
    private static int Rank(ulong[] visited, int[] superblockRanks, long objectRow)
    {
        long word = objectRow >> 6;
        ulong mask = 1UL << (int)(objectRow & 63);
        if ((visited[word] & mask) == 0)
            return -1;

        long superblock = word / SuperblockWords;
        int rank = superblockRanks[superblock];
        for (long w = superblock * SuperblockWords; w < word; w++)
            rank += BitOperations.PopCount(visited[w]);

        return rank + BitOperations.PopCount(visited[word] & (mask - 1));
    }

    private static bool TrySetBit(ulong[] bits, long index)
    {
        ulong mask = 1UL << (int)(index & 63);
        ref ulong word = ref bits[index >> 6];
        if ((word & mask) != 0)
            return false;

        word |= mask;
        return true;
    }
}

/// <summary>
/// A row-keyed walk's output. Ids are dense reachable rows in ascending address order, so any
/// row-aligned column keyed by them matches <c>ReachableRowBitmap</c>'s ordering directly and
/// <see cref="DominatorRowMapping"/> is the identity.
/// </summary>
internal sealed class RowKeyedWalkResult
{
    public required long ObjectRowCount { get; init; }

    /// <summary>Membership over object rows — persisted as <c>ReachableRowBitmap</c> unchanged.</summary>
    public required ulong[] VisitedBitmap { get; init; }

    public required int NodeCount { get; init; }
    public required long EdgeCount { get; init; }

    /// <summary>Dense id → object row. Empty when only membership was requested.</summary>
    public required int[] ObjectRowOf { get; init; }

    public required ulong[] Addresses { get; init; }
    public required int[] OutDegree { get; init; }
    public required int[] InDegree { get; init; }
    public required bool[] IsRoot { get; init; }
    public required int[] FwdOffsets { get; init; }
    public required int[] FwdTargets { get; init; }
    public required int[] RevOffsets { get; init; }
    public required int[] RevTargets { get; init; }

    public static RowKeyedWalkResult MembershipOnly(ulong[] visited, long objectRowCount, long reachableCount) =>
        new()
        {
            ObjectRowCount = objectRowCount,
            VisitedBitmap = visited,
            NodeCount = checked((int)reachableCount),
            EdgeCount = 0,
            ObjectRowOf = [],
            Addresses = [],
            OutDegree = [],
            InDegree = [],
            IsRoot = [],
            FwdOffsets = [],
            FwdTargets = [],
            RevOffsets = [],
            RevTargets = [],
        };
}
