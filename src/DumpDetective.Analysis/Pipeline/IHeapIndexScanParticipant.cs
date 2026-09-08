namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// Opt-in interface for analyzers that consume the shared on-disk heap-index scan
/// (<see cref="DumpDetective.Analysis.Cache.HeapAnalysisCache.EnumerateIndexedEntries"/>) instead of
/// enumerating the index themselves. <see cref="HeapIndexScanDispatcher"/> runs one shared pass over
/// the index per analysis run and fans each <see cref="DumpDetective.Analysis.Indexing.HeapEntry"/>
/// out to every registered participant, so N participating analyzers cost one scan instead of N scans.
/// </summary>
internal interface IHeapIndexScanParticipant
{
    /// <summary>
    /// Runs once per analyzer before the shared scan starts. Seed candidate-filter state here
    /// (e.g. from <c>TypeAggregates</c>) — this is the only place accumulator state should be reset,
    /// so <see cref="OnHeapEntry"/> stays branch-free on the "is this the first call" question across
    /// millions of invocations.
    /// </summary>
    void BeforeHeapIndexScan(DumpDetective.Core.Abstractions.AnalysisContext context);

    /// <summary>
    /// Called once per index record, in address order, during the single shared pass.
    /// Implementations accumulate into their own private instance fields.
    /// </summary>
    void OnHeapEntry(in DumpDetective.Analysis.Indexing.HeapEntry entry);

    /// <summary>
    /// Called exactly once per participant after <see cref="HeapIndexScanDispatcher.Run"/>
    /// finishes with it — either because the shared pass completed normally, or because
    /// <see cref="BeforeHeapIndexScan"/>/<see cref="OnHeapEntry"/> threw and the dispatcher
    /// isolated the failure so other participants keep running. <paramref name="succeeded"/>
    /// is the single source of truth for whether accumulated participant state is trustworthy;
    /// implementations that consume it in <c>AnalyzeAsync</c> must gate on this value instead of
    /// re-deriving "was the shared scan primed" from <c>cache.TryGetHeapIndex</c> a second time.
    /// Default no-op for participants with no separate fallback to gate on it.
    /// </summary>
    void OnHeapIndexScanCompleted(bool succeeded) { }
}
