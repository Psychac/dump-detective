using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;

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
internal sealed class HeapDominatorQuery(IDominatorTreeProvider treeProvider, IThreadRetentionProvider? threadRetentionProvider) : IHeapDominatorQuery
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
}
