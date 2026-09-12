using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Builds an <c>Sdk.Analysis.AnalysisContext</c> from the legacy <c>Core.Abstractions.AnalysisContext</c>
/// a <see cref="LegacyAnalyzerAdapter{TSdkAnalyzer}"/> receives from the existing pipeline. Only
/// populates the capabilities a migrated analyzer actually needs (<c>HeapTypeStatistics</c>,
/// <c>HeapSegments</c>, <c>HeapRoots</c>, <c>HeapObjectLookup</c>, <c>HeapReferences</c>,
/// <c>HeapDominators</c>, <c>HeapRootPaths</c>, <c>HeapHandles</c>, <c>HeapSyncBlocks</c>,
/// <c>HeapFinalizerQueue</c>, <c>RuntimeThreads</c>, <c>RuntimeJit</c>, <c>DumpObjectFields</c> —
/// see docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// Every other capability-scoped property on the built context is left null, which is correct here
/// (not merely incomplete): no migrated analyzer needs them yet, and the SDK context's own contract
/// already treats null as "not available for this session."
/// </summary>
internal static class LegacyAnalysisContextTranslator
{
    /// <param name="runtimeThreadsOverride">
    /// Substitutes the normal live <see cref="RuntimeThreadQuery"/> — used by
    /// <see cref="Analyzers.LockGraphAnalyzerLegacyAdapter"/> and the rest of the thread-domain
    /// quartet to hand their inner analyzer a precomputed query backed by data the pipeline's
    /// shared <c>ThreadStackScanDispatcher</c> pass already accumulated, instead of a query that
    /// would re-walk every thread's stack independently. Null (the default, every other adapter)
    /// means "build the normal live one."
    /// </param>
    /// <param name="heapObjectsOverride">
    /// Same shape as <paramref name="runtimeThreadsOverride"/>, for <c>heap.objects</c> — used by
    /// <see cref="TypedResourceCandidateStreamingLegacyAdapter{TSelf, TSdkAnalyzer}"/> to hand its
    /// inner analyzer the candidate objects the pipeline's shared heap-index scan already
    /// discovered, instead of the inner analyzer re-deriving them. Null (the default) means "no
    /// live default exists yet" — unlike <c>RuntimeThreads</c>, no dump-side always-on
    /// <c>IHeapObjectStream</c> implementation has been built, since no non-scan-participant
    /// consumer has needed one so far.
    /// </param>
    public static Sdk.Analysis.AnalysisContext Translate(
        Core.Abstractions.AnalysisContext legacy, object? analyzerOptions,
        IRuntimeThreadQuery? runtimeThreadsOverride = null, IHeapObjectStream? heapObjectsOverride = null)
    {
        // Resolved once, upfront — GCRootAnalyzer's own pre-retyping gate was "is Stage B's
        // provider non-null at all", not a per-address check, so HeapDominators is either fully
        // backed or left null; there's no partial/lazy state to preserve here.
        IDominatorTreeProvider? treeProvider = legacy.Cache.TryGetDominatorTreeProvider();

        return new()
        {
            Observations = NoOpObservationSink.Instance,
            Progress = WrapProgress(legacy.Progress),
            AnalyzerOptions = analyzerOptions,
            HeapObjects = heapObjectsOverride,
            HeapTypeStatistics = new HeapTypeStatisticsQuery(legacy.Heap, legacy.Cache),
            HeapSegments = new HeapSegmentQuery(legacy.Heap, legacy.Runtime, legacy.Cache),
            HeapRoots = new HeapRootQuery(legacy.Heap, legacy.Cache),
            HeapObjectLookup = new HeapObjectLookup(legacy.Heap, legacy.Cache),
            HeapReferences = new HeapReferenceQuery(legacy.Heap),
            HeapHandles = new HeapHandleQuery(legacy.Runtime, legacy.Heap, legacy.Cache),
            HeapSyncBlocks = new HeapSyncBlockQuery(legacy.Heap),
            HeapFinalizerQueue = new HeapFinalizerQueueQuery(legacy.Heap),
            HeapDominators = treeProvider is not null
                ? new HeapDominatorQuery(treeProvider, legacy.Cache.TryGetThreadRetentionProvider())
                : null,
            HeapRootPaths = new HeapRootPathQuery(legacy.Heap, legacy.Cache),
            RuntimeThreads = runtimeThreadsOverride ?? new RuntimeThreadQuery(legacy.Runtime, legacy.Cache),
            RuntimeJit = new RuntimeJitQuery(legacy.Runtime),
            DumpObjectFields = new DumpObjectFieldQuery(legacy.Heap),
            DumpArrayShapes = new DumpArrayShapeQuery(legacy.Heap),
        };
    }

    private static IProgress<Sdk.Analysis.AnalyzerProgressReport>? WrapProgress(IProgress<Core.Abstractions.AnalyzerProgressReport>? progress) =>
        progress is null
            ? null
            : new Progress<Sdk.Analysis.AnalyzerProgressReport>(p =>
                progress.Report(new Core.Abstractions.AnalyzerProgressReport(p.ScannedCount, p.Phase, p.Detail, p.Elapsed)));
}
