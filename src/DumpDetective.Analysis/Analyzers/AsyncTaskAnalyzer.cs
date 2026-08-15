using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

internal sealed class AsyncTaskAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant
{
    // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
    // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
    // OnHeapEntry; consumed by LoadTaskEntries once the shared index scan has completed.
    private HashSet<ulong>? _taskMts;
    private List<(ulong Address, ulong Mt, int StateFlags)>? _participantEntries;
    private int _participantMaxTasksToScan;
    // Set by OnHeapIndexScanCompleted — the single source of truth for whether
    // _participantEntries is trustworthy. Avoids re-deriving "did the shared scan run"
    // from a second cache.TryGetHeapIndex call in LoadTaskEntries.
    private bool _participantScanSucceeded;

    // TaskCompletionSource candidate collection — rides the same shared heap-index scan
    // pass as _taskMts/_participantEntries above (no extra heap/disk pass). Unlike task MTs,
    // there's no precomputed TypeAggregateFlags bit for this (see TaskTypeNamePattern), so
    // the candidate set is built by resolving each distinct TypeAggregates MT's name once in
    // BeforeHeapIndexScan — bounded by distinct type count, not object count.
    private HashSet<ulong>? _tcsMts;
    private List<(ulong Address, ulong Mt)>? _participantTcsEntries;
    private int _participantMaxTcsToScan;

    // IValueTaskSource candidate collection — same rides-the-shared-scan approach as _tcsMts
    // above. Unlike TCS (a name-prefix check), detection here requires per-type interface
    // enumeration (see ValueTaskSourcePattern) plus locating the embedded
    // ManualResetValueTaskSourceCore<T> field, so the resolved ClrInstanceField is cached here
    // during BeforeHeapIndexScan (once per distinct type) for direct reuse in Phase 2.
    private HashSet<ulong>? _vtsMts;
    private List<(ulong Address, ulong Mt)>? _participantVtsEntries;
    private int _participantMaxVtsToScan;
    private Dictionary<ulong, ClrInstanceField>? _vtsCoreFieldByMt;

    // Field cache by MethodTable to avoid repeated ClrMD lookups per type
    // Maps (MethodTable, fieldName) to ClrInstanceField? (null if field not found on that type)
    private Dictionary<(ulong MethodTable, string FieldName), ClrInstanceField?>? _fieldCacheByMt;

    // Pool of reusable buffers for enumerating List<object> multi-continuation elements,
    // indexed by recursion depth level. A single shared buffer isn't safe here because a
    // nested List<object> (fan-out below fan-out) would recurse into TryReadListElements
    // again and clear the buffer the outer frame is still iterating. Pool size is bounded
    // by MaxContinuationDepth and reused across the whole task scan — no steady-state
    // per-node allocation once warmed up.
    private List<List<ClrObject>>? _continuationListBufferPool;

    // Per-task record of which immediate child was chosen as the deepest branch at each
    // visited node address — populated during ExploreContinuation, consumed once afterward
    // to reconstruct the winning chain's type names for the top-5 deepest chains display.
    // Cleared and reused per task.
    private Dictionary<ulong, ClrObject>? _bestHopSelections;


    // m_stateFlags bit masks (matches HangAnalyzer)
    private const int MaskCompleted = 0x1000000;
    private const int MaskFaulted = 0x200000;
    private const int MaskCanceled = 0x400000;
    private const int MaskRunning = 0x10000; // TASK_STATE_DELEGATE_INVOKED

    // Sentinel continuation type — no-op callback; indicates orphan
    private const string NoOpContinuationType = "System.Threading.Tasks.Task+<>c";
    // When >1 continuation is attached to a Task, the runtime replaces the scalar
    // m_continuationObject with a List<object> holding all attached continuations.
    private const string MultiContinuationListTypePrefix = "System.Collections.Generic.List<";
    // Sanity cap on fan-out enumeration — protects against corrupted/adversarial size fields.
    private const int MaxFanOutElementsToEnumerate = 10_000;
    // Global per-task cap on total nodes visited during continuation-tree exploration.
    // Bounds worst-case cost for pathological/adversarial fan-out trees (wide-and-deep
    // multi-continuation graphs) independent of MaxContinuationDepth, which only bounds
    // the depth of any single branch, not the total size of the reachable subgraph.
    private const int MaxContinuationNodesToVisitPerTask = 2_000;
    private static readonly string[] ExceptionRelatedFields =
    [
        "m_exceptionsHolder",
        "_exceptionsHolder",
        "m_contingentProperties",
        "_contingentProperties",
        "m_faultExceptions",
        "_faultExceptions",
        "_exception",
        "m_exception",
    ];

    public string Name => "Async Task Analysis";
    public string Category => "Async";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AsyncTaskAnalysisOptions options = context.AnalysisOptions.AsyncTaskAnalysis;
        return ValueTask.FromResult(Analyze(context.Heap, context.Cache, context.Progress, options, cancellationToken).Stamp(this));
    }

    // Resets per-entry accumulator fields ahead of the shared heap-index scan pass.
    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        _participantMaxTasksToScan = context.AnalysisOptions.AsyncTaskAnalysis.MaxTasksToScan;
        _participantMaxTcsToScan = context.AnalysisOptions.AsyncTaskAnalysis.MaxTcsToScan;
        _participantMaxVtsToScan = context.AnalysisOptions.AsyncTaskAnalysis.MaxVtsToScan;
        _participantEntries = new List<(ulong, ulong, int)>(capacity: 1024);
        _participantTcsEntries = new List<(ulong, ulong)>(capacity: 64);
        _participantVtsEntries = new List<(ulong, ulong)>(capacity: 64);
        _taskMts = null;
        _tcsMts = null;
        _vtsMts = null;
        _vtsCoreFieldByMt = null;
        _fieldCacheByMt = new Dictionary<(ulong, string), ClrInstanceField?>(capacity: 32);

        if (context.Cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
        {
            _taskMts = new HashSet<ulong>(capacity: 32);
            _tcsMts = new HashSet<ulong>(capacity: 8);
            _vtsMts = new HashSet<ulong>(capacity: 8);
            _vtsCoreFieldByMt = new Dictionary<ulong, ClrInstanceField>(capacity: 8);
            foreach (var kvp in heapIndex.TypeAggregates)
            {
                if ((kvp.Value.Flags & TypeAggregateFlags.IsTaskType) != 0)
                {
                    _taskMts.Add(kvp.Key);
                    continue;
                }

                // No precomputed TypeAggregateFlags bit for TaskCompletionSource or
                // IValueTaskSource — resolve the type once per distinct MT here (bounded by
                // type count, not object count) and check both.
                ClrType? type = context.Heap.GetTypeByMethodTable(kvp.Key);
                if (type is null)
                    continue;

                if (type.Name != null && TaskTypeNamePattern.IsTaskCompletionSource(type.Name))
                {
                    _tcsMts.Add(kvp.Key);
                    continue;
                }

                if (ValueTaskSourcePattern.ImplementsValueTaskSource(type))
                {
                    ClrInstanceField? coreField = ValueTaskSourcePattern.FindCoreField(type);
                    if (coreField != null)
                    {
                        _vtsMts.Add(kvp.Key);
                        _vtsCoreFieldByMt[kvp.Key] = coreField;
                    }
                }
            }
        }
    }

    public void OnHeapEntry(in HeapEntry entry)
    {
        if (_taskMts is not null && _participantEntries!.Count < _participantMaxTasksToScan
            && _taskMts.Contains(entry.MethodTable))
        {
            _participantEntries.Add((entry.Address, entry.MethodTable, 0)); // StateFlags resolved in Phase 2
            return;
        }

        if (_tcsMts is not null && _participantTcsEntries!.Count < _participantMaxTcsToScan
            && _tcsMts.Contains(entry.MethodTable))
        {
            _participantTcsEntries.Add((entry.Address, entry.MethodTable));
            return;
        }

        if (_vtsMts is not null && _participantVtsEntries!.Count < _participantMaxVtsToScan
            && _vtsMts.Contains(entry.MethodTable))
        {
            _participantVtsEntries.Add((entry.Address, entry.MethodTable));
        }
    }

    public void OnHeapIndexScanCompleted(bool succeeded) => _participantScanSucceeded = succeeded;

    public IHeapIndexScanParticipant CreateWorkerInstance() => new AsyncTaskAnalyzer();

    // Each worker (including this instance, which owns range 0) scans its own range uncapped
    // relative to the others — OnHeapEntry's _participantMaxTasksToScan guard already caps
    // every worker at the full limit, not a divided share, so no worker starves itself if
    // matches cluster in one address range. Re-sort the union by address and trim to the
    // true global cap here, once, after every worker has finished.
    public void MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        foreach (IHeapIndexScanParticipant p in partials)
        {
            var other = (AsyncTaskAnalyzer)p;
            if (other._participantEntries is not null)
                _participantEntries!.AddRange(other._participantEntries);
            if (other._participantTcsEntries is not null)
                _participantTcsEntries!.AddRange(other._participantTcsEntries);
            if (other._participantVtsEntries is not null)
                _participantVtsEntries!.AddRange(other._participantVtsEntries);
        }

        _participantEntries = _participantEntries!
            .OrderBy(e => e.Address)
            .Take(_participantMaxTasksToScan)
            .ToList();

        _participantTcsEntries = _participantTcsEntries!
            .OrderBy(e => e.Address)
            .Take(_participantMaxTcsToScan)
            .ToList();

        _participantVtsEntries = _participantVtsEntries!
            .OrderBy(e => e.Address)
            .Take(_participantMaxVtsToScan)
            .ToList();
    }

    private AnalyzerDomainResult Analyze(
        ClrHeap heap,
        IHeapAnalysisCache? cache,
        IProgress<AnalyzerProgressReport>? progress,
        AsyncTaskAnalysisOptions options,
        CancellationToken ct)
    {
        // ── Step 1: Resolve task entries (TaskIndex.bin fast path or heap fallback) ──────
        progress?.Report(new(0, "loading task index"));

        var taskEntries = LoadTaskEntries(heap, cache, progress, options, ct);
        int total = taskEntries.Count;

        // TaskCompletionSource and IValueTaskSource entries are independent of task-scan results
        // (a heap could in principle have TCS/VTS instances even when the task scan finds none),
        // so both run regardless of the early-return below.
        var tcsEntries = LoadTcsEntries(heap, cache, progress, options, ct);
        var tcsResult = AnalyzeTaskCompletionSources(heap, tcsEntries, options, ct);

        var vtsEntries = LoadVtsEntries(heap, cache, progress, options, ct);
        var vtsResult = AnalyzeValueTaskSources(heap, vtsEntries, options, ct);

        if (total == 0)
        {
            return new AsyncTaskDomainResult(
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
                TaskScanLimited: false,
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
                TcsScanLimited: tcsResult.TcsScanLimited,
                TopUnresolvedTaskCompletionSources: tcsResult.TopUnresolved,
                TotalValueTaskSources: vtsResult.TotalVts,
                PendingValueTaskSources: vtsResult.PendingVts,
                PendingVtsGen2Count: vtsResult.PendingVtsGen2Count,
                VtsScanLimited: vtsResult.VtsScanLimited,
                TopPendingValueTaskSources: vtsResult.TopPending);
        }

        bool taskScanLimited = total >= options.MaxTasksToScan;

        // ── Step 2: Classify task states ─────────────────────────────────────────────────
        progress?.Report(new(0, "classifying task states", $"0 / {total:N0} tasks"));

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

        // MT → type-name cache to avoid repeated ClrMD lookups
        var typeNameByMt = new Dictionary<ulong, string>(capacity: 64);

        int classifyScanCount = 0;
        for (int i = 0; i < taskEntries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (address, mt, stateFlags) = taskEntries[i];

            classifyScanCount++;
            if (classifyScanCount % 5000 == 0)
                progress?.Report(new(classifyScanCount, "classifying task states",
                    $"{classifyScanCount:N0} / {total:N0} tasks"));

            // Single heap.GetObject call per task, reused below for stateFlags re-read,
            // faulted-exception extraction, and BFS continuation traversal — previously
            // three separate lookups of the same address.
            ClrObject taskObj = heap.GetObject(address);

            // Re-read stateFlags from ClrMD if written as 0 during Phase 1
            if (stateFlags == 0 && taskObj.IsValid && taskObj.Type != null)
            {
                var stateField = TryGetCachedField(taskObj.Type, mt, "m_stateFlags", "_stateFlags");
                if (stateField != null)
                    stateFlags = stateField.Read<int>(taskObj, interior: false);
            }

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
                int gen = SegmentKindMapper.ResolveGeneration(heap, address);
                if (gen == 0) pendingGen0++;
                else if (gen == 1) pendingGen1++;
                else if (gen == 2) pendingGen2++;
                else if (gen == 3) pendingLOH++;
            }

            // Resolve type name
            string typeName = ResolveTypeName(heap, mt, typeNameByMt);

            // Track top type counts
            if (!isCompleted && !isCanceled)
            {
                if (isFaulted)
                {
                    IncrementCount(faultedTypeCount, typeName);
                    // Extract exception type for histogram
                    if (taskObj.IsValid && taskObj.Type != null)
                    {
                        (string? exceptionType, _) = ExtractFaultedTaskException(taskObj);
                        if (exceptionType != null)
                        {
                            if (!faultedTypeExceptions.TryGetValue(typeName, out var exceptionHistogram))
                            {
                                exceptionHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
                                faultedTypeExceptions[typeName] = exceptionHistogram;
                            }
                            IncrementCount(exceptionHistogram, exceptionType);
                        }
                    }
                }
                else
                {
                    IncrementCount(pendingTypeCount, typeName);
                    if (taskObj.IsValid)
                    {
                        pendingTypeBytes.TryGetValue(typeName, out var byteEntry);
                        pendingTypeBytes[typeName] = (byteEntry.TotalBytes + (long)taskObj.Size, byteEntry.Count + 1);
                    }
                }
            }

            // ── Orphan detection + continuation chain BFS ─────────────────────
            // taskEntries is already capped at MaxTasksToScan by all load paths;
            // BFS runs for every task with a valid continuation.
            if (taskObj.IsValid && taskObj.Type != null)
            {
                var continuationField = TryGetCachedField(taskObj.Type, mt, "m_continuationObject", "_continuationObject");
                if (continuationField != null)
                {
                    ClrObject continuationObj = continuationField.ReadObject(taskObj, interior: false);

                    bool isOrphan = !continuationObj.IsValid
                        || continuationObj.Address == 0
                        || string.Equals(continuationObj.Type?.Name, NoOpContinuationType, StringComparison.Ordinal);

                        var chainTypes = new List<string>(capacity: 8);
                        chainTypes.Add(typeName);

                    if (isOrphan && !isCompleted && !isCanceled)
                    {
                        orphaned++;
                        if (orphanedSnapshots.Count < options.TopOrphanedToShow)
                        {
                            string? resultType = ExtractResultType(typeName);
                            ulong size = taskObj.Size;
                            (string? exceptionType, string? exceptionMessage) = ExtractFaultedTaskException(taskObj);
                            orphanedSnapshots.Add(new OrphanedTaskSnapshot(address, typeName, resultType, size, exceptionType, exceptionMessage));
                        }
                    }

                    // BFS chain depth — true branching traversal. When m_continuationObject
                    // holds a List<object> (2+ continuations attached), every branch is
                    // explored to find the true maximum depth and to fully capture fan-out
                    // stats (not just the branch that happens to be walked first). See
                    // ExploreContinuation for the node-budget and diamond-convergence caveats.
                    if (continuationObj.IsValid && continuationObj.Address != 0)
                    {
                        var visited = new HashSet<ulong>(capacity: 16) { address };
                        _bestHopSelections ??= new Dictionary<ulong, ClrObject>(capacity: 32);
                        _bestHopSelections.Clear();
                        int nodeBudget = MaxContinuationNodesToVisitPerTask;

                        int depth = ExploreContinuation(
                            continuationObj, visited, address, options.MaxContinuationDepth, ref nodeBudget, depthLevel: 0,
                            continuationCount, fanoutTypeCount, ref totalContinuations, ref multiContinuationNodes, ref maxFanOut,
                            ref cycleDetected, _bestHopSelections);

                        // Reconstruct the winning branch's type-name sequence for display by
                        // replaying the per-node decisions recorded during exploration. Bounded
                        // by the node-visit budget as a defensive guard — the recorded
                        // selections form a DAG (visited-set enforced during construction) so
                        // this terminates naturally once no further hop was recorded.
                        ClrObject hop = continuationObj;
                        int reconstructSteps = 0;
                        while (hop.IsValid && hop.Address != 0 && reconstructSteps++ < MaxContinuationNodesToVisitPerTask)
                        {
                            if (!IsMultiContinuationList(hop.Type) && hop.Type != null)
                                chainTypes.Add(hop.Type.Name ?? string.Empty);

                            if (!_bestHopSelections.TryGetValue(hop.Address, out ClrObject nextHop))
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
        }

        progress?.Report(new(classifyScanCount, "aggregating results",
            $"{total:N0} tasks classified, {orphaned:N0} orphaned"));

        double avgDepth = depthSampleCount > 0 ? (double)totalDepthSum / depthSampleCount : 0.0;

        var faultedProfiles = BuildFaultedTaskTypeProfiles(faultedTypeCount, faultedTypeExceptions, options.TopTypesToShow);

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
            TaskScanLimited: taskScanLimited,
            TopPendingTaskTypes: BuildTopN(pendingTypeCount, options.TopTypesToShow),
            TopFaultedTaskTypes: BuildTopN(faultedTypeCount, options.TopTypesToShow),
            TopContinuationTypes: BuildTopN(continuationCount, options.TopTypesToShow),
            TopOrphanedTasks: orphanedSnapshots,
            TopDeepestChains: deepestChains.OrderByDescending(chain => chain.Depth).ToArray(),
            FaultedTaskExceptionHistograms: faultedProfiles,
            MultiContinuationNodeCount: multiContinuationNodes,
            MaxContinuationFanOut: maxFanOut,
            TopContinuationFanoutTypes: BuildTopN(fanoutTypeCount, options.TopTypesToShow),
            DepthSampleCount: depthSampleCount,
            CycleDetected: cycleDetected,
            PendingGen0: pendingGen0,
            PendingGen1: pendingGen1,
            PendingGen2: pendingGen2,
            PendingLOH: pendingLOH,
            TopPendingTaskTypesByBytes: BuildTopNByBytes(pendingTypeBytes, options.TopTypesToShow),
            TotalTaskCompletionSources: tcsResult.TotalTcs,
            UnresolvedTaskCompletionSources: tcsResult.UnresolvedTcs,
            UnresolvedTcsGen2Count: tcsResult.UnresolvedTcsGen2Count,
            TcsScanLimited: tcsResult.TcsScanLimited,
            TopUnresolvedTaskCompletionSources: tcsResult.TopUnresolved,
            TotalValueTaskSources: vtsResult.TotalVts,
            PendingValueTaskSources: vtsResult.PendingVts,
            PendingVtsGen2Count: vtsResult.PendingVtsGen2Count,
            VtsScanLimited: vtsResult.VtsScanLimited,
            TopPendingValueTaskSources: vtsResult.TopPending);
    }

    // ── TaskIndex.bin reader ──────────────────────────────────────────────────

    /// <summary>
    /// Loads task entries from <c>TaskIndex.bin</c> if available, otherwise falls back to
    /// a filtered heap scan using <see cref="TypeAggregateFlags.IsTaskType"/>.
    /// Returns at most <see cref="MaxTasksToScan"/> entries (scan-limit respected).
    /// </summary>
    private List<(ulong Address, ulong Mt, int StateFlags)> LoadTaskEntries(
        ClrHeap heap,
        IHeapAnalysisCache? cache,
        IProgress<AnalyzerProgressReport>? progress,
        AsyncTaskAnalysisOptions options,
        CancellationToken ct)
    {
        // Fast path: TaskIndex.bin exists
        if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
        {
            // Memory-backed mode: InMemoryTaskCandidates was collected during Phase 1 at zero
            // extra scanning cost — use it directly to avoid an O(N_total) scan of InMemoryEntries.
            if (heapIndex.InMemoryTaskCandidates is { Length: > 0 } inMemCandidates)
                return ConvertInMemoryTaskCandidates(inMemCandidates, options.MaxTasksToScan);

            var entries = TaskIndexReader.ReadTaskIndexFile(heapIndex.IndexPath, options.MaxTasksToScan, ct);
            if (entries != null)
            {
                if (entries.Count > 0)
                    progress?.Report(new(entries.Count, "task index loaded",
                        $"{entries.Count:N0} task records read"));
                return entries;
            }

            // BeforeHeapIndexScan/OnHeapEntry already ran via the pipeline's
            // HeapIndexScanDispatcher before AnalyzeAsync executes; read back
            // participant-accumulated state instead of re-scanning the index. If the shared
            // scan failed partway (isolated by the dispatcher), _participantEntries may be
            // incomplete, so fall back to a raw heap scan instead of trusting it.
            return _participantScanSucceeded ? (_participantEntries ?? []) : ScanRawHeapForTasks(heap, progress, options.MaxTasksToScan, ct);
        }

        // No cache — full heap scan
        return ScanRawHeapForTasks(heap, progress, options.MaxTasksToScan, ct);
    }

    private static List<(ulong, ulong, int)> ConvertInMemoryTaskCandidates((ulong Addr, ulong Mt)[] candidates, int maxTasksToScan)
    {
        int cap = Math.Min(candidates.Length, maxTasksToScan);
        var result = new List<(ulong, ulong, int)>(cap);
        for (int i = 0; i < cap; i++)
            result.Add((candidates[i].Addr, candidates[i].Mt, 0)); // StateFlags resolved in Phase 2
        return result;
    }

    private static List<(ulong, ulong, int)> ScanRawHeapForTasks(
        ClrHeap heap,
        IProgress<AnalyzerProgressReport>? progress,
        int maxTasksToScan,
        CancellationToken ct)
    {
        var result = new List<(ulong, ulong, int)>(capacity: 512);
        var scanCounter = new ObjectScanCounter("scanning task objects", progress);
        var stateFieldCache = new Dictionary<ulong, ClrInstanceField?>(capacity: 8);

        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            scanCounter.Tick();

            if (!obj.IsValid || obj.Type is null)
                continue;

            string? typeName = obj.Type.Name;
            if (typeName is null || !TaskTypeNamePattern.IsTaskType(typeName))
                continue;

            // obj.Type is already resolved here, so reading m_stateFlags now is free
            // relative to the Phase 2 re-read it would otherwise trigger.
            ulong mt = obj.Type.MethodTable;
            if (!stateFieldCache.TryGetValue(mt, out ClrInstanceField? stateField))
            {
                stateField = obj.Type.GetFieldByName("m_stateFlags") ?? obj.Type.GetFieldByName("_stateFlags");
                stateFieldCache[mt] = stateField;
            }
            int stateFlags = stateField != null ? stateField.Read<int>(obj, interior: false) : 0;

            result.Add((obj.Address, mt, stateFlags));
            if (result.Count >= maxTasksToScan)
                break;
        }

        scanCounter.Complete();
        return result;
    }

    // ── TaskCompletionSource entry loading ──────────────────────────────────────

    /// <summary>
    /// Loads TaskCompletionSource candidate entries. No disk satellite index exists for these
    /// (unlike TaskIndex.bin) — TCS instance counts are a bounded resource (created explicitly
    /// per pending external operation, not per compiler-generated async call like Task), so the
    /// participant-collected list from the shared heap-index scan (or a raw heap scan fallback
    /// when no index exists) is sufficient without a dedicated fast-path file.
    /// </summary>
    private List<(ulong Address, ulong Mt)> LoadTcsEntries(
        ClrHeap heap,
        IHeapAnalysisCache? cache,
        IProgress<AnalyzerProgressReport>? progress,
        AsyncTaskAnalysisOptions options,
        CancellationToken ct)
    {
        if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out _))
        {
            return _participantScanSucceeded
                ? (_participantTcsEntries ?? [])
                : ScanRawHeapForTcs(heap, progress, options.MaxTcsToScan, ct);
        }

        return ScanRawHeapForTcs(heap, progress, options.MaxTcsToScan, ct);
    }

    private static List<(ulong, ulong)> ScanRawHeapForTcs(
        ClrHeap heap,
        IProgress<AnalyzerProgressReport>? progress,
        int maxTcsToScan,
        CancellationToken ct)
    {
        var result = new List<(ulong, ulong)>(capacity: 64);
        var scanCounter = new ObjectScanCounter("scanning TaskCompletionSource objects", progress);

        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            scanCounter.Tick();

            if (!obj.IsValid || obj.Type is null)
                continue;

            string? typeName = obj.Type.Name;
            if (typeName is null || !TaskTypeNamePattern.IsTaskCompletionSource(typeName))
                continue;

            result.Add((obj.Address, obj.Type.MethodTable));
            if (result.Count >= maxTcsToScan)
                break;
        }

        scanCounter.Complete();
        return result;
    }

    // ── IValueTaskSource entry loading ──────────────────────────────────────────

    /// <summary>
    /// Loads IValueTaskSource candidate entries. Same rationale as <see cref="LoadTcsEntries"/>
    /// for skipping a dedicated disk satellite index — these are a bounded resource, not created
    /// per compiler-generated async call.
    /// </summary>
    private List<(ulong Address, ulong Mt)> LoadVtsEntries(
        ClrHeap heap,
        IHeapAnalysisCache? cache,
        IProgress<AnalyzerProgressReport>? progress,
        AsyncTaskAnalysisOptions options,
        CancellationToken ct)
    {
        if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out _))
        {
            return _participantScanSucceeded
                ? (_participantVtsEntries ?? [])
                : ScanRawHeapForVts(heap, progress, options.MaxVtsToScan, ct);
        }

        return ScanRawHeapForVts(heap, progress, options.MaxVtsToScan, ct);
    }

    private static List<(ulong, ulong)> ScanRawHeapForVts(
        ClrHeap heap,
        IProgress<AnalyzerProgressReport>? progress,
        int maxVtsToScan,
        CancellationToken ct)
    {
        var result = new List<(ulong, ulong)>(capacity: 64);
        var scanCounter = new ObjectScanCounter("scanning IValueTaskSource objects", progress);
        // Interface enumeration is more expensive than a name check, so cache the negative
        // result per-MT to avoid re-checking every instance of a non-matching type.
        var checkedNonMatchMts = new HashSet<ulong>(capacity: 64);

        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            scanCounter.Tick();

            if (!obj.IsValid || obj.Type is null)
                continue;

            ulong mt = obj.Type.MethodTable;
            if (checkedNonMatchMts.Contains(mt))
                continue;

            if (!ValueTaskSourcePattern.ImplementsValueTaskSource(obj.Type) || ValueTaskSourcePattern.FindCoreField(obj.Type) is null)
            {
                checkedNonMatchMts.Add(mt);
                continue;
            }

            result.Add((obj.Address, mt));
            if (result.Count >= maxVtsToScan)
                break;
        }

        scanCounter.Complete();
        return result;
    }

    private readonly record struct VtsAnalysisResult(
        int TotalVts,
        int PendingVts,
        int PendingVtsGen2Count,
        bool VtsScanLimited,
        IReadOnlyList<PendingValueTaskSourceSnapshot> TopPending);

    /// <summary>
    /// Classifies each IValueTaskSource candidate by reading its embedded
    /// ManualResetValueTaskSourceCore&lt;T&gt; field (resolved during BeforeHeapIndexScan, or
    /// lazily here for the raw-heap-scan fallback path) and checking its <c>_completed</c> flag.
    /// A false value means the source hasn't signaled completion — either a normal in-flight
    /// operation, or (if it has survived into Gen2/LOH) a stuck one. See
    /// <see cref="PendingValueTaskSourceSnapshot"/>.
    /// </summary>
    private VtsAnalysisResult AnalyzeValueTaskSources(
        ClrHeap heap,
        List<(ulong Address, ulong Mt)> vtsEntries,
        AsyncTaskAnalysisOptions options,
        CancellationToken ct)
    {
        if (vtsEntries.Count == 0)
            return new VtsAnalysisResult(0, 0, 0, false, []);

        bool vtsScanLimited = vtsEntries.Count >= options.MaxVtsToScan;
        int pendingVts = 0;
        int pendingVtsGen2 = 0;
        var pendingSnapshots = new List<PendingValueTaskSourceSnapshot>(capacity: 32);
        _vtsCoreFieldByMt ??= new Dictionary<ulong, ClrInstanceField>(capacity: 8);

        for (int i = 0; i < vtsEntries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (address, mt) = vtsEntries[i];

            if (!_vtsCoreFieldByMt.TryGetValue(mt, out ClrInstanceField? coreField))
            {
                ClrType? type = heap.GetTypeByMethodTable(mt);
                coreField = type != null ? ValueTaskSourcePattern.FindCoreField(type) : null;
                if (coreField != null)
                    _vtsCoreFieldByMt[mt] = coreField;
            }

            if (coreField is null)
                continue;

            ClrObject vtsObj = heap.GetObject(address);
            if (!vtsObj.IsValid || vtsObj.Type is null)
                continue;

            ClrValueType core = vtsObj.ReadValueTypeField(coreField);
            if (!core.IsValid || core.Type is null)
                continue;

            var completedField = TryGetCachedField(core.Type, core.Type.MethodTable, "_completed", "m_completed");
            if (completedField is null)
                continue;

            bool completed = core.ReadField<bool>(completedField);
            if (completed)
                continue;

            pendingVts++;

            int generation = SegmentKindMapper.ResolveGeneration(heap, address);
            if (generation is 2 or 3)
                pendingVtsGen2++;

            pendingSnapshots.Add(new PendingValueTaskSourceSnapshot(
                Address: address,
                TypeName: vtsObj.Type.Name ?? $"MT:0x{mt:X}",
                Size: vtsObj.Size,
                Generation: generation));
        }

        // Same bounded-population rationale as AnalyzeTaskCompletionSources: a full sort of the
        // pending subset is cheap here. Strongest leak signal first: old-generation residency,
        // then size within that tier.
        pendingSnapshots.Sort((a, b) =>
        {
            int genCompare = b.Generation.CompareTo(a.Generation);
            return genCompare != 0 ? genCompare : b.Size.CompareTo(a.Size);
        });

        IReadOnlyList<PendingValueTaskSourceSnapshot> topPending = pendingSnapshots.Count > options.TopPendingVtsToShow
            ? pendingSnapshots.GetRange(0, options.TopPendingVtsToShow)
            : pendingSnapshots;

        return new VtsAnalysisResult(vtsEntries.Count, pendingVts, pendingVtsGen2, vtsScanLimited, topPending);
    }

    private readonly record struct TcsAnalysisResult(
        int TotalTcs,
        int UnresolvedTcs,
        int UnresolvedTcsGen2Count,
        bool TcsScanLimited,
        IReadOnlyList<UnresolvedTcsSnapshot> TopUnresolved);

    /// <summary>
    /// Classifies each TaskCompletionSource candidate by reading its inner Task (field
    /// <c>_task</c>/<c>m_task</c>, fallback for the .NET Core 3 field-rename convention) and
    /// checking whether that inner task is terminal. A non-terminal inner task means nobody has
    /// called SetResult/SetException/SetCanceled — either a normal in-flight promise, or (if the
    /// TCS has survived into Gen2/LOH) a leaked one. See <see cref="UnresolvedTcsSnapshot"/>.
    /// </summary>
    private TcsAnalysisResult AnalyzeTaskCompletionSources(
        ClrHeap heap,
        List<(ulong Address, ulong Mt)> tcsEntries,
        AsyncTaskAnalysisOptions options,
        CancellationToken ct)
    {
        if (tcsEntries.Count == 0)
            return new TcsAnalysisResult(0, 0, 0, false, []);

        bool tcsScanLimited = tcsEntries.Count >= options.MaxTcsToScan;
        int unresolvedTcs = 0;
        int unresolvedTcsGen2 = 0;
        var unresolvedSnapshots = new List<UnresolvedTcsSnapshot>(capacity: 32);

        for (int i = 0; i < tcsEntries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (address, mt) = tcsEntries[i];

            ClrObject tcsObj = heap.GetObject(address);
            if (!tcsObj.IsValid || tcsObj.Type is null)
                continue;

            var innerTaskField = TryGetCachedField(tcsObj.Type, mt, "m_task", "_task");
            if (innerTaskField is null)
                continue;

            ClrObject innerTask = innerTaskField.ReadObject(tcsObj, interior: false);
            if (!innerTask.IsValid || innerTask.Type is null)
                continue;

            var stateField = TryGetCachedField(innerTask.Type, innerTask.Type.MethodTable, "m_stateFlags", "_stateFlags");
            if (stateField is null)
                continue;

            int stateFlags = stateField.Read<int>(innerTask, interior: false);
            bool isTerminal = (stateFlags & (MaskCompleted | MaskFaulted | MaskCanceled)) != 0;
            if (isTerminal)
                continue;

            unresolvedTcs++;

            int generation = SegmentKindMapper.ResolveGeneration(heap, address);
            if (generation is 2 or 3)
                unresolvedTcsGen2++;

            unresolvedSnapshots.Add(new UnresolvedTcsSnapshot(
                Address: address,
                TypeName: tcsObj.Type.Name ?? $"MT:0x{mt:X}",
                Size: tcsObj.Size,
                Generation: generation));
        }

        // Bounded population (TCS instance counts are a bounded resource, capped further by
        // MaxTcsToScan) — a full sort of the unresolved subset is cheap, no need for the
        // maintained-threshold partial-sort pattern used elsewhere for large populations.
        // Strongest leak signal first: old-generation residency, then size within that tier.
        unresolvedSnapshots.Sort((a, b) =>
        {
            int genCompare = b.Generation.CompareTo(a.Generation);
            return genCompare != 0 ? genCompare : b.Size.CompareTo(a.Size);
        });

        IReadOnlyList<UnresolvedTcsSnapshot> topUnresolved = unresolvedSnapshots.Count > options.TopUnresolvedTcsToShow
            ? unresolvedSnapshots.GetRange(0, options.TopUnresolvedTcsToShow)
            : unresolvedSnapshots;

        return new TcsAnalysisResult(tcsEntries.Count, unresolvedTcs, unresolvedTcsGen2, tcsScanLimited, topUnresolved);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves a field by trying multiple names with fallback (e.g., "m_fieldName" then "_fieldName").
    /// Caches results by (MethodTable, fieldName) to avoid redundant ClrMD lookups.
    /// </summary>
    private ClrInstanceField? TryGetCachedField(ClrType type, ulong methodTable, params string[] fieldNames)
    {
        if (type is null || fieldNames.Length == 0)
            return null;

        _fieldCacheByMt ??= new Dictionary<(ulong, string), ClrInstanceField?>(capacity: 32);

        foreach (string fieldName in fieldNames)
        {
            var cacheKey = (methodTable, fieldName);
            if (_fieldCacheByMt.TryGetValue(cacheKey, out var cached))
                return cached;

            var field = type.GetFieldByName(fieldName);
            _fieldCacheByMt[cacheKey] = field;

            if (field != null)
                return field;
        }

        return null;
    }

    private static bool IsMultiContinuationList(ClrType? type)
    {
        string? name = type?.Name;
        return name != null && name.StartsWith(MultiContinuationListTypePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the elements of a List&lt;object&gt; multi-continuation node into <paramref name="buffer"/>
    /// via its backing <c>_items</c> array and <c>_size</c> fields. Returns false if the field
    /// layout couldn't be resolved (e.g. unexpected list-like type).
    /// </summary>
    private bool TryReadListElements(ClrObject listObj, List<ClrObject> buffer, int maxElements)
    {
        buffer.Clear();

        if (listObj.Type is null)
            return false;

        ulong mt = listObj.Type.MethodTable;
        var itemsField = TryGetCachedField(listObj.Type, mt, "_items");
        var sizeField = TryGetCachedField(listObj.Type, mt, "_size");
        if (itemsField is null || sizeField is null)
            return false;

        ClrObject itemsObj = itemsField.ReadObject(listObj, interior: false);
        if (!itemsObj.IsValid || !itemsObj.IsArray)
            return false;

        int size = sizeField.Read<int>(listObj, interior: false);
        if (size <= 0)
            return true;

        ClrArray arr = itemsObj.AsArray();
        int count = Math.Min(Math.Min(size, arr.Length), maxElements);

        for (int i = 0; i < count; i++)
        {
            ClrObject elem = arr.GetObjectValue(i);
            if (elem.IsValid && elem.Address != 0)
                buffer.Add(elem);
        }

        return true;
    }

    /// <summary>
    /// Returns the reusable element buffer for a given recursion depth level. A single shared
    /// buffer isn't safe across recursive <see cref="ExploreContinuation"/> calls because a
    /// nested List&lt;object&gt; would clear the buffer an outer frame is still iterating —
    /// pooling by depth (bounded by MaxContinuationDepth) avoids that while staying
    /// allocation-free after the pool warms up.
    /// </summary>
    private List<ClrObject> GetContinuationListBuffer(int depthLevel)
    {
        _continuationListBufferPool ??= new List<List<ClrObject>>(capacity: 16);
        while (_continuationListBufferPool.Count <= depthLevel)
            _continuationListBufferPool.Add(new List<ClrObject>(capacity: 32));
        return _continuationListBufferPool[depthLevel];
    }

    /// <summary>
    /// True branching exploration of a task's continuation graph. Returns the maximum depth
    /// (in genuine continuation hops — List&lt;object&gt; wrapper nodes don't count) reachable
    /// from <paramref name="node"/>. For every List&lt;object&gt; multi-continuation node
    /// encountered anywhere in the reachable subgraph, all elements are enumerated for fan-out
    /// stats and recursed into, so nested fan-out (a branch whose own continuation is itself
    /// another List&lt;object&gt;) is fully discovered rather than only the first branch walked.
    ///
    /// Caveats:
    /// - <paramref name="visited"/> is shared across the whole exploration to prevent
    ///   re-expanding cycles; if two branches converge on the same downstream node (a
    ///   "diamond"), whichever branch reaches it first claims it and the other branch is
    ///   truncated there — an accepted approximation, not an exact max-depth guarantee.
    /// - <paramref name="nodeBudget"/> bounds total nodes visited per task, independent of
    ///   depth, to keep worst-case cost bounded for wide-and-deep fan-out trees.
    /// - The winning branch at each node is recorded into <paramref name="bestHopSelections"/>
    ///   (keyed by node address) so the caller can replay it afterward to build a display
    ///   chain without allocating per-branch lists during exploration.
    /// - <paramref name="rootCycleDetected"/> is set to true if a cycle leads back to the
    ///   root task (indicating a hard deadlock).
    /// </summary>
    private int ExploreContinuation(
        ClrObject node,
        HashSet<ulong> visited,
        ulong rootTaskAddress,
        int remainingDepth,
        ref int nodeBudget,
        int depthLevel,
        Dictionary<string, int> continuationCount,
        Dictionary<string, int> fanoutTypeCount,
        ref int totalContinuations,
        ref int multiContinuationNodes,
        ref int maxFanOut,
        ref bool rootCycleDetected,
        Dictionary<ulong, ClrObject> bestHopSelections)
    {
        if (remainingDepth <= 0 || !node.IsValid || node.Address == 0 || nodeBudget <= 0)
            return 0;

        if (!visited.Add(node.Address))
        {
            if (node.Address == rootTaskAddress)
                rootCycleDetected = true;
            return 0;
        }

        nodeBudget--;

        if (IsMultiContinuationList(node.Type))
        {
            List<ClrObject> buffer = GetContinuationListBuffer(depthLevel);
            if (!TryReadListElements(node, buffer, MaxFanOutElementsToEnumerate))
                return 0;

            multiContinuationNodes++;
            if (buffer.Count > maxFanOut)
                maxFanOut = buffer.Count;

            int bestDepth = 0;
            ClrObject bestElem = default;
            foreach (ClrObject elem in buffer)
            {
                if (elem.Type != null)
                    IncrementCount(fanoutTypeCount, elem.Type.Name ?? string.Empty);

                int elemDepth = ExploreContinuation(
                    elem, visited, rootTaskAddress, remainingDepth, ref nodeBudget, depthLevel + 1,
                    continuationCount, fanoutTypeCount, ref totalContinuations, ref multiContinuationNodes, ref maxFanOut,
                    ref rootCycleDetected, bestHopSelections);

                if (elemDepth > bestDepth)
                {
                    bestDepth = elemDepth;
                    bestElem = elem;
                }
            }

            if (bestElem.IsValid && bestElem.Address != 0)
                bestHopSelections[node.Address] = bestElem;

            return bestDepth;
        }

        totalContinuations++;
        if (node.Type != null)
            IncrementCount(continuationCount, node.Type.Name ?? string.Empty);

        var nextField = node.Type != null
            ? TryGetCachedField(node.Type, node.Type.MethodTable, "m_continuationObject", "_continuationObject")
            : null;
        if (nextField == null)
            return 1;

        ClrObject next = nextField.ReadObject(node, interior: false);
        if (!next.IsValid || next.Address == 0)
            return 1;

        bestHopSelections[node.Address] = next;

        int childDepth = ExploreContinuation(
            next, visited, rootTaskAddress, remainingDepth - 1, ref nodeBudget, depthLevel + 1,
            continuationCount, fanoutTypeCount, ref totalContinuations, ref multiContinuationNodes, ref maxFanOut,
            ref rootCycleDetected, bestHopSelections);

        return 1 + childDepth;
    }

    private static string ResolveTypeName(ClrHeap heap, ulong mt,
        Dictionary<ulong, string> cache)
    {
        if (cache.TryGetValue(mt, out string? name))
            return name;

        // OPT (docs/cache/cache-architecture.md Phase 5): mt is already known —
        // resolve via the metadata cache instead of materializing a ClrObject.
        string resolved = heap.GetTypeByMethodTable(mt)?.Name
            ?? "System.Threading.Tasks.Task";

        cache[mt] = resolved;
        return resolved;
    }

    private static string? ExtractResultType(string typeName)
    {
        // "System.Threading.Tasks.Task`1[[System.String, ...]]" → "System.String"
        int start = typeName.IndexOf("[[", StringComparison.Ordinal);
        if (start < 0) return null;
        start += 2;
        int end = typeName.IndexOf(',', start);
        if (end < 0) end = typeName.IndexOf("]]", start, StringComparison.Ordinal);
        if (end <= start) return null;
        return typeName[start..end];
    }

    private (string? ExceptionType, string? ExceptionMessage) ExtractFaultedTaskException(ClrObject taskObj)
    {
        if (!taskObj.IsValid || taskObj.Type is null)
            return (null, null);

        var visited = new HashSet<ulong>(capacity: 16);

        foreach (string fieldName in new[] { "m_contingentProperties", "_contingentProperties" })
        {
            ClrObject contingent = ReadObjectField(taskObj, fieldName);
            if (!contingent.IsValid)
                continue;

            if (TryFindExceptionLikeObject(contingent, visited, 0, out ClrObject exceptionObj) && TryReadExceptionSummary(exceptionObj, out string? exceptionType, out string? message))
                return (exceptionType, message);
        }

        if (TryFindExceptionLikeObject(taskObj, visited, 0, out ClrObject fallbackExceptionObj) && TryReadExceptionSummary(fallbackExceptionObj, out string? fallbackType, out string? fallbackMessage))
            return (fallbackType, fallbackMessage);

        return (null, null);
    }

    private ClrObject ReadObjectField(ClrObject source, string fieldName)
    {
        if (source.Type is null)
            return default;

        var field = TryGetCachedField(source.Type, source.Type.MethodTable, fieldName);
        return field is null ? default : field.ReadObject(source, interior: false);
    }

    private bool TryFindExceptionLikeObject(ClrObject source, HashSet<ulong> visited, int depth, out ClrObject exceptionObj)
    {
        return ObjectGraphTraversal.TryFindByPredicate(
            source,
            visited,
            depth,
            maxDepth: 4,
            prioritizedFieldNames: ExceptionRelatedFields,
            isMatch: candidate =>
            {
                string? typeName = candidate.Type?.Name;
                return !string.IsNullOrWhiteSpace(typeName)
                    && typeName.Contains("Exception", StringComparison.Ordinal)
                    && !typeName.Contains("ExceptionDispatchInfo", StringComparison.Ordinal)
                    && TryReadExceptionSummary(candidate, out _, out _);
            },
            readObjectField: (obj, fieldName) => ReadObjectField(obj, fieldName),
            out exceptionObj);
    }

    private bool TryReadExceptionSummary(ClrObject exceptionObj, out string? exceptionType, out string? message)
    {
        exceptionType = exceptionObj.Type?.Name;
        message = null;

        if (!exceptionObj.IsValid || exceptionObj.Type is null)
            return false;

        if (exceptionType is not null && exceptionType.Contains("ExceptionDispatchInfo", StringComparison.Ordinal))
        {
            foreach (string fieldName in new[] { "_exception", "m_exception" })
            {
                ClrObject inner = ReadObjectField(exceptionObj, fieldName);
                if (TryReadExceptionSummary(inner, out exceptionType, out message))
                    return true;
            }

            return false;
        }

        var messageField = exceptionObj.Type.GetFieldByName("_message");
        if (messageField != null)
        {
            ClrObject messageObj = messageField.ReadObject(exceptionObj, interior: false);
            if (messageObj.IsValid)
                message = messageObj.AsString();
        }

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
        Dictionary<string, Dictionary<string, int>> faultedExceptions,
        int topTypesToShow)
    {
        var topFaulted = BuildTopN(faultedCounts, topTypesToShow);
        var profiles = new List<FaultedTaskTypeProfile>(capacity: topFaulted.Count);

        foreach (var entry in topFaulted)
        {
            var exceptionHistogram = faultedExceptions.TryGetValue(entry.Name, out var exceptionCounts)
                ? BuildTopN(exceptionCounts, topTypesToShow)
                : (IReadOnlyList<NameCountEntry>)[];

            profiles.Add(new FaultedTaskTypeProfile(entry.Name, entry.Count, exceptionHistogram));
        }

        return profiles;
    }

    private static IReadOnlyList<NameCountEntry> BuildTopN(Dictionary<string, int> counts, int topTypesToShow)
    {
        if (counts.Count == 0) return [];

        var result = new List<NameCountEntry>(capacity: Math.Min(counts.Count, topTypesToShow));
        int threshold = 0;

        // Find top-N without LINQ — sort only if we must
        if (counts.Count <= topTypesToShow)
        {
            foreach (var kvp in counts)
                result.Add(new(kvp.Key, kvp.Value));
            result.Sort((a, b) => b.Count.CompareTo(a.Count));
            return result;
        }

        // Partial sort: track min in top-N bucket
        foreach (var kvp in counts)
        {
            if (result.Count < topTypesToShow)
            {
                result.Add(new(kvp.Key, kvp.Value));
                if (kvp.Value < threshold || result.Count == 1)
                    threshold = kvp.Value;

                // After fill phase completes, sync threshold to the actual minimum
                // in the filled result before proceeding to replacements.
                if (result.Count == topTypesToShow)
                {
                    threshold = int.MaxValue;
                    for (int i = 0; i < result.Count; i++)
                        if (result[i].Count < threshold) threshold = result[i].Count;
                }
            }
            else if (kvp.Value > threshold)
            {
                // Replace the entry with the lowest count
                int minIdx = 0;
                for (int i = 1; i < result.Count; i++)
                    if (result[i].Count < result[minIdx].Count) minIdx = i;
                result[minIdx] = new(kvp.Key, kvp.Value);
                threshold = int.MaxValue;
                for (int i = 0; i < result.Count; i++)
                    if (result[i].Count < threshold) threshold = result[i].Count;
            }
        }
        result.Sort((a, b) => b.Count.CompareTo(a.Count));
        return result;
    }

    private static IReadOnlyList<NameSizeCountEntry> BuildTopNByBytes(
        Dictionary<string, (long TotalBytes, int Count)> byteCounts, int topTypesToShow)
    {
        if (byteCounts.Count == 0) return [];

        var result = new List<NameSizeCountEntry>(capacity: Math.Min(byteCounts.Count, topTypesToShow));
        long threshold = 0;

        // Find top-N without LINQ — sort only if we must
        if (byteCounts.Count <= topTypesToShow)
        {
            foreach (var kvp in byteCounts)
                result.Add(new(kvp.Key, kvp.Value.TotalBytes, kvp.Value.Count));
            result.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
            return result;
        }

        // Partial sort: track min in top-N bucket
        foreach (var kvp in byteCounts)
        {
            if (result.Count < topTypesToShow)
            {
                result.Add(new(kvp.Key, kvp.Value.TotalBytes, kvp.Value.Count));
                if (kvp.Value.TotalBytes < threshold || result.Count == 1)
                    threshold = kvp.Value.TotalBytes;

                // After fill phase completes, sync threshold to the actual minimum
                // in the filled result before proceeding to replacements.
                if (result.Count == topTypesToShow)
                {
                    threshold = long.MaxValue;
                    for (int i = 0; i < result.Count; i++)
                        if (result[i].TotalBytes < threshold) threshold = result[i].TotalBytes;
                }
            }
            else if (kvp.Value.TotalBytes > threshold)
            {
                // Replace the entry with the lowest total bytes
                int minIdx = 0;
                for (int i = 1; i < result.Count; i++)
                    if (result[i].TotalBytes < result[minIdx].TotalBytes) minIdx = i;
                result[minIdx] = new(kvp.Key, kvp.Value.TotalBytes, kvp.Value.Count);
                threshold = long.MaxValue;
                for (int i = 0; i < result.Count; i++)
                    if (result[i].TotalBytes < threshold) threshold = result[i].TotalBytes;
            }
        }
        result.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        return result;
    }

    public void Dispose() { }
}
