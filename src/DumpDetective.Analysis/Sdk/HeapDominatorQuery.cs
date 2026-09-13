using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.dominators</c> capability, built for
/// <c>GCRootAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// A thin pass-through over an already-resolved <see cref="IDominatorTreeProvider"/>/
/// <see cref="IThreadRetentionProvider"/> pair — the caller (<see cref="LegacyAnalysisContextTranslator"/>)
/// is the one that decides whether the capability exists at all for this session, by only
/// constructing this type when <c>IHeapAnalysisCache.TryGetDominatorTreeProvider</c> returned
/// non-null; <c>AnalysisContext.HeapDominators</c> stays null otherwise. Matches the old
/// <c>GCRootAnalyzer</c>'s own single upfront "is Stage B available at all" gate rather than
/// re-deriving it per call.
/// </summary>
internal sealed class HeapDominatorQuery(IDominatorTreeProvider treeProvider, IThreadRetentionProvider? threadRetentionProvider, IHeapObjectLookup objectLookup) : IHeapDominatorQuery
{
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
