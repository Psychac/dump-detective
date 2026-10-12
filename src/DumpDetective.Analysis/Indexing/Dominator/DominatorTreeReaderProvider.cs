using DumpDetective.Platform.Storage.Container;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// <see cref="IDominatorTreeProvider"/> backed by the three persisted dominator-tree readers (§10.4,
/// Batch 3, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — one facade so
/// callers (<c>DominatorAnalyzer</c>, §10.7) have one thing to null-check instead of three.
/// </summary>
internal sealed class DominatorTreeReaderProvider : IDominatorTreeProvider, IDisposable
{
    private readonly DominatorTreeIndexReader _scalarReader;
    private readonly DominatorChildIndexReader _childIndexReader;
    private readonly DominatorTreeMetadata _metadata;
    private readonly CacheContainerReader _container;
    private readonly object _typePairsLock = new();
    private IReadOnlyList<DominatorTypePair>? _typePairs;
    private readonly Dictionary<ulong, ulong> _retainedBytesByMethodTable;
    private bool _disposed;

    private DominatorTreeReaderProvider(
        DominatorTreeIndexReader scalarReader, DominatorChildIndexReader childIndexReader, DominatorTreeMetadata metadata,
        CacheContainerReader container)
    {
        _container = container;
        _scalarReader = scalarReader;
        _childIndexReader = childIndexReader;
        _metadata = metadata;

        // Loaded once here rather than re-parsed per query — ByMethodTable's JSON list is small
        // (bounded by distinct-type count, not object count).
        _retainedBytesByMethodTable = new Dictionary<ulong, ulong>(metadata.ByMethodTable.Count);
        foreach (DominatorTypeRetainedBytes entry in metadata.ByMethodTable)
            _retainedBytesByMethodTable[entry.MethodTable] = entry.RetainedBytes;
    }

    /// <summary>
    /// Attempts to open all three dominator-tree sections. Returns <c>false</c> — same as any
    /// missing/corrupt satellite section — if any of the three writers' sections are entirely
    /// absent (e.g. a legacy pre-Stage-B cache.bin, or Stage B skipped/failed for this run). The
    /// finer-grained "idom exists but retained bytes doesn't" case is handled inside
    /// <see cref="DominatorTreeIndexReader"/> itself, not here.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out DominatorTreeReaderProvider? provider)
    {
        provider = null;

        if (!DominatorTreeIndexReader.TryOpen(container, out DominatorTreeIndexReader? scalarReader) || scalarReader is null)
            return false;

        if (!DominatorChildIndexReader.TryOpen(container, out DominatorChildIndexReader? childIndexReader) || childIndexReader is null)
        {
            scalarReader.Dispose();
            return false;
        }

        if (!DominatorTreeMetadataReader.TryOpen(container, out DominatorTreeMetadata? metadata) || metadata is null)
        {
            scalarReader.Dispose();
            childIndexReader.Dispose();
            return false;
        }

        provider = new DominatorTreeReaderProvider(scalarReader, childIndexReader, metadata, container);
        return true;
    }

    public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) =>
        _scalarReader.TryGetImmediateDominator(address, out dominatorAddress);

    public bool TryGetRetainedBytes(ulong address, out ulong retainedBytes) =>
        _scalarReader.TryGetRetainedBytes(address, out retainedBytes);

    public IEnumerable<ulong> EnumerateRetainedSet(ulong address)
    {
        if (!_childIndexReader.TryGetChildren(address, out ulong[] rootChildren))
            yield break;

        yield return address;

        // Iterative, not recursive — safe at real-dump scale, same style DominatorTreeComputer's
        // own preorder traversal already uses.
        var stack = new Stack<ulong>();
        for (int i = rootChildren.Length - 1; i >= 0; i--)
            stack.Push(rootChildren[i]);

        while (stack.Count > 0)
        {
            ulong current = stack.Pop();
            yield return current;

            if (_childIndexReader.TryGetChildren(current, out ulong[] children))
            {
                for (int i = children.Length - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }
        }
    }

    public ulong TotalRetainedBytes => _metadata.TotalRetainedBytes;

    public IReadOnlyList<DominatorTypePair> GetImmediateDominatorTypePairs()
    {
        lock (_typePairsLock)
            return _typePairs ??= ComputeTypePairs();
    }

    // One streaming pass over the object index. The dominator's own method table is a point lookup (an object can be dominated by any
    // address), the object's comes with the stream, so nothing proportional to the object count is held: only the distinct pairs.
    private IReadOnlyList<DominatorTypePair> ComputeTypePairs()
    {
        if (!ObjectAddressLookup.TryOpen(_container, out ObjectAddressLookup? lookup) || lookup is null)
            return [];

        using (lookup)
        {
            var countsByPair = new Dictionary<(ulong Dominator, ulong Object), long>();
            foreach (HeapEntry entry in ObjectIndexReader.ReadDiskEntries(_container))
            {
                if (!_scalarReader.TryGetImmediateDominator(entry.Address, out ulong dominatorAddress) || dominatorAddress == 0)
                    continue;

                if (!lookup.TryGetEntry(dominatorAddress, out ulong dominatorMethodTable, out _))
                    continue;

                ref long count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
                    countsByPair, (dominatorMethodTable, entry.MethodTable), out _);
                count++;
            }

            var pairs = new List<DominatorTypePair>(countsByPair.Count);
            foreach (KeyValuePair<(ulong Dominator, ulong Object), long> pair in countsByPair)
                pairs.Add(new DominatorTypePair(pair.Key.Dominator, pair.Key.Object, pair.Value));

            pairs.Sort(static (a, b) => a.DominatorMethodTable != b.DominatorMethodTable
                ? a.DominatorMethodTable.CompareTo(b.DominatorMethodTable)
                : a.MethodTable.CompareTo(b.MethodTable));
            return pairs;
        }
    }

    public bool TryGetRetainedBytesByMethodTable(ulong methodTable, out ulong retainedBytes) =>
        _retainedBytesByMethodTable.TryGetValue(methodTable, out retainedBytes);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _scalarReader.Dispose();
        _childIndexReader.Dispose();
    }
}
