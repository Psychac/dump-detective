using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.dominators</c> capability, built for
/// <c>GCRootAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// A thin pass-through over an already-resolved <see cref="IDominatorTreeProvider"/>/
/// <see cref="IThreadRetentionProvider"/> pair — the caller (<see cref="Pipeline.AnalysisContextBuilder"/>)
/// is the one that decides whether the capability exists at all for this session, by only
/// constructing this type when <c>IHeapAnalysisCache.TryGetDominatorTreeProvider</c> returned
/// non-null; <c>AnalysisContext.HeapDominators</c> stays null otherwise. Matches the old
/// <c>GCRootAnalyzer</c>'s own single upfront "is Stage B available at all" gate rather than
/// re-deriving it per call.
/// </summary>
internal sealed class HeapDominatorQuery(
    IDominatorTreeProvider treeProvider, IThreadRetentionProvider? threadRetentionProvider, IHeapObjectLookup objectLookup,
    Func<ulong, string?>? resolveTypeName = null) : IHeapDominatorQuery
{
    public IReadOnlyList<DominatedTypePair> GetImmediateDominatorTypePairs()
    {
        if (resolveTypeName is null)
            return [];

        var typeByMethodTable = new Dictionary<ulong, TypeRef>();
        TypeRef TypeOf(ulong methodTable)
        {
            if (!typeByMethodTable.TryGetValue(methodTable, out TypeRef? type))
                typeByMethodTable[methodTable] = type = SdkTypeRefFactory.FromName(resolveTypeName(methodTable) ?? $"MT:0x{methodTable:x}", methodTable);
            return type;
        }

        // Distinct method tables can canonicalize to one name (generic instantiations, state machines), so their counts are added.
        var merged = new Dictionary<(string Dominator, string Dominated), (TypeRef Dominator, TypeRef Dominated, long Count)>();
        foreach (DominatorTypePair pair in treeProvider.GetImmediateDominatorTypePairs())
        {
            TypeRef dominator = TypeOf(pair.DominatorMethodTable);
            TypeRef dominated = TypeOf(pair.MethodTable);
            var key = (dominator.CanonicalName, dominated.CanonicalName);
            merged[key] = merged.TryGetValue(key, out var existing)
                ? (existing.Dominator, existing.Dominated, existing.Count + pair.ObjectCount)
                : (dominator, dominated, pair.ObjectCount);
        }

        var pairs = new List<DominatedTypePair>(merged.Count);
        foreach (var entry in merged.Values)
            pairs.Add(new DominatedTypePair(entry.Dominator, entry.Dominated, entry.Count));

        pairs.Sort(static (a, b) => string.CompareOrdinal(a.Dominator.CanonicalName, b.Dominator.CanonicalName) is var c and not 0
            ? c : string.CompareOrdinal(a.Dominated.CanonicalName, b.Dominated.CanonicalName));
        return pairs;
    }

    public bool TryGetRetainedSize(ulong address, out ulong retainedBytes) =>
        treeProvider.TryGetRetainedBytes(address, out retainedBytes);

    public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) =>
        treeProvider.TryGetImmediateDominator(address, out dominatorAddress);

    public bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes)
    {
        if (threadRetentionProvider is null)
        {
            retainedBytes = 0;
            return false;
        }

        return threadRetentionProvider.TryGetRetainedBytesForThread(osThreadId, out retainedBytes);
    }

    public IEnumerable<HeapObjectRef> EnumerateRetainedObjects(ulong rootAddress)
    {
        foreach (ulong address in treeProvider.EnumerateRetainedSet(rootAddress))
            if (objectLookup.TryGetObject(address, out HeapObjectRef obj))
                yield return obj;
    }

    public bool TryGetRetainedBytesByType(TypeRef type, out ulong retainedBytes)
    {
        if (type.MethodTable is not ulong methodTable)
        {
            retainedBytes = 0;
            return false;
        }

        return treeProvider.TryGetRetainedBytesByMethodTable(methodTable, out retainedBytes);
    }
}
