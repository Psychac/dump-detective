using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// <see cref="IThreadRetentionProvider"/> backed by the <c>RootStackThreadAttribution</c> section
/// cross-referenced with <c>Roots</c>' Stack-kind entries and an already-open
/// <see cref="IDominatorTreeProvider"/> — §12.2
/// (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md). Every thread's retained
/// bytes are computed once, eagerly, at open time — thread counts and per-thread stack-root counts
/// are both small, so this is cheap relative to a per-query resolution, and every caller of a
/// provider like this ends up wanting most or all threads' numbers anyway (a report section, not a
/// single targeted query).
/// </summary>
internal sealed class ThreadRetentionReaderProvider : IThreadRetentionProvider
{
    // Byte value matches Microsoft.Diagnostics.Runtime.ClrRootKind.Stack exactly — see
    // RootIndexReader.KindToString's equivalent mapping.
    private const byte StackKind = 4;

    private readonly Dictionary<uint, ulong> _retainedBytesByOSThreadId;

    private ThreadRetentionReaderProvider(Dictionary<uint, ulong> retainedBytesByOSThreadId)
    {
        _retainedBytesByOSThreadId = retainedBytesByOSThreadId;
    }

    /// <summary>
    /// Returns <c>false</c> — same as any missing/corrupt satellite section — if either the
    /// <c>RootStackThreadAttribution</c> section or the <c>Roots</c> section is absent (a legacy
    /// pre-§12.2 cache.bin, or <c>SkipRootIndexBuild</c> was set at build time).
    /// </summary>
    public static bool TryOpen(
        CacheContainerReader container,
        IDominatorTreeProvider treeProvider,
        CancellationToken cancellationToken,
        out ThreadRetentionReaderProvider? provider)
    {
        provider = null;

        Dictionary<ulong, (uint OSThreadId, int ManagedThreadId)> stackRootOwners =
            RootStackThreadIndexReader.Read(container, cancellationToken);
        if (stackRootOwners.Count == 0)
            return false;

        List<(ulong TargetAddr, ulong RootAddr, byte Kind)> roots =
            RootIndexReader.ReadRootIndexFile(container, cancellationToken);
        if (roots.Count == 0)
            return false;

        var targetsByOSThreadId = new Dictionary<uint, List<ulong>>();
        for (int i = 0; i < roots.Count; i++)
        {
            (ulong targetAddr, ulong rootAddr, byte kind) = roots[i];
            if (kind != StackKind)
                continue;

            if (!stackRootOwners.TryGetValue(rootAddr, out (uint OSThreadId, int ManagedThreadId) owner))
                continue;

            if (!targetsByOSThreadId.TryGetValue(owner.OSThreadId, out List<ulong>? targets))
                targetsByOSThreadId[owner.OSThreadId] = targets = new List<ulong>();
            targets.Add(targetAddr);
        }

        var retainedBytesByOSThreadId = new Dictionary<uint, ulong>(targetsByOSThreadId.Count);
        foreach (KeyValuePair<uint, List<ulong>> kv in targetsByOSThreadId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            retainedBytesByOSThreadId[kv.Key] = DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes(treeProvider, kv.Value);
        }

        provider = new ThreadRetentionReaderProvider(retainedBytesByOSThreadId);
        return true;
    }

    public bool TryGetRetainedBytesForThread(uint osThreadId, out ulong retainedBytes) =>
        _retainedBytesByOSThreadId.TryGetValue(osThreadId, out retainedBytes);
}
