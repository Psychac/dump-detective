namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Streaming heap-object enumeration — the <c>heap.objects</c> capability's coarse-scan surface.
/// Never buffer this into a list; the whole point of streaming here matches the project's
/// no-full-materialization rule for the underlying heap scan itself.
/// </summary>
/// <remarks>
/// <b>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> parameter — considered
/// and confirmed correct 2026-09-10</b>
/// (docs/refactor/modularity/phase-1-sdk-review-findings.md item 13), not a placeholder pending an
/// async rewrite. Every Tier-1 streaming surface in this namespace shares this shape and this
/// rationale; this is the one place it's written out in full rather than repeated per interface.
///
/// The "trace sources are event-callback-driven, so this should be async" instinct is a category
/// error: by the time analysis runs and calls this method, the underlying data has already been
/// ingested into a disk-backed, sequential index during an earlier, separate phase — the dump's
/// single-pass heap scan, or a trace's ingest (e.g. <c>GcPauseIndexer</c>, which already turns live
/// <c>TraceEventDispatcher</c> callbacks into a disk section before any analyzer runs). This
/// interface is consumed strictly after that, and the real trace-side reader that already exists
/// (<c>TraceMethodIndexReader.ReadAll(stream)</c>, used by <c>CpuHotspotAnalyzer</c> today) is
/// itself a plain synchronous sequential stream read — not a live callback subscription. Async
/// ingest is real; async analysis-time query was never actually needed.
///
/// Matches both real precedents exactly: ClrMD's own <c>ClrHeap.EnumerateObjects()</c> is sync
/// <see cref="IEnumerable{T}"/> with no cancellation parameter, and every existing dump analyzer is
/// built on that shape. Cancellation flows through the outer
/// <c>IAnalyzer.AnalyzeAsync(AnalysisContext, CancellationToken)</c> call and is checked
/// periodically inside the loop body that consumes this — the same pattern used everywhere today,
/// on both the dump and trace sides — not threaded into the enumerable itself.
/// </remarks>
public interface IHeapObjectStream
{
    IEnumerable<HeapObjectRef> EnumerateObjects();
}
