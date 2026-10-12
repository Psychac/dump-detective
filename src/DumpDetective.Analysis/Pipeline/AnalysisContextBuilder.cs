using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;

using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// Builds the <c>Sdk.Analysis.AnalysisContext</c> each analyzer's <c>AnalyzeAsync</c> runs against,
/// from the pipeline's <see cref="RuntimeAnalysisContext"/>. Only populates the capabilities a
/// retyped analyzer actually needs (<c>HeapTypeStatistics</c>, <c>HeapSegments</c>, <c>HeapRoots</c>,
/// <c>HeapObjectLookup</c>, <c>HeapReferences</c>, <c>HeapReverseReferences</c>, <c>HeapDominators</c>,
/// <c>HeapReachability</c>, <c>HeapRootPaths</c>, <c>HeapHandles</c>, <c>HeapSyncBlocks</c>,
/// <c>HeapFinalizerQueue</c>, <c>RuntimeThreads</c>, <c>RuntimeJit</c>, <c>RuntimeThreadPool</c>,
/// <c>DumpObjectFields</c>). Every other capability-scoped property on the built context is left
/// null, which is correct here (not merely incomplete): no analyzer needs them yet, and the SDK
/// context's own contract already treats null as "not available for this session."
/// </summary>
internal static class AnalysisContextBuilder
{
    /// <param name="runtimeThreadsOverride">
    /// Substitutes the normal live <see cref="RuntimeThreadQuery"/> — used by
    /// <see cref="Analyzers.Threads.LockGraphAnalyzer"/> and the rest of the thread-domain quartet to
    /// hand themselves a precomputed query backed by data the pipeline's shared
    /// <see cref="ThreadStackScanDispatcher"/> pass already accumulated, instead of a query that
    /// would re-walk every thread's stack independently. Null (the default, every other analyzer)
    /// means "build the normal live one."
    /// </param>
    /// <param name="heapObjectsOverride">
    /// Same shape as <paramref name="runtimeThreadsOverride"/>, for <c>heap.objects</c> — used by
    /// <see cref="TypedResourceCandidateStreamingLegacyAdapter{TSelf, TSdkAnalyzer}"/> to hand its
    /// inner analyzer the candidate objects the pipeline's shared heap-index scan already
    /// discovered, instead of the inner analyzer re-deriving them. Null (the default) means "build
    /// the normal live <see cref="HeapObjectStream"/>".
    /// </param>
    public static Sdk.Analysis.AnalysisContext Build(
        RuntimeAnalysisContext context, object? analyzerOptions,
        IRuntimeThreadQuery? runtimeThreadsOverride = null, IHeapObjectStream? heapObjectsOverride = null)
    {
        // Resolved once, upfront — GCRootAnalyzer's own pre-retyping gate was "is Stage B's
        // provider non-null at all", not a per-address check, so HeapDominators is either fully
        // backed or left null; there's no partial/lazy state to preserve here.
        IDominatorTreeProvider? treeProvider = context.Cache.TryGetDominatorTreeProvider();

        // Same shape as HeapDominators above -- resolved once, upfront; either fully backed or
        // left null, no partial/lazy state to preserve.
        IReachableAddressProvider? reachableProvider = context.Cache.TryGetReachableAddressProvider();

        var objectLookup = new HeapObjectLookup(context.Heap, context.Cache);

        return new()
        {
            Observations = context.ObservationSink,
            ArtifactId = context.ArtifactId,
            Progress = WrapProgress(context.Progress),
            AnalyzerOptions = analyzerOptions,
            HeapObjects = heapObjectsOverride ?? new HeapObjectStream(context.Heap, context.Cache),
            HeapTypeStatistics = new HeapTypeStatisticsQuery(context.Heap, context.Cache),
            HeapSegments = new HeapSegmentQuery(context.Heap, context.Runtime, context.Cache),
            HeapRoots = new HeapRootQuery(context.Heap, context.Cache),
            HeapObjectLookup = objectLookup,
            HeapReferences = new HeapReferenceQuery(context.Heap),
            HeapReverseReferences = new HeapReverseReferenceQuery(context.Cache),
            HeapHandles = new HeapHandleQuery(context.Runtime, context.Heap, context.Cache),
            HeapSyncBlocks = new HeapSyncBlockQuery(context.Heap),
            HeapFinalizerQueue = new HeapFinalizerQueueQuery(context.Heap),
            HeapReachability = reachableProvider is not null
                ? new HeapReachabilityQuery(reachableProvider)
                : null,
            HeapDominators = treeProvider is not null
                ? new HeapDominatorQuery(treeProvider, context.Cache.TryGetThreadRetentionProvider(), objectLookup, methodTable => context.Heap.GetTypeByMethodTable(methodTable)?.Name)
                : null,
            HeapRootPaths = new HeapRootPathQuery(context.Heap, context.Cache),
            RuntimeThreads = runtimeThreadsOverride ?? new RuntimeThreadQuery(context.Runtime, context.Cache),
            RuntimeJit = new RuntimeJitQuery(context.Runtime),
            RuntimeThreadPool = new RuntimeThreadPoolQuery(context.Runtime),
            RuntimeModules = new RuntimeModuleQuery(context.Runtime),
            DumpObjectFields = new DumpObjectFieldQuery(context.Heap),
            DumpArrayShapes = new DumpArrayShapeQuery(context.Heap),
        };
    }

    private static IProgress<Sdk.Analysis.AnalyzerProgressReport>? WrapProgress(IProgress<Core.Abstractions.AnalyzerProgressReport>? progress) =>
        progress is null
            ? null
            : new Progress<Sdk.Analysis.AnalyzerProgressReport>(p =>
                progress.Report(new Core.Abstractions.AnalyzerProgressReport(p.ScannedCount, p.Phase, p.Detail, p.Elapsed)));
}
