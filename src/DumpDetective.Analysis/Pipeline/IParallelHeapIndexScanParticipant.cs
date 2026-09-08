namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// Opt-in extension of <see cref="IHeapIndexScanParticipant"/> for participants that can be
/// scanned as K workers over disjoint record ranges of the on-disk heap index instead of one
/// worker over the full range. <see cref="HeapIndexScanDispatcher"/> only partitions
/// participants that explicitly implement this interface — everyone else keeps running
/// through the existing single full-range pass, unchanged.
/// </summary>
internal interface IParallelHeapIndexScanParticipant : IHeapIndexScanParticipant
{
    /// <summary>
    /// Creates a fresh instance to own one worker's private accumulator state. The returned
    /// instance must not share mutable state with the instance it was created from —
    /// <see cref="IHeapIndexScanParticipant.BeforeHeapIndexScan"/> is called on it before it
    /// scans its own range.
    /// </summary>
    IHeapIndexScanParticipant CreateWorkerInstance();

    /// <summary>
    /// Called once, on the instance that will go on to serve <c>AnalyzeAsync</c>, after every
    /// worker (including this instance, which owns one range itself) has finished scanning its
    /// range. Merges partial accumulator state from <paramref name="partials"/> into this
    /// instance.
    /// </summary>
    void MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials);
}
