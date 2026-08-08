using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Traversal;
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

    // Field cache by MethodTable to avoid repeated ClrMD lookups per type
    // Maps (MethodTable, fieldName) to ClrInstanceField? (null if field not found on that type)
    private Dictionary<(ulong MethodTable, string FieldName), ClrInstanceField?>? _fieldCacheByMt;


    // m_stateFlags bit masks (matches HangAnalyzer)
    private const int MaskCompleted = 0x1000000;
    private const int MaskFaulted = 0x200000;
    private const int MaskCanceled = 0x400000;
    private const int MaskRunning = 0x10000; // TASK_STATE_DELEGATE_INVOKED

    // Sentinel continuation type — no-op callback; indicates orphan
    private const string NoOpContinuationType = "System.Threading.Tasks.Task+<>c";
    private static readonly string[] TaskNamespacePrefixes = ["System.Threading.Tasks.Task"];
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
        _participantEntries = new List<(ulong, ulong, int)>(capacity: 1024);
        _taskMts = null;
        _fieldCacheByMt = new Dictionary<(ulong, string), ClrInstanceField?>(capacity: 32);

        if (context.Cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
        {
            _taskMts = new HashSet<ulong>(capacity: 32);
            foreach (var kvp in heapIndex.TypeAggregates)
            {
                if ((kvp.Value.Flags & TypeAggregateFlags.IsTaskType) != 0)
                    _taskMts.Add(kvp.Key);
            }
        }
    }

    public void OnHeapEntry(in HeapEntry entry)
    {
        if (_taskMts is null || _participantEntries!.Count >= _participantMaxTasksToScan)
            return;

        if (_taskMts.Contains(entry.MethodTable))
            _participantEntries.Add((entry.Address, entry.MethodTable, 0)); // StateFlags resolved in Phase 2
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
        }

        _participantEntries = _participantEntries!
            .OrderBy(e => e.Address)
            .Take(_participantMaxTasksToScan)
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
                TopDeepestChains: []);
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
        var faultedTypeCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var continuationCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var orphanedSnapshots = new List<OrphanedTaskSnapshot>(capacity: 32);
        var deepestChains = new List<ContinuationChainSnapshot>(capacity: 5);

        int totalDepthSum = 0;
        int maxDepth = 0;
        int depthSampleCount = 0;
        int totalContinuations = 0;

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

            // Re-read stateFlags from ClrMD if written as 0 during Phase 1
            if (stateFlags == 0)
            {
                ClrObject obj = heap.GetObject(address);
                if (obj.IsValid && obj.Type != null)
                {
                    var stateField = TryGetCachedField(obj.Type, mt, "m_stateFlags", "_stateFlags");
                    if (stateField != null)
                        stateFlags = stateField.Read<int>(obj, interior: false);
                }
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

            // Resolve type name
            string typeName = ResolveTypeName(heap, address, mt, typeNameByMt);

            // Track top type counts
            if (!isCompleted && !isCanceled)
            {
                if (isFaulted)
                    IncrementCount(faultedTypeCount, typeName);
                else
                    IncrementCount(pendingTypeCount, typeName);
            }

            // ── Orphan detection + continuation chain BFS ─────────────────────
            // taskEntries is already capped at MaxTasksToScan by all load paths;
            // BFS runs for every task with a valid continuation.
            ClrObject taskObj = heap.GetObject(address);
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

                    // BFS chain depth
                    if (continuationObj.IsValid && continuationObj.Address != 0)
                    {
                        int depth = 1;
                        var visited = new HashSet<ulong>(capacity: 8) { address };
                        ClrObject current = continuationObj;
                            if (current.Type != null)
                                chainTypes.Add(current.Type.Name ?? string.Empty);

                        while (depth < options.MaxContinuationDepth && current.IsValid && current.Address != 0
                                 && visited.Add(current.Address))
                        {
                            // Track continuation type for top-N
                            if (current.Type != null)
                            {
                                totalContinuations++;
                                IncrementCount(continuationCount, current.Type.Name ?? string.Empty);
                            }

                            var nextField = current.Type != null
                                ? TryGetCachedField(current.Type, current.Type.MethodTable, "m_continuationObject", "_continuationObject")
                                : null;
                            if (nextField == null) break;

                            ClrObject next = nextField.ReadObject(current, interior: false);
                            if (!next.IsValid || next.Address == 0) break;

                            current = next;
                            depth++;
                                if (current.Type != null)
                                    chainTypes.Add(current.Type.Name ?? string.Empty);
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
            TopDeepestChains: deepestChains.OrderByDescending(chain => chain.Depth).ToArray());
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

        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            scanCounter.Tick();

            if (!obj.IsValid || obj.Type is null)
                continue;

            string? typeName = obj.Type.Name;
            if (typeName is null || !TypeNamePatternMatcher.HasAnyPrefix(typeName, TaskNamespacePrefixes))
                continue;

            result.Add((obj.Address, obj.Type.MethodTable, 0));
            if (result.Count >= maxTasksToScan)
                break;
        }

        scanCounter.Complete();
        return result;
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

    private static string ResolveTypeName(ClrHeap heap, ulong address, ulong mt,
        Dictionary<ulong, string> cache)
    {
        if (cache.TryGetValue(mt, out string? name))
            return name;

        ClrObject obj = heap.GetObject(address);
        string resolved = (obj.IsValid ? obj.Type?.Name : null)
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

    public void Dispose() { }
}
