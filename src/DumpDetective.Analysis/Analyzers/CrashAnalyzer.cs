using System.Collections.Concurrent;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
public class CrashAnalyzer : IAnalyzer
    {
        private const int MaxExceptionsPerType = 10;
        private const int TopExceptionTypesCount = 10;
        private const int MaxDetailedExceptionsPerType = 5;
        private const int MaxOriginalStackFramesToPrint = 20;
        private const int MaxCurrentThreadFramesToPrint = 5;
        private const int TopCrashThreadCandidates = 5;
        private const int TopDetailedExceptionInstances = 25;

        public string Name => "Crash Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Cache).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            return Analyze(runtime, heap, cache: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap, IHeapAnalysisCache? cache)
        {
            var exceptionInfo = AnalyzeExceptions(heap, runtime, cache);

            var domainResult = new CrashDomainResult(
                exceptionInfo.TotalExceptions,
                exceptionInfo.ActiveExceptions,
                new Dictionary<string, int>(exceptionInfo.ExceptionTypeCounts),
                new Dictionary<string, int>(exceptionInfo.ActiveExceptionTypeCounts),
                BuildCrashThreadSnapshots(exceptionInfo),
                BuildExceptionInstanceSnapshots(exceptionInfo));

            if (exceptionInfo.TotalExceptions == 0)
            {
                return domainResult;
            }

            return domainResult;
        }

        private static IReadOnlyList<CrashThreadCandidateSnapshot> BuildCrashThreadSnapshots(ExceptionAnalysis analysis)
        {
            return analysis.CrashThreadCandidates
                .Take(TopCrashThreadCandidates)
                .Select(c => new CrashThreadCandidateSnapshot(
                    c.ThreadId,
                    c.OSThreadId,
                    c.ActiveExceptionCount,
                    c.PrimaryExceptionType,
                    c.CurrentThreadStack
                        .Take(MaxCurrentThreadFramesToPrint)
                        .Select(f => f.Method?.Signature ?? f.FrameName ?? f.ToString() ?? StringConstants.UnknownType)
                        .ToList()))
                .ToList();
        }

        private static IReadOnlyList<ExceptionInstanceSnapshot> BuildExceptionInstanceSnapshots(ExceptionAnalysis analysis)
        {
            var instances = analysis.ExceptionsByType
                .SelectMany(kvp => kvp.Value.Select(v => new { Type = kvp.Key, Instance = v }))
                .OrderByDescending(x => x.Instance.ThreadId.HasValue)
                .ThenByDescending(x => x.Instance.OriginalStackTrace.Count)
                .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.Instance.Message))
                .Take(TopDetailedExceptionInstances)
                .Select(x => new ExceptionInstanceSnapshot(
                    x.Type,
                    x.Instance.Address,
                    string.IsNullOrWhiteSpace(x.Instance.Message) ? null : x.Instance.Message,
                    x.Instance.HResult == 0 ? null : x.Instance.HResult,
                    x.Instance.InnerExceptionType,
                    x.Instance.ThreadId.HasValue,
                    x.Instance.ThreadId,
                    x.Instance.OSThreadId,
                    x.Instance.CurrentThreadStack.Count == 0
                        ? null
                        : x.Instance.CurrentThreadStack
                            .Take(MaxCurrentThreadFramesToPrint)
                            .Select(f => f.Method?.Signature ?? f.FrameName ?? f.ToString() ?? StringConstants.UnknownType)
                            .ToList(),
                    x.Instance.OriginalStackTrace.Count == 0
                        ? null
                        : x.Instance.OriginalStackTrace.Take(MaxOriginalStackFramesToPrint).ToList()))
                .ToList();

            return instances;
        }

        private static InsightFinding CreateFinding(ExceptionAnalysis analysis)
        {
            FindingSeverity severity = analysis.ActiveExceptions > 0
                ? FindingSeverity.Critical
                : analysis.TotalExceptions > 0
                    ? FindingSeverity.Warning
                    : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(CrashAnalyzer),
                Category: "Stability",
                Severity: severity,
                Title: "Exception pressure in crash dump",
                Evidence: $"Total exceptions: {analysis.TotalExceptions:N0}; active thread exceptions: {analysis.ActiveExceptions:N0}; unique types: {analysis.ExceptionTypeCounts.Count:N0}.",
                Recommendation: analysis.ActiveExceptions > 0
                    ? "Prioritize active exception threads and top exception types for root-cause isolation."
                    : "Review top exception families for recurring fault paths.",
                Tags: ["crash", "exceptions", "threads"],
                MetricValue: analysis.ActiveExceptions,
                MetricUnit: "active-exceptions");
        }

        private ExceptionAnalysis AnalyzeExceptions(ClrHeap heap, ClrRuntime runtime, IHeapAnalysisCache? cache)
        {
            var activeExceptions = BuildActiveExceptionLookup(runtime);

            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out var heapIdx))
            {
                // In-memory index: parallel over the flat entry array
                if (heapIdx.StorageKind == HeapIndexStorageKind.Memory && heapIdx.InMemoryEntries is { } entries)
                    return RunParallelExceptionScan(heap, inMemoryEntries: entries, heapIdx: heapIdx, activeExceptions: activeExceptions);

                // Disk-backed index: sequential (I/O bound)
                return RunSequentialExceptionScan(heap, heapCache, cache, activeExceptions);
            }

            // No cache: parallel over GC segments
            return RunParallelExceptionScan(heap, inMemoryEntries: null, heapIdx: null, activeExceptions: activeExceptions);
        }

        // Unified parallel exception scanner — drives either a flat in-memory HeapEntry[]
        // or a per-segment ClrObject walk using the same concurrent accumulation logic.
        private ExceptionAnalysis RunParallelExceptionScan(
            ClrHeap heap,
            HeapEntry[]? inMemoryEntries,
            HeapIndexBuildResult? heapIdx,
            Dictionary<ulong, ActiveExceptionContext> activeExceptions)
        {
            var exceptionMethodTables = new ConcurrentDictionary<ulong, bool>();
            var methodTableNameCache = new ConcurrentDictionary<ulong, string>();
            var exceptionTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var activeExceptionTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionInstances = new ConcurrentBag<(string TypeName, ExceptionInstance Instance, bool IsActive)>();
            var crashThreadCandidates = new ConcurrentDictionary<uint, CrashThreadCandidate>();
            var candidateLock = new object();
            int totalExceptions = 0, activeExceptionsCount = 0;

            void ProcessEntry(ulong exceptionAddress, ulong mt)
            {
                if (exceptionAddress == 0)
                    return;

                // Filter: is this an exception type?
                bool isException = exceptionMethodTables.GetOrAdd(mt, _ =>
                {
                    ClrObject o = heap.GetObject(exceptionAddress);
                    string? n = o.IsValid ? o.Type?.Name : null;
                    return n?.Contains("Exception", StringComparison.Ordinal) == true;
                });

                if (!isException)
                    return;

                // Resolve type name
                string? typeName = methodTableNameCache.GetOrAdd(mt, _ =>
                {
                    if (heapIdx?.TypeAggregates.TryGetValue(mt, out var agg) == true && agg.SampleAddress != 0)
                    {
                        ClrObject sample = heap.GetObject(agg.SampleAddress);
                        if (sample.IsValid && sample.Type != null)
                            return sample.Type.Name ?? string.Empty;
                    }
                    return heap.GetObject(exceptionAddress).Type?.Name ?? string.Empty;
                });

                if (typeName?.Contains("Exception", StringComparison.Ordinal) != true)
                    return;

                Interlocked.Increment(ref totalExceptions);
                exceptionTypeCounts.AddOrUpdate(typeName, 1, (_, c) => c + 1);

                bool isActive = activeExceptions.TryGetValue(exceptionAddress, out var activeCtx);
                if (isActive)
                {
                    Interlocked.Increment(ref activeExceptionsCount);
                    activeExceptionTypeCounts.AddOrUpdate(typeName, 1, (_, c) => c + 1);

                    lock (candidateLock)
                    {
                        if (!crashThreadCandidates.TryGetValue(activeCtx.ThreadId, out var candidate))
                        {
                            candidate = new CrashThreadCandidate
                            {
                                ThreadId = activeCtx.ThreadId,
                                OSThreadId = activeCtx.OSThreadId,
                                CurrentThreadStack = activeCtx.CurrentThreadStack,
                                PrimaryExceptionType = typeName
                            };
                            crashThreadCandidates[activeCtx.ThreadId] = candidate;
                        }
                        candidate.ActiveExceptionCount++;
                    }
                }

                var exceptionInstance = ExtractExceptionInfo(heap, exceptionAddress, isActive ? activeCtx : null);
                exceptionInstances.Add((typeName, exceptionInstance, isActive));
            }

            if (inMemoryEntries != null)
            {
                Parallel.ForEach(inMemoryEntries, entry =>
                {
                    if (entry.Address == 0 || entry.MethodTable == 0)
                        return;
                    ProcessEntry(entry.Address, entry.MethodTable);
                });
            }
            else
            {
                Parallel.ForEach(heap.Segments, segment =>
                {
                    foreach (ClrObject obj in segment.EnumerateObjects())
                    {
                        if (!obj.IsValid || obj.Type is null)
                            continue;
                        ulong mt = obj.Type.MethodTable;
                        if (mt == 0)
                            continue;
                        ProcessEntry(obj.Address, mt);
                    }
                });
            }

            // Sequential post-processing: build per-type exception list with cap enforcement
            var exceptionsByType = new Dictionary<string, List<ExceptionInstance>>(StringComparer.Ordinal);
            foreach (var (typeName, instance, isActive) in exceptionInstances)
            {
                if (!exceptionsByType.TryGetValue(typeName, out var list))
                {
                    list = new List<ExceptionInstance>(capacity: MaxExceptionsPerType);
                    exceptionsByType[typeName] = list;
                }
                if (list.Count < MaxExceptionsPerType || isActive)
                    list.Add(instance);
            }

            var sortedExceptionsByType = new Dictionary<string, List<ExceptionInstance>>(
                exceptionsByType.Count, StringComparer.Ordinal);
            foreach (string tn in exceptionTypeCounts.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key))
            {
                if (exceptionsByType.TryGetValue(tn, out var list))
                    sortedExceptionsByType[tn] = list;
            }

            return new ExceptionAnalysis
            {
                TotalExceptions = totalExceptions,
                ActiveExceptions = activeExceptionsCount,
                ExceptionTypeCounts = new Dictionary<string, int>(exceptionTypeCounts, StringComparer.Ordinal),
                ActiveExceptionTypeCounts = new Dictionary<string, int>(activeExceptionTypeCounts, StringComparer.Ordinal),
                ExceptionsByType = sortedExceptionsByType,
                CrashThreadCandidates = crashThreadCandidates.Values
                    .OrderByDescending(c => c.ActiveExceptionCount)
                    .ToList()
            };
        }

        private ExceptionAnalysis RunSequentialExceptionScan(
            ClrHeap heap, HeapAnalysisCache heapCache, IHeapAnalysisCache? cache,
            Dictionary<ulong, ActiveExceptionContext> activeExceptions)
        {
            var analysis = new ExceptionAnalysis();
            var exceptionsByType = new Dictionary<string, List<ExceptionInstance>>();
            var exceptionTypeCounts = new Dictionary<string, int>();
            var activeExceptionTypeCounts = new Dictionary<string, int>();
            var exceptionMethodTables = new Dictionary<ulong, bool>(capacity: 64);
            var methodTableNameCache = new Dictionary<ulong, string>(capacity: 64);
            var crashThreadCandidates = new Dictionary<uint, CrashThreadCandidate>();
            var scanCounter = new ObjectScanCounter("Crash exception scan");

            foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
            {
                scanCounter.Tick();

                ulong exceptionAddress = entry.Address;
                if (exceptionAddress == 0)
                    continue;

                if (!IsExceptionEntry(heap, entry, exceptionMethodTables))
                    continue;

                string? typeName;
                ulong mt = entry.MethodTable;
                if (mt != 0 && methodTableNameCache.TryGetValue(mt, out var cachedName))
                {
                    typeName = cachedName;
                }
                else
                {
                    if (mt != 0 && cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out var build)
                        && build.TypeAggregates.TryGetValue(mt, out var agg) && agg.SampleAddress != 0)
                    {
                        ClrObject sample = heap.GetObject(agg.SampleAddress);
                        typeName = sample.IsValid && sample.Type != null ? sample.Type.Name : null;
                    }
                    else
                    {
                        typeName = heap.GetObject(exceptionAddress).Type?.Name;
                    }
                    if (mt != 0 && typeName != null)
                        methodTableNameCache[mt] = typeName;
                }

                if (typeName?.Contains("Exception", StringComparison.Ordinal) == true)
                {
                    analysis.TotalExceptions++;
                    exceptionTypeCounts.TryGetValue(typeName, out int typeCount);
                    exceptionTypeCounts[typeName] = typeCount + 1;

                    bool isActive = activeExceptions.TryGetValue(exceptionAddress, out var activeExceptionContext);
                    if (isActive)
                    {
                        analysis.ActiveExceptions++;
                        activeExceptionTypeCounts.TryGetValue(typeName, out int activeTypeCount);
                        activeExceptionTypeCounts[typeName] = activeTypeCount + 1;

                        if (!crashThreadCandidates.TryGetValue(activeExceptionContext.ThreadId, out var candidate))
                        {
                            candidate = new CrashThreadCandidate
                            {
                                ThreadId = activeExceptionContext.ThreadId,
                                OSThreadId = activeExceptionContext.OSThreadId,
                                CurrentThreadStack = activeExceptionContext.CurrentThreadStack,
                                PrimaryExceptionType = typeName
                            };
                            crashThreadCandidates[activeExceptionContext.ThreadId] = candidate;
                        }
                        candidate.ActiveExceptionCount++;
                    }

                    if (!exceptionsByType.TryGetValue(typeName, out var list))
                    {
                        list = new List<ExceptionInstance>(capacity: MaxExceptionsPerType);
                        exceptionsByType[typeName] = list;
                    }
                    if (list.Count < MaxExceptionsPerType || isActive)
                    {
                        var exceptionInstance = ExtractExceptionInfo(heap, exceptionAddress, activeExceptionContext);
                        list.Add(exceptionInstance);
                    }
                }
            }

            scanCounter.Complete();

            var sortedTypeNames = exceptionTypeCounts.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key);
            var sortedExceptionsByType = new Dictionary<string, List<ExceptionInstance>>(exceptionsByType.Count);
            foreach (string typeName in sortedTypeNames)
            {
                if (exceptionsByType.TryGetValue(typeName, out var list))
                    sortedExceptionsByType[typeName] = list;
            }

            analysis.ExceptionTypeCounts = exceptionTypeCounts;
            analysis.ActiveExceptionTypeCounts = activeExceptionTypeCounts;
            analysis.ExceptionsByType = sortedExceptionsByType;
            analysis.CrashThreadCandidates = crashThreadCandidates.Values
                .OrderByDescending(c => c.ActiveExceptionCount)
                .ToList();
            return analysis;
        }

        private Dictionary<ulong, ActiveExceptionContext> BuildActiveExceptionLookup(ClrRuntime runtime)
        {
            var lookup = new Dictionary<ulong, ActiveExceptionContext>();
            var scanCounter = new ObjectScanCounter("Crash thread scan", reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (var thread in runtime.Threads)
            {
                scanCounter.Tick();

                if (thread.CurrentException == null)
                    continue;

                lookup[thread.CurrentException.Address] = new ActiveExceptionContext
                {
                    ThreadId = (uint)thread.ManagedThreadId,
                    OSThreadId = thread.OSThreadId,
                    CurrentThreadStack = thread.EnumerateStackTrace().Take(10).ToList()
                };
            }

            scanCounter.Complete();

            return lookup;
        }

        private static bool IsExceptionEntry(ClrHeap heap, in HeapEntry entry, Dictionary<ulong, bool> exceptionMethodTables)
        {
            if (entry.MethodTable == 0)
                return false;

            if (exceptionMethodTables.TryGetValue(entry.MethodTable, out bool isException))
                return isException;

            ClrObject obj = heap.GetObject(entry.Address);
            string? typeName = obj.IsValid ? obj.Type?.Name : null;
            isException = typeName?.Contains("Exception", StringComparison.Ordinal) == true;
            exceptionMethodTables[entry.MethodTable] = isException;
            return isException;
        }

        private ExceptionInstance ExtractExceptionInfo(ClrHeap heap, ulong exceptionAddress, ActiveExceptionContext? activeContext)
        {
            ClrObject exceptionObj = heap.GetObject(exceptionAddress);

            var instance = new ExceptionInstance
            {
                Address = exceptionAddress,
                Type = exceptionObj.Type?.Name ?? StringConstants.UnknownType
            };

            if (!exceptionObj.IsValid || exceptionObj.Type == null)
                return instance;

            try
            {
                // Get exception message
                var messageField = exceptionObj.Type?.GetFieldByName("_message");
                if (messageField != null)
                {
                    var messageObj = messageField.ReadObject(exceptionObj, interior: false);
                    if (messageObj.IsValid)
                    {
                        instance.Message = messageObj.AsString() ?? "";
                    }
                }

                // Get HRESULT
                var hresultField = exceptionObj.Type?.GetFieldByName("_HResult");
                if (hresultField != null)
                {
                    instance.HResult = hresultField.Read<int>(exceptionObj, interior: false);
                }

                // Get inner exception
                var innerExceptionField = exceptionObj.Type?.GetFieldByName("_innerException");
                if (innerExceptionField != null)
                {
                    var innerObj = innerExceptionField.ReadObject(exceptionObj, interior: false);
                    if (innerObj.IsValid && innerObj.Type != null)
                    {
                        instance.InnerExceptionType = innerObj.Type.Name;
                    }
                }

                // Get the ORIGINAL stack trace from exception object (not thread stack)
                instance.OriginalStackTrace = ExtractExceptionStackTrace(heap, exceptionAddress);

                if (activeContext != null)
                {
                    instance.ThreadId = activeContext.ThreadId;
                    instance.OSThreadId = activeContext.OSThreadId;
                    instance.CurrentThreadStack = activeContext.CurrentThreadStack;
                }
            }
            catch
            {
                // Continue with partial info
            }

            return instance;
        }

        private List<string> ExtractExceptionStackTrace(ClrHeap heap, ulong exceptionAddress)
        {
            var stackFrames = new List<string>();

            ClrObject exceptionObj = heap.GetObject(exceptionAddress);
            if (!exceptionObj.IsValid || exceptionObj.Type == null)
                return stackFrames;

            try
            {
                // Try to get _stackTraceString first (formatted string)
                var stackTraceStringField = exceptionObj.Type?.GetFieldByName("_stackTraceString");
                if (stackTraceStringField != null)
                {
                    var stackTraceObj = stackTraceStringField.ReadObject(exceptionObj, interior: false);
                    if (stackTraceObj.IsValid)
                    {
                        string? stackTraceStr = stackTraceObj.AsString();
                        if (!string.IsNullOrEmpty(stackTraceStr))
                        {
                            // Split by newlines and clean up
                            var lines = stackTraceStr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in lines)
                            {
                                string trimmed = line.Trim();
                                if (!string.IsNullOrEmpty(trimmed))
                                {
                                    stackFrames.Add(trimmed);
                                }
                            }
                            return stackFrames;
                        }
                    }
                }

                // Try to parse _stackTrace field (native format)
                var stackTraceField = exceptionObj.Type?.GetFieldByName("_stackTrace");
                if (stackTraceField != null)
                {
                    var stackTraceObj = stackTraceField.ReadObject(exceptionObj, interior: false);
                    if (stackTraceObj.IsValid && stackTraceObj.IsArray)
                    {
                        var array = stackTraceObj.AsArray();
                        for (int i = 0; i < Math.Min(array.Length, 50); i++)
                        {
                            var element = array.GetObjectValue(i);
                            if (element.IsValid && element.Type != null)
                            {
                                // Each element is a StackTraceElement - try to extract method info
                                var methodField = element.Type.GetFieldByName("_method");
                                if (methodField != null)
                                {
                                    var methodObj = methodField.ReadObject(element, interior: false);
                                    if (methodObj.IsValid)
                                    {
                                        // Try to get method name
                                        var nameField = methodObj.Type?.GetFieldByName("_name");
                                        if (nameField != null)
                                        {
                                            var nameObj = nameField.ReadObject(methodObj, interior: false);
                                            if (nameObj.IsValid)
                                            {
                                                string? methodName = nameObj.AsString();
                                                if (!string.IsNullOrEmpty(methodName))
                                                {
                                                    stackFrames.Add($"   at {methodName}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // If still no stack, try to get from exception's ToString()
                if (stackFrames.Count == 0)
                {
                    var remoteStackField = exceptionObj.Type?.GetFieldByName("_remoteStackTraceString");
                    if (remoteStackField != null)
                    {
                        var remoteStackObj = remoteStackField.ReadObject(exceptionObj, interior: false);
                        if (remoteStackObj.IsValid)
                        {
                            string? remoteStack = remoteStackObj.AsString();
                            if (!string.IsNullOrEmpty(remoteStack))
                            {
                                stackFrames.Add(remoteStack);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Return what we have
            }

            return stackFrames;
        }

    }

    internal class ExceptionAnalysis
    {
        public int TotalExceptions { get; set; }
        public int ActiveExceptions { get; set; }
        public Dictionary<string, int> ExceptionTypeCounts { get; set; } = new();
        public Dictionary<string, int> ActiveExceptionTypeCounts { get; set; } = new();
        public Dictionary<string, List<ExceptionInstance>> ExceptionsByType { get; set; } = new();
        public List<CrashThreadCandidate> CrashThreadCandidates { get; set; } = new();
    }

    internal class CrashThreadCandidate
    {
        public uint ThreadId { get; set; }
        public uint OSThreadId { get; set; }
        public int ActiveExceptionCount { get; set; }
        public string PrimaryExceptionType { get; set; } = string.Empty;
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
    }

    internal class ActiveExceptionContext
    {
        public uint ThreadId { get; set; }
        public uint OSThreadId { get; set; }
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
    }

    internal class ExceptionInstance
    {
        public ulong Address { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int HResult { get; set; }
        public string? InnerExceptionType { get; set; }
        public uint? ThreadId { get; set; }
        public uint? OSThreadId { get; set; }
        public List<string> OriginalStackTrace { get; set; } = new();
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
    }
}


