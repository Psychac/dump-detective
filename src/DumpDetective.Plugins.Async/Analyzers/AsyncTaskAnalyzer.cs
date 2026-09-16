using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers.Async;

/// <summary>
/// Phase-2 analyzer covering task/continuation-graph health, orphaned tasks,
/// <c>TaskCompletionSource</c>, and <c>IValueTaskSource</c> population health.
/// </summary>
/// <remarks>
/// Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Three
/// object shapes (Task, TaskCompletionSource, IValueTaskSource) are discovered off one unified
/// <c>heap.objects</c> candidate stream <see cref="AsyncTaskAnalyzerLegacyAdapter"/> assembles, then
/// classified here via <see cref="HeapTypeStatistics.IsTaskType"/>/<c>IsTaskCompletionSourceType</c>/
/// <c>ImplementsValueTaskSource</c> — the same "classify from the shared per-type stats map, not a
/// separate scan-participant MT set" pattern <c>AsyncStateMachineAnalyzer</c> established for
/// <c>IsAsyncStateMachineType</c>. Dropping the pre-retyping analyzer's own separate
/// <c>_taskMts</c>/<c>_tcsMts</c>/<c>_vtsMts</c> tracking and three-way <c>MergePartial</c> (moved to
/// the adapter, which still needs cache/ClrMD access the SDK analyzer itself must not have) is a real
/// simplification, not just a mechanical port.
///
/// <b>Two genuinely new Tier-2 primitives, closed here</b>: <see cref="IDumpArrayShapeQuery.EnumerateObjectReferenceElements"/>
/// (array element *address* enumeration — nothing before this let a retyped analyzer walk a
/// <c>List&lt;object&gt;</c>'s backing array by index) and <see cref="IDumpObjectFieldQuery.TryReadNestedStructField"/>
/// (one level of embedded-struct field read — needed for
/// <c>ManualResetValueTaskSourceCore&lt;TResult&gt;._completed</c>, which <c>TryReadField</c> alone
/// can't reach). <see cref="IHeapReferenceQuery.EnumerateReferences"/> plus a ported
/// <see cref="CapabilityObjectFieldTraversal"/> close the faulted-task exception-object search
/// without needing any new capability at all.
///
/// <b>Type-name resolution simplification</b>: the pre-retyping analyzer maintained its own
/// MT-to-name cache (<c>ResolveTypeName</c>) because raw <see cref="Microsoft.Diagnostics.Runtime.HeapEntry"/>
/// records only carry a <c>MethodTable</c>. Every candidate here arrives as a <see cref="HeapObjectRef"/>,
/// which already carries <see cref="HeapObjectRef.TypeDisplayName"/> resolved once by the adapter —
/// that cache has no reason to exist anymore.
///
/// <b>One accepted, documented fidelity gap</b>: pending/unresolved-population generation bucketing
/// used the pre-retyping analyzer's own 0/1/2/3(LOH) scheme, with no POH/Frozen bucket.
/// <see cref="IHeapSegmentQuery.GetGeneration"/>'s <see cref="HeapGenerationTag"/> distinguishes them
/// — both fold into the legacy LOH bucket (<see cref="ToLegacyGeneration"/>), matching
/// <c>GCRootAnalyzer</c>'s own prior treatment of the same gap.
///
/// <b>One accepted perf/fidelity trade, documented in <see cref="AsyncTaskAnalyzerLegacyAdapter"/></b>:
/// the pre-retyping analyzer's fastest Task-discovery path (a dedicated <c>TaskIndex.bin</c> satellite
/// index) carried each task's <c>m_stateFlags</c> for free, skipping a ClrMD re-read on that one path.
/// <see cref="HeapObjectRef"/> has no slot for that extra field, so <c>m_stateFlags</c> is always
/// re-read here via <see cref="IDumpObjectFieldQuery.TryReadField"/> regardless of which candidate
/// source won — the same per-task cost three of the pre-retyping analyzer's four load paths already
/// paid.
/// </remarks>
[AnalyzerModule(key: "async-task", displayName: "Async Task Analysis", order: 190, tags: ["async"])]
[RequiresCapability(
    CapabilityVocabulary.DumpObjectFields,
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapReferences,
    CapabilityVocabulary.HeapSegments,
    CapabilityVocabulary.HeapTypes)]
[OptionalCapability(CapabilityVocabulary.TraceThreadTimeline)]
public sealed partial class AsyncTaskAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    // m_stateFlags bit masks (matches HangAnalyzer)
    private const int MaskCompleted = 0x1000000;
    private const int MaskFaulted = 0x200000;
    private const int MaskCanceled = 0x400000;
    private const int MaskRunning = 0x10000; // TASK_STATE_DELEGATE_INVOKED

    // Sentinel continuation type -- no-op callback; indicates an orphan.
    private const string NoOpContinuationType = "System.Threading.Tasks.Task+<>c";
    // When >1 continuation is attached to a Task, the runtime replaces the scalar
    // m_continuationObject with a List<object> holding all attached continuations.
    private const string MultiContinuationListTypePrefix = "System.Collections.Generic.List<";
    // Sanity cap on fan-out enumeration -- protects against corrupted/adversarial size fields.
    private const int MaxFanOutElementsToEnumerate = 10_000;
    // Global per-task cap on total nodes visited during continuation-tree exploration. Bounds
    // worst-case cost for pathological/adversarial fan-out trees (wide-and-deep multi-continuation
    // graphs), independent of MaxContinuationDepth, which only bounds depth of a single branch, not
    // the total size of the reachable subgraph.
    private const int MaxContinuationNodesToVisitPerTask = 2_000;
    private const int MaxExceptionSearchDepth = 4;

    private static readonly string[] ExceptionRelatedFields =
    [
        "m_exceptionsHolder",
        "_exceptionsHolder",
        "m_contingentProperties",
        "_contingentProperties",
    ];

    public string Name => "Async Task Analysis";
    public string Category => "Memory";

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapTypes}' capability.");
        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.DumpObjectFields}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
        IHeapReferenceQuery references = context.HeapReferences
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapReferences}' capability.");
        IHeapSegmentQuery segments = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
        IDumpArrayShapeQuery arrayShapes = context.DumpArrayShapes
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.DumpArrayElements}' capability.");

        LastResult = Analyze(typeStatistics, fields, objectLookup, references, segments, arrayShapes, context.HeapObjects, context.Progress, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static AsyncTaskDomainResult Analyze(
        IHeapTypeStatisticsQuery typeStatistics,
        IDumpObjectFieldQuery fields,
        IHeapObjectLookup objectLookup,
        IHeapReferenceQuery references,
        IHeapSegmentQuery segments,
        IDumpArrayShapeQuery arrayShapes,
        IHeapObjectStream? candidates,
        IProgress<AnalyzerProgressReport>? progress,
        CancellationToken ct)
    {
        var taskEntries = new List<HeapObjectRef>();
        var tcsEntries = new List<HeapObjectRef>();
        var vtsEntries = new List<(HeapObjectRef Object, string CoreFieldName)>();

        if (candidates is not null)
        {
            IReadOnlyDictionary<string, HeapTypeStatistics> stats = typeStatistics.GetTypeStatistics();
            foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
            {
                ct.ThrowIfCancellationRequested();
                if (!stats.TryGetValue(candidate.TypeDisplayName, out HeapTypeStatistics entry))
                    continue;

                if (entry.IsTaskType)
                    taskEntries.Add(candidate);
                else if (entry.IsTaskCompletionSourceType)
                    tcsEntries.Add(candidate);
                else if (entry.ImplementsValueTaskSource && entry.ValueTaskSourceCoreFieldName is not null)
                    vtsEntries.Add((candidate, entry.ValueTaskSourceCoreFieldName));
            }
        }

        int total = taskEntries.Count;

        // TaskCompletionSource and IValueTaskSource results are independent of the task-scan
        // results (a heap could in principle have TCS/VTS instances even if the task scan finds
        // none), so both run regardless of the early-return below.
        TcsAnalysisResult tcsResult = AnalyzeTaskCompletionSources(fields, segments, tcsEntries, ct);
        VtsAnalysisResult vtsResult = AnalyzeValueTaskSources(fields, segments, vtsEntries, ct);

        if (total == 0)
            return EmptyWith(tcsResult, vtsResult);

        // ── Classify task states ─────────────────────────────────────────────────
        progress?.Report(new AnalyzerProgressReport(0, "classifying task states", $"0 / {total:N0} tasks"));

        int pending = 0;
        int running = 0;
        int faulted = 0;
        int canceled = 0;
        int completed = 0;
        int orphaned = 0;

        var pendingTypeCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var pendingTypeBytes = new Dictionary<string, (long TotalBytes, int Count)>(StringComparer.Ordinal);
        var faultedTypeCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var faultedTypeExceptions = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var continuationCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var fanoutTypeCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var orphanedSnapshots = new List<OrphanedTaskSnapshot>(capacity: 32);
        var deepestChains = new List<ContinuationChainSnapshot>(capacity: 5);

        int totalDepthSum = 0;
        int maxDepth = 0;
        int depthSampleCount = 0;
        int totalContinuations = 0;
        int multiContinuationNodes = 0;
        int maxFanOut = 0;
        bool cycleDetected = false;
        int pendingGen0 = 0;
        int pendingGen1 = 0;
        int pendingGen2 = 0;
        int pendingLOH = 0;

        int classifyScanCount = 0;
        for (int i = 0; i < taskEntries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            HeapObjectRef task = taskEntries[i];
            ulong address = task.Address;
            string typeName = task.TypeDisplayName;

            classifyScanCount++;
            if (classifyScanCount % 5000 == 0)
                progress?.Report(new AnalyzerProgressReport(classifyScanCount, "classifying task states",
                    $"{classifyScanCount:N0} / {total:N0} tasks"));

            int stateFlags = TryReadStateFlags(fields, address) ?? 0;

            bool isCompleted = (stateFlags & MaskCompleted) != 0;
            bool isFaulted = (stateFlags & MaskFaulted) != 0;
            bool isCanceled = (stateFlags & MaskCanceled) != 0;
            bool isRunning = (stateFlags & MaskRunning) != 0 && !isCompleted && !isFaulted && !isCanceled;

            if (isFaulted) faulted++;
            else if (isCanceled) canceled++;
            else if (isCompleted) completed++;
            else if (isRunning) running++;
            else pending++;

            // Track GC generation for pending tasks
            if (!isCompleted && !isCanceled && !isFaulted && !isRunning)
            {
                int gen = ToLegacyGeneration(segments.GetGeneration(address));
                if (gen == 0) pendingGen0++;
                else if (gen == 1) pendingGen1++;
                else if (gen == 2) pendingGen2++;
                if (gen == 3) pendingLOH++;
            }

            // Track top type counts
            if (!isCompleted && !isCanceled)
            {
                if (isFaulted)
                {
                    IncrementCount(faultedTypeCount, typeName);
                    (string? exceptionType, _) = ExtractFaultedTaskException(fields, objectLookup, references, address);
                    if (exceptionType != null)
                    {
                        if (!faultedTypeExceptions.TryGetValue(typeName, out Dictionary<string, int>? exceptionHistogram))
                        {
                            exceptionHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
                            faultedTypeExceptions[typeName] = exceptionHistogram;
                        }
                        IncrementCount(exceptionHistogram, exceptionType);
                    }
                }
                else
                {
                    IncrementCount(pendingTypeCount, typeName);
                    pendingTypeBytes.TryGetValue(typeName, out (long TotalBytes, int Count) byteEntry);
                    pendingTypeBytes[typeName] = (byteEntry.TotalBytes + (long)task.Size, byteEntry.Count + 1);
                }
            }

            // ── Orphan detection + continuation chain BFS ─────────────────────
            bool hasContinuationField = fields.TryReadField(address, "m_continuationObject", out FieldValue continuationValue)
                || fields.TryReadField(address, "_continuationObject", out continuationValue);

            if (hasContinuationField)
            {
                ulong continuationAddress = continuationValue.Kind == FieldValueKind.ObjectReference
                    ? continuationValue.AsObjectAddress()
                    : 0;

                string? continuationTypeName = continuationAddress != 0 && objectLookup.TryGetObject(continuationAddress, out HeapObjectRef continuationObj)
                    ? continuationObj.TypeDisplayName
                    : null;

                bool isOrphan = continuationAddress == 0
                    || string.Equals(continuationTypeName, NoOpContinuationType, StringComparison.Ordinal);

                var chainTypes = new List<string>(capacity: 8) { typeName };

                if (isOrphan && !isCompleted && !isCanceled)
                {
                    orphaned++;
                    string? resultType = ExtractResultType(typeName);
                    (string? exceptionType, string? exceptionMessage) = ExtractFaultedTaskException(fields, objectLookup, references, address);
                    orphanedSnapshots.Add(new OrphanedTaskSnapshot(address, typeName, resultType, task.Size, exceptionType, exceptionMessage));
                }

                // BFS chain depth -- true branching traversal. If m_continuationObject holds a
                // List<object> (2+ continuations attached), every branch is explored to find the
                // true maximum depth and fully populate fan-out stats (not just whichever branch
                // happens to be walked first). See ExploreContinuation's own remarks for the
                // node-budget and diamond-convergence caveats.
                if (continuationAddress != 0)
                {
                    var visited = new HashSet<ulong>(capacity: 16) { address };
                    var bestHopSelections = new Dictionary<ulong, ulong>(capacity: 32);
                    int nodeBudget = MaxContinuationNodesToVisitPerTask;

                    int depth = ExploreContinuation(
                        continuationAddress, visited, address, ref nodeBudget, depthLevel: 0,
                        fields, objectLookup, arrayShapes,
                        continuationCount, fanoutTypeCount, ref totalContinuations, ref multiContinuationNodes, ref maxFanOut,
                        ref cycleDetected, bestHopSelections);

                    // Reconstruct the winning branch's type-name sequence for display by replaying
                    // the per-node decisions recorded during exploration. Bounded by the same
                    // node-visit budget as a defensive guard -- the recorded selections form a DAG
                    // (visited-set enforced during construction) so this terminates naturally once
                    // no further hop was recorded.
                    ulong hop = continuationAddress;
                    int reconstructSteps = 0;
                    while (hop != 0 && reconstructSteps++ < MaxContinuationNodesToVisitPerTask)
                    {
                        if (objectLookup.TryGetObject(hop, out HeapObjectRef hopObj) && !IsMultiContinuationList(hopObj.TypeDisplayName))
                            chainTypes.Add(hopObj.TypeDisplayName);

                        if (!bestHopSelections.TryGetValue(hop, out ulong nextHop))
                            break;
                        hop = nextHop;
                    }

                    totalDepthSum += depth;
                    if (depth > maxDepth) maxDepth = depth;
                    depthSampleCount++;

                    if (deepestChains.Count < 5)
                    {
                        deepestChains.Add(new ContinuationChainSnapshot(address, typeName, depth, chainTypes));
                    }
                    else
                    {
                        int shallowestIndex = 0;
                        for (int chainIndex = 1; chainIndex < deepestChains.Count; chainIndex++)
                        {
                            if (deepestChains[chainIndex].Depth < deepestChains[shallowestIndex].Depth)
                                shallowestIndex = chainIndex;
                        }

                        if (depth > deepestChains[shallowestIndex].Depth)
                            deepestChains[shallowestIndex] = new ContinuationChainSnapshot(address, typeName, depth, chainTypes);
                    }
                }
            }
        }

        progress?.Report(new AnalyzerProgressReport(classifyScanCount, "aggregating results",
            $"{total:N0} tasks classified, {orphaned:N0} orphaned"));

        double avgDepth = depthSampleCount > 0 ? (double)totalDepthSum / depthSampleCount : 0.0;

        IReadOnlyList<FaultedTaskTypeProfile> faultedProfiles = BuildFaultedTaskTypeProfiles(faultedTypeCount, faultedTypeExceptions);

        return new AsyncTaskDomainResult(
            TotalTasks: total,
            PendingTasks: pending,
            RunningTasks: running,
            FaultedTasks: faulted,
            CanceledTasks: canceled,
            CompletedTasks: completed,
            OrphanedTasks: orphaned,
            TotalTaskContinuations: totalContinuations,
            MaxContinuationDepth: maxDepth,
            AvgContinuationDepth: avgDepth,
            TopPendingTaskTypes: BuildSorted(pendingTypeCount),
            TopFaultedTaskTypes: BuildSorted(faultedTypeCount),
            TopContinuationTypes: BuildSorted(continuationCount),
            TopOrphanedTasks: orphanedSnapshots,
            TopDeepestChains: deepestChains.OrderByDescending(chain => chain.Depth).ToArray(),
            FaultedTaskExceptionHistograms: faultedProfiles,
            MultiContinuationNodeCount: multiContinuationNodes,
            MaxContinuationFanOut: maxFanOut,
            TopContinuationFanoutTypes: BuildSorted(fanoutTypeCount),
            DepthSampleCount: depthSampleCount,
            CycleDetected: cycleDetected,
            PendingGen0: pendingGen0,
            PendingGen1: pendingGen1,
            PendingGen2: pendingGen2,
            PendingLOH: pendingLOH,
            TopPendingTaskTypesByBytes: BuildSortedByBytes(pendingTypeBytes),
            TotalTaskCompletionSources: tcsResult.TotalTcs,
            UnresolvedTaskCompletionSources: tcsResult.UnresolvedTcs,
            UnresolvedTcsGen2Count: tcsResult.UnresolvedTcsGen2Count,
            TopUnresolvedTaskCompletionSources: tcsResult.TopUnresolved,
            TotalValueTaskSources: vtsResult.TotalVts,
            PendingValueTaskSources: vtsResult.PendingVts,
            PendingVtsGen2Count: vtsResult.PendingVtsGen2Count,
            TopPendingValueTaskSources: vtsResult.TopPending);
    }

    // ── TaskCompletionSource analysis ────────────────────────────────────────────

    private readonly record struct TcsAnalysisResult(
        int TotalTcs,
        int UnresolvedTcs,
        int UnresolvedTcsGen2Count,
        IReadOnlyList<UnresolvedTcsSnapshot> TopUnresolved);

    /// <summary>
    /// Classifies each TaskCompletionSource candidate by reading its inner Task field (<c>m_task</c>/
    /// <c>_task</c>, the .NET Core 3+ field-rename fallback) and checking whether the inner task is
    /// terminal. A non-terminal inner task means nobody called SetResult/SetException/SetCanceled --
    /// either a normal in-flight promise, or (if the TCS has survived into Gen2/LOH) a leaked one.
    /// See <see cref="UnresolvedTcsSnapshot"/>.
    /// </summary>
    private static TcsAnalysisResult AnalyzeTaskCompletionSources(
        IDumpObjectFieldQuery fields,
        IHeapSegmentQuery segments,
        List<HeapObjectRef> tcsEntries,
        CancellationToken ct)
    {
        if (tcsEntries.Count == 0)
            return new TcsAnalysisResult(0, 0, 0, []);

        int unresolvedTcs = 0;
        int unresolvedTcsGen2 = 0;
        var unresolvedSnapshots = new List<UnresolvedTcsSnapshot>(capacity: 32);

        for (int i = 0; i < tcsEntries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            HeapObjectRef tcs = tcsEntries[i];

            if (!TryReadObjectReferenceField(fields, tcs.Address, "m_task", "_task", out ulong innerTaskAddress) || innerTaskAddress == 0)
                continue;

            int? stateFlags = TryReadStateFlags(fields, innerTaskAddress);
            if (stateFlags is null)
                continue;

            bool isTerminal = (stateFlags.Value & (MaskCompleted | MaskFaulted | MaskCanceled)) != 0;
            if (isTerminal)
                continue;

            unresolvedTcs++;

            int generation = ToLegacyGeneration(segments.GetGeneration(tcs.Address));
            if (generation is 2 or 3)
                unresolvedTcsGen2++;

            unresolvedSnapshots.Add(new UnresolvedTcsSnapshot(
                Address: tcs.Address,
                TypeName: tcs.TypeDisplayName,
                Size: tcs.Size,
                Generation: generation));
        }

        // Bounded population (TCS instance counts are a bounded resource) -- a full sort of the
        // unresolved subset is cheap, no need for a maintained-threshold partial-sort pattern.
        // Strongest leak signal first: old-generation residency, then size within that tier.
        // Complete population, no Top-N cap -- the render layer slices for display.
        unresolvedSnapshots.Sort((a, b) =>
        {
            int genCompare = b.Generation.CompareTo(a.Generation);
            return genCompare != 0 ? genCompare : b.Size.CompareTo(a.Size);
        });

        return new TcsAnalysisResult(tcsEntries.Count, unresolvedTcs, unresolvedTcsGen2, unresolvedSnapshots);
    }

    // ── IValueTaskSource analysis ─────────────────────────────────────────────────

    private readonly record struct VtsAnalysisResult(
        int TotalVts,
        int PendingVts,
        int PendingVtsGen2Count,
        IReadOnlyList<PendingValueTaskSourceSnapshot> TopPending);

    /// <summary>
    /// Classifies each IValueTaskSource candidate by reading its embedded
    /// ManualResetValueTaskSourceCore&lt;T&gt; field and checking its <c>_completed</c>/<c>m_completed</c>
    /// flag. A false value means the source hasn't signaled completion -- either a normal in-flight
    /// operation, or (if it has survived into Gen2/LOH) a stuck one. See
    /// <see cref="PendingValueTaskSourceSnapshot"/>.
    /// </summary>
    private static VtsAnalysisResult AnalyzeValueTaskSources(
        IDumpObjectFieldQuery fields,
        IHeapSegmentQuery segments,
        List<(HeapObjectRef Object, string CoreFieldName)> vtsEntries,
        CancellationToken ct)
    {
        if (vtsEntries.Count == 0)
            return new VtsAnalysisResult(0, 0, 0, []);

        int pendingVts = 0;
        int pendingVtsGen2 = 0;
        var pendingSnapshots = new List<PendingValueTaskSourceSnapshot>(capacity: 32);

        for (int i = 0; i < vtsEntries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            (HeapObjectRef vts, string coreFieldName) = vtsEntries[i];

            if (!fields.TryReadNestedStructField(vts.Address, coreFieldName, "_completed", out FieldValue completedValue)
                && !fields.TryReadNestedStructField(vts.Address, coreFieldName, "m_completed", out completedValue))
                continue;

            if (completedValue.Kind != FieldValueKind.Bool || completedValue.AsBool())
                continue;

            pendingVts++;

            int generation = ToLegacyGeneration(segments.GetGeneration(vts.Address));
            if (generation is 2 or 3)
                pendingVtsGen2++;

            pendingSnapshots.Add(new PendingValueTaskSourceSnapshot(
                Address: vts.Address,
                TypeName: vts.TypeDisplayName,
                Size: vts.Size,
                Generation: generation));
        }

        // Same bounded-population rationale as AnalyzeTaskCompletionSources: a full sort of the
        // pending subset is cheap here too. Strongest leak signal first: old-generation residency,
        // then size within that tier. Complete population, no Top-N cap.
        pendingSnapshots.Sort((a, b) =>
        {
            int genCompare = b.Generation.CompareTo(a.Generation);
            return genCompare != 0 ? genCompare : b.Size.CompareTo(a.Size);
        });

        return new VtsAnalysisResult(vtsEntries.Count, pendingVts, pendingVtsGen2, pendingSnapshots);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Maps <see cref="HeapGenerationTag"/> onto the pre-retyping analyzer's own
    /// 0/1/2/3(LOH) scheme. Accepted, documented gap: Poh/Frozen have no bucket of their own in that
    /// scheme and fold into 3 (LOH), matching <c>GCRootAnalyzer</c>'s own prior treatment of the same
    /// gap. Unknown (address not on any live segment) maps to -1, matching neither bucket.</summary>
    private static int ToLegacyGeneration(HeapGenerationTag tag) => tag switch
    {
        HeapGenerationTag.Gen0 => 0,
        HeapGenerationTag.Gen1 => 1,
        HeapGenerationTag.Gen2 => 2,
        HeapGenerationTag.Loh or HeapGenerationTag.Poh or HeapGenerationTag.Frozen => 3,
        _ => -1,
    };

    /// <summary>Reads <c>m_stateFlags</c>/<c>_stateFlags</c>, trying both field names in order.
    /// Returns <c>null</c> when neither field resolves to a readable Int32 value -- distinct from a
    /// resolved value of 0, since <see cref="AnalyzeTaskCompletionSources"/> treats "field genuinely
    /// absent" as "skip this candidate" rather than "classify as pending" (matching the pre-retyping
    /// analyzer's own <c>stateField is null =&gt; continue</c> gate there). The main task-classification
    /// loop above collapses <c>null</c> to 0 instead, matching that loop's own pre-retyping behavior of
    /// never skipping a task for a missing state field.</summary>
    private static int? TryReadStateFlags(IDumpObjectFieldQuery fields, ulong address)
    {
        if (fields.TryReadField(address, "m_stateFlags", out FieldValue value) && value.Kind == FieldValueKind.Int32)
            return value.AsInt32();
        if (fields.TryReadField(address, "_stateFlags", out value) && value.Kind == FieldValueKind.Int32)
            return value.AsInt32();
        return null;
    }

    private static bool TryReadObjectReferenceField(IDumpObjectFieldQuery fields, ulong address, string primaryName, string fallbackName, out ulong targetAddress)
    {
        if ((fields.TryReadField(address, primaryName, out FieldValue value) || fields.TryReadField(address, fallbackName, out value))
            && value.Kind == FieldValueKind.ObjectReference)
        {
            targetAddress = value.AsObjectAddress();
            return true;
        }

        targetAddress = 0;
        return false;
    }

    private static bool IsMultiContinuationList(string typeName) =>
        typeName.StartsWith(MultiContinuationListTypePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Reads the elements of a List&lt;object&gt; multi-continuation node via its backing
    /// <c>_items</c> array and <c>_size</c> fields. Returns <c>null</c> if the field layout couldn't
    /// be resolved (e.g. an unexpected list-like type) -- distinct from an empty, non-null list
    /// (a genuinely empty List&lt;object&gt;), matching the pre-retyping analyzer's own
    /// <c>TryReadListElements</c> true/false-with-cleared-buffer distinction. Null/zero elements are
    /// filtered out, matching that method's own "only add valid, non-zero elements" behavior --
    /// <see cref="IDumpArrayShapeQuery.EnumerateObjectReferenceElements"/> reports a null slot as
    /// address 0 rather than omitting it.
    /// </summary>
    private static List<ulong>? ReadListElements(ulong listAddress, IDumpObjectFieldQuery fields, IDumpArrayShapeQuery arrayShapes, int maxElements)
    {
        if (!fields.TryReadField(listAddress, "_items", out FieldValue itemsValue) || itemsValue.Kind != FieldValueKind.ObjectReference)
            return null;
        if (!fields.TryReadField(listAddress, "_size", out FieldValue sizeValue) || sizeValue.Kind != FieldValueKind.Int32)
            return null;

        ulong itemsAddress = itemsValue.AsObjectAddress();
        if (itemsAddress == 0)
            return null;

        int size = sizeValue.AsInt32();
        if (size <= 0)
            return [];

        IReadOnlyList<ulong> raw = arrayShapes.EnumerateObjectReferenceElements(itemsAddress, Math.Min(size, maxElements));
        var result = new List<ulong>(raw.Count);
        foreach (ulong elementAddress in raw)
        {
            if (elementAddress != 0)
                result.Add(elementAddress);
        }
        return result;
    }

    /// <summary>
    /// True branching exploration of a task's continuation graph. Returns the maximum depth (in
    /// genuine continuation hops -- List&lt;object&gt; wrapper nodes don't count) reachable from
    /// <paramref name="nodeAddress"/>. For every List&lt;object&gt; multi-continuation node
    /// encountered anywhere in the reachable subgraph, all elements are enumerated for fan-out stats
    /// and recursed into, so nested fan-out (a branch whose own continuation is itself another
    /// List&lt;object&gt;) is fully discovered rather than only the first branch walked.
    ///
    /// Caveats:
    /// - <paramref name="visited"/> is shared across the whole exploration to prevent re-expanding
    ///   cycles; if two branches converge on the same downstream node (a "diamond"), whichever
    ///   branch reaches it first claims it and the other branch is truncated there -- an accepted
    ///   approximation, not an exact max-depth guarantee.
    /// - <paramref name="nodeBudget"/> bounds total nodes visited per task, independent of depth, to
    ///   keep worst-case cost bounded for wide-and-deep fan-out trees.
    /// - The winning branch at each node is recorded into <paramref name="bestHopSelections"/>
    ///   (keyed by node address) so the caller can replay it afterward to build a display chain
    ///   without allocating per-branch lists during exploration.
    /// - <paramref name="rootCycleDetected"/> is set to true if a cycle leads back to the root task
    ///   (indicating a hard deadlock).
    /// </summary>
    private static int ExploreContinuation(
        ulong nodeAddress,
        HashSet<ulong> visited,
        ulong rootTaskAddress,
        ref int nodeBudget,
        int depthLevel,
        IDumpObjectFieldQuery fields,
        IHeapObjectLookup objectLookup,
        IDumpArrayShapeQuery arrayShapes,
        Dictionary<string, int> continuationCount,
        Dictionary<string, int> fanoutTypeCount,
        ref int totalContinuations,
        ref int multiContinuationNodes,
        ref int maxFanOut,
        ref bool rootCycleDetected,
        Dictionary<ulong, ulong> bestHopSelections)
    {
        if (nodeAddress == 0 || nodeBudget <= 0)
            return 0;

        if (!objectLookup.TryGetObject(nodeAddress, out HeapObjectRef node))
            return 0;

        if (!visited.Add(nodeAddress))
        {
            if (nodeAddress == rootTaskAddress)
                rootCycleDetected = true;
            return 0;
        }

        nodeBudget--;

        if (IsMultiContinuationList(node.TypeDisplayName))
        {
            List<ulong>? buffer = ReadListElements(nodeAddress, fields, arrayShapes, MaxFanOutElementsToEnumerate);
            if (buffer is null)
                return 0;

            multiContinuationNodes++;
            if (buffer.Count > maxFanOut)
                maxFanOut = buffer.Count;

            int bestDepth = 0;
            ulong bestElemAddress = 0;
            foreach (ulong elemAddress in buffer)
            {
                if (objectLookup.TryGetObject(elemAddress, out HeapObjectRef elemObj))
                    IncrementCount(fanoutTypeCount, elemObj.TypeDisplayName);

                int elemDepth = ExploreContinuation(
                    elemAddress, visited, rootTaskAddress, ref nodeBudget, depthLevel + 1,
                    fields, objectLookup, arrayShapes,
                    continuationCount, fanoutTypeCount, ref totalContinuations, ref multiContinuationNodes, ref maxFanOut,
                    ref rootCycleDetected, bestHopSelections);

                if (elemDepth > bestDepth)
                {
                    bestDepth = elemDepth;
                    bestElemAddress = elemAddress;
                }
            }

            if (bestElemAddress != 0)
                bestHopSelections[nodeAddress] = bestElemAddress;

            return bestDepth;
        }

        totalContinuations++;
        IncrementCount(continuationCount, node.TypeDisplayName);

        if (!TryReadObjectReferenceField(fields, nodeAddress, "m_continuationObject", "_continuationObject", out ulong nextAddress) || nextAddress == 0)
            return 1;

        bestHopSelections[nodeAddress] = nextAddress;

        int childDepth = ExploreContinuation(
            nextAddress, visited, rootTaskAddress, ref nodeBudget, depthLevel + 1,
            fields, objectLookup, arrayShapes,
            continuationCount, fanoutTypeCount, ref totalContinuations, ref multiContinuationNodes, ref maxFanOut,
            ref rootCycleDetected, bestHopSelections);

        return 1 + childDepth;
    }

    private static string? ExtractResultType(string typeName)
    {
        // "System.Threading.Tasks.Task`1[[System.String, ...]]" -> "System.String"
        int start = typeName.IndexOf("[[", StringComparison.Ordinal);
        if (start < 0) return null;
        start += 2;
        int end = typeName.IndexOf(',', start);
        if (end < 0) end = typeName.IndexOf("]]", start, StringComparison.Ordinal);
        if (end <= start) return null;
        return typeName[start..end];
    }

    private static (string? ExceptionType, string? ExceptionMessage) ExtractFaultedTaskException(
        IDumpObjectFieldQuery fields, IHeapObjectLookup objectLookup, IHeapReferenceQuery references, ulong taskAddress)
    {
        var visited = new HashSet<ulong>(capacity: 16);

        foreach (string fieldName in new[] { "m_contingentProperties", "_contingentProperties" })
        {
            if (!fields.TryReadField(taskAddress, fieldName, out FieldValue contingentValue) || contingentValue.Kind != FieldValueKind.ObjectReference)
                continue;

            ulong contingentAddress = contingentValue.AsObjectAddress();
            if (contingentAddress == 0)
                continue;

            if (TryFindExceptionLikeObject(contingentAddress, visited, 0, fields, objectLookup, references, out HeapObjectRef exceptionObj)
                && TryReadExceptionSummary(exceptionObj, fields, objectLookup, out string? exceptionType, out string? message))
                return (exceptionType, message);
        }

        if (TryFindExceptionLikeObject(taskAddress, visited, 0, fields, objectLookup, references, out HeapObjectRef fallbackExceptionObj)
            && TryReadExceptionSummary(fallbackExceptionObj, fields, objectLookup, out string? fallbackType, out string? fallbackMessage))
            return (fallbackType, fallbackMessage);

        return (null, null);
    }

    private static bool TryFindExceptionLikeObject(
        ulong sourceAddress, HashSet<ulong> visited, int depth,
        IDumpObjectFieldQuery fields, IHeapObjectLookup objectLookup, IHeapReferenceQuery references,
        out HeapObjectRef exceptionObj)
    {
        return CapabilityObjectFieldTraversal.TryFindByPredicate(
            sourceAddress, visited, depth, MaxExceptionSearchDepth, ExceptionRelatedFields,
            fields, objectLookup, references,
            isMatch: candidate =>
            {
                string typeName = candidate.TypeDisplayName;
                return !string.IsNullOrWhiteSpace(typeName)
                    && typeName.Contains("Exception", StringComparison.Ordinal)
                    && !typeName.Contains("ExceptionDispatchInfo", StringComparison.Ordinal)
                    && TryReadExceptionSummary(candidate, fields, objectLookup, out _, out _);
            },
            out exceptionObj);
    }

    private static bool TryReadExceptionSummary(
        HeapObjectRef exceptionObj, IDumpObjectFieldQuery fields, IHeapObjectLookup objectLookup,
        out string? exceptionType, out string? message)
    {
        exceptionType = exceptionObj.TypeDisplayName;
        message = null;

        if (exceptionType.Contains("ExceptionDispatchInfo", StringComparison.Ordinal))
        {
            foreach (string fieldName in new[] { "_exception", "m_exception" })
            {
                if (!fields.TryReadField(exceptionObj.Address, fieldName, out FieldValue innerValue) || innerValue.Kind != FieldValueKind.ObjectReference)
                    continue;

                ulong innerAddress = innerValue.AsObjectAddress();
                if (innerAddress == 0 || !objectLookup.TryGetObject(innerAddress, out HeapObjectRef inner))
                    continue;

                if (TryReadExceptionSummary(inner, fields, objectLookup, out exceptionType, out message))
                    return true;
            }

            return false;
        }

        if (fields.TryReadField(exceptionObj.Address, "_message", out FieldValue messageValue) && messageValue.Kind == FieldValueKind.String)
            message = messageValue.AsString();

        if (string.IsNullOrWhiteSpace(message))
            message = null;

        return !string.IsNullOrWhiteSpace(exceptionType);
    }

    private static void IncrementCount(Dictionary<string, int> dict, string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        dict.TryGetValue(key, out int count);
        dict[key] = count + 1;
    }

    private static IReadOnlyList<FaultedTaskTypeProfile> BuildFaultedTaskTypeProfiles(
        Dictionary<string, int> faultedCounts,
        Dictionary<string, Dictionary<string, int>> faultedExceptions)
    {
        IReadOnlyList<NameCountEntry> topFaulted = BuildSorted(faultedCounts);
        var profiles = new List<FaultedTaskTypeProfile>(capacity: topFaulted.Count);

        foreach (NameCountEntry entry in topFaulted)
        {
            IReadOnlyList<NameCountEntry> exceptionHistogram = faultedExceptions.TryGetValue(entry.Name, out Dictionary<string, int>? exceptionCounts)
                ? BuildSorted(exceptionCounts)
                : [];

            profiles.Add(new FaultedTaskTypeProfile(entry.Name, entry.Count, exceptionHistogram));
        }

        return profiles;
    }

    // Complete population, descending by count -- no Top-N cap. Per-type dictionaries are bounded
    // by distinct type count (small relative to instance count), so a full sort is cheap; the
    // render layer slices for display.
    private static IReadOnlyList<NameCountEntry> BuildSorted(Dictionary<string, int> counts)
    {
        if (counts.Count == 0) return [];

        var result = new List<NameCountEntry>(counts.Count);
        foreach (KeyValuePair<string, int> kvp in counts)
            result.Add(new NameCountEntry(kvp.Key, kvp.Value));
        result.Sort((a, b) => b.Count.CompareTo(a.Count));
        return result;
    }

    private static IReadOnlyList<NameSizeCountEntry> BuildSortedByBytes(
        Dictionary<string, (long TotalBytes, int Count)> byteCounts)
    {
        if (byteCounts.Count == 0) return [];

        var result = new List<NameSizeCountEntry>(byteCounts.Count);
        foreach (KeyValuePair<string, (long TotalBytes, int Count)> kvp in byteCounts)
            result.Add(new NameSizeCountEntry(kvp.Key, kvp.Value.TotalBytes, kvp.Value.Count));
        result.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        return result;
    }

    private static AsyncTaskDomainResult EmptyWith(TcsAnalysisResult tcsResult, VtsAnalysisResult vtsResult) => new(
        TotalTasks: 0,
        PendingTasks: 0,
        RunningTasks: 0,
        FaultedTasks: 0,
        CanceledTasks: 0,
        CompletedTasks: 0,
        OrphanedTasks: 0,
        TotalTaskContinuations: 0,
        MaxContinuationDepth: 0,
        AvgContinuationDepth: 0.0,
        TopPendingTaskTypes: [],
        TopFaultedTaskTypes: [],
        TopContinuationTypes: [],
        TopOrphanedTasks: [],
        TopDeepestChains: [],
        FaultedTaskExceptionHistograms: [],
        MultiContinuationNodeCount: 0,
        MaxContinuationFanOut: 0,
        TopContinuationFanoutTypes: [],
        DepthSampleCount: 0,
        CycleDetected: false,
        PendingGen0: 0,
        PendingGen1: 0,
        PendingGen2: 0,
        PendingLOH: 0,
        TopPendingTaskTypesByBytes: [],
        TotalTaskCompletionSources: tcsResult.TotalTcs,
        UnresolvedTaskCompletionSources: tcsResult.UnresolvedTcs,
        UnresolvedTcsGen2Count: tcsResult.UnresolvedTcsGen2Count,
        TopUnresolvedTaskCompletionSources: tcsResult.TopUnresolved,
        TotalValueTaskSources: vtsResult.TotalVts,
        PendingValueTaskSources: vtsResult.PendingVts,
        PendingVtsGen2Count: vtsResult.PendingVtsGen2Count,
        TopPendingValueTaskSources: vtsResult.TopPending);

    public void Dispose() { }
}
