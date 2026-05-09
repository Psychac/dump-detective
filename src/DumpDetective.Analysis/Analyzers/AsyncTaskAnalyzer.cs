using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Analyzers;

internal sealed class AsyncTaskAnalyzer : IAnalyzer
{
    // Task index record layout (20 bytes, little-endian):
    //   Address (8) | MT (8) | StateFlags (4)
    private const int TaskIndexMagic = 0x58494B54; // "TKIX"
    private const int TaskIndexVersion = 1;
    private const int RecordSize = 20;

    // m_stateFlags bit masks (matches HangAnalyzer)
    private const int MaskCompleted = 0x1000000;
    private const int MaskFaulted = 0x200000;
    private const int MaskCanceled = 0x400000;
    private const int MaskRunning = 0x10000; // TASK_STATE_DELEGATE_INVOKED

    // Sentinel continuation type — no-op callback; indicates orphan
    private const string NoOpContinuationType = "System.Threading.Tasks.Task+<>c";

    public string Name => "Async Task Analysis";
    public string Category => "Async";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AsyncTaskAnalysisOptions options = context.GetOption<AsyncTaskAnalysisOptions>();
        return ValueTask.FromResult(Analyze(context.Heap, context.Cache, context.Progress, options, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(
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
                MaxContinuationDepth: 0,
                AvgContinuationDepth: 0.0,
                TaskScanLimited: false,
                TopPendingTaskTypes: [],
                TopFaultedTaskTypes: [],
                TopContinuationTypes: [],
                TopOrphanedTasks: []);
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

        int totalDepthSum = 0;
        int maxDepth = 0;
        int depthSampleCount = 0;

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
                    var stateField = obj.Type.GetFieldByName("m_stateFlags");
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
                var continuationField = taskObj.Type.GetFieldByName("m_continuationObject");
                if (continuationField != null)
                {
                    ClrObject continuationObj = continuationField.ReadObject(taskObj, interior: false);

                    bool isOrphan = !continuationObj.IsValid
                        || continuationObj.Address == 0
                        || string.Equals(continuationObj.Type?.Name, NoOpContinuationType, StringComparison.Ordinal);

                    if (isOrphan && !isCompleted && !isCanceled)
                    {
                        orphaned++;
                        if (orphanedSnapshots.Count < options.TopOrphanedToShow)
                        {
                            string? resultType = ExtractResultType(typeName);
                            ulong size = taskObj.Size;
                            orphanedSnapshots.Add(new OrphanedTaskSnapshot(address, typeName, resultType, size));
                        }
                    }

                    // BFS chain depth
                    if (continuationObj.IsValid && continuationObj.Address != 0)
                    {
                        int depth = 1;
                        var visited = new HashSet<ulong>(capacity: 8) { address };
                        ClrObject current = continuationObj;

                        while (depth < options.MaxContinuationDepth && current.IsValid && current.Address != 0
                                 && visited.Add(current.Address))
                        {
                            // Track continuation type for top-N
                            if (current.Type != null)
                                IncrementCount(continuationCount, current.Type.Name ?? string.Empty);

                            var nextField = current.Type?.GetFieldByName("m_continuationObject");
                            if (nextField == null) break;

                            ClrObject next = nextField.ReadObject(current, interior: false);
                            if (!next.IsValid || next.Address == 0) break;

                            current = next;
                            depth++;
                        }

                        totalDepthSum += depth;
                        if (depth > maxDepth) maxDepth = depth;
                        depthSampleCount++;
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
            MaxContinuationDepth: maxDepth,
            AvgContinuationDepth: avgDepth,
            TaskScanLimited: taskScanLimited,
            TopPendingTaskTypes: BuildTopN(pendingTypeCount, options.TopTypesToShow),
            TopFaultedTaskTypes: BuildTopN(faultedTypeCount, options.TopTypesToShow),
            TopContinuationTypes: BuildTopN(continuationCount, options.TopTypesToShow),
            TopOrphanedTasks: orphanedSnapshots);
    }

    // ── TaskIndex.bin reader ──────────────────────────────────────────────────

    /// <summary>
    /// Loads task entries from <c>TaskIndex.bin</c> if available, otherwise falls back to
    /// a filtered heap scan using <see cref="TypeAggregateFlags.IsTaskType"/>.
    /// Returns at most <see cref="MaxTasksToScan"/> entries (scan-limit respected).
    /// </summary>
    private static List<(ulong Address, ulong Mt, int StateFlags)> LoadTaskEntries(
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

            string indexDir = Path.GetDirectoryName(heapIndex.IndexPath) ?? string.Empty;
            string taskIndexPath = Path.Combine(indexDir, DumpIndexPaths.TaskIndexFile);

            if (File.Exists(taskIndexPath))
            {
                var entries = ReadTaskIndexFile(taskIndexPath, progress, options.MaxTasksToScan, ct);
                if (entries != null)
                    return entries;
            }

            // Fall back to typed scan via TypeAggregates flags
            return ScanHeapIndexForTasks(heap, heapCache, heapIndex, progress, options.MaxTasksToScan, ct);
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

    private static List<(ulong, ulong, int)>? ReadTaskIndexFile(
        string path,
        IProgress<AnalyzerProgressReport>? progress,
        int maxTasksToScan,
        CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 256 * 1024, FileOptions.SequentialScan);

            if (!IndexHeader.TryRead(stream, out IndexHeader header))
                return null;

            if (header.Magic != TaskIndexMagic || header.Version != TaskIndexVersion)
                return null;

            long recordCount = header.RecordCount;
            int cap = (int)Math.Min(recordCount, maxTasksToScan);
            var result = new List<(ulong, ulong, int)>(capacity: cap);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(RecordSize * 4096);
            try
            {
                int read;
                int recordsRead = 0;

                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int offset = 0;
                    while (offset + RecordSize <= read)
                    {
                        ulong address = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset));
                        ulong mt = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset + 8));
                        int stateFlags = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 16));

                        result.Add((address, mt, stateFlags));
                        offset += RecordSize;
                        recordsRead++;

                        if (recordsRead >= maxTasksToScan)
                            goto done;
                    }
                }
            done:;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            progress?.Report(new(result.Count, "task index loaded",
                $"{result.Count:N0} task records read"));
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<(ulong, ulong, int)> ScanHeapIndexForTasks(
        ClrHeap heap,
        HeapAnalysisCache heapCache,
        HeapIndexBuildResult heapIndex,
        IProgress<AnalyzerProgressReport>? progress,
        int maxTasksToScan,
        CancellationToken ct)
    {
        // Build IsTaskType MT set from TypeAggregates flags (O(1) per lookup)
        var taskMts = new HashSet<ulong>(capacity: 32);
        foreach (var kvp in heapIndex.TypeAggregates)
        {
            if ((kvp.Value.Flags & TypeAggregateFlags.IsTaskType) != 0)
                taskMts.Add(kvp.Key);
        }

        var result = new List<(ulong, ulong, int)>(capacity: 1024);
        var scanCounter = new ObjectScanCounter("scanning task objects (indexed)", progress);

        foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
        {
            ct.ThrowIfCancellationRequested();
            scanCounter.Tick();

            if (!taskMts.Contains(entry.MethodTable))
                continue;

            result.Add((entry.Address, entry.MethodTable, 0)); // StateFlags resolved in Phase 2
            if (result.Count >= maxTasksToScan)
                break;
        }

        scanCounter.Complete();
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
            if (typeName is null || !typeName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
                continue;

            result.Add((obj.Address, obj.Type.MethodTable, 0));
            if (result.Count >= maxTasksToScan)
                break;
        }

        scanCounter.Complete();
        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
