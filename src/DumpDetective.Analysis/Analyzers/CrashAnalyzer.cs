using System.Collections.Concurrent;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers
{
    public class CrashAnalyzer : IAnalyzer
    {
        private CrashAnalysisOptions _options = CrashAnalysisOptions.Default;

        public string Name => "Crash Analysis";
        public string Category => "Crash";

        public CrashAnalyzer()
        {
        }

        public CrashAnalyzer(CrashAnalysisOptions options)
        {
            _options = options ?? CrashAnalysisOptions.Default;
        }

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _options = context.GetOption<CrashAnalysisOptions>();
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Cache, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            return Analyze(runtime, heap, cache: null, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress)
        {
            var exceptionInfo = AnalyzeExceptions(heap, runtime, cache, progress);

            var candidateSnapshots = BuildCrashThreadSnapshots(exceptionInfo);

            // Respect payload options: by default include all types in payload. When disabled,
            // limit the sent type-counts to the top-N configured in options to reduce JSON size.
            Dictionary<string, int> payloadExceptionTypeCounts;
            Dictionary<string, int> payloadActiveExceptionTypeCounts;

            if (_options.IncludeAllTypesInPayload)
            {
                payloadExceptionTypeCounts = new Dictionary<string, int>(exceptionInfo.ExceptionTypeCounts);
                payloadActiveExceptionTypeCounts = new Dictionary<string, int>(exceptionInfo.ActiveExceptionTypeCounts);
            }
            else
            {
                payloadExceptionTypeCounts = exceptionInfo.ExceptionTypeCounts
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(_options.TopExceptionTypesToInclude)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

                payloadActiveExceptionTypeCounts = exceptionInfo.ActiveExceptionTypeCounts
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(_options.TopExceptionTypesToInclude)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
            }

            var domainResult = new CrashDomainResult(
                exceptionInfo.TotalExceptions,
                exceptionInfo.ActiveExceptions,
                payloadExceptionTypeCounts,
                payloadActiveExceptionTypeCounts,
                candidateSnapshots,
                BuildExceptionInstanceSnapshots(exceptionInfo),
                exceptionInfo.InferredTraceCount);

            if (exceptionInfo.TotalExceptions == 0)
            {
                return domainResult;
            }

            return domainResult;
        }

        private IReadOnlyList<CrashThreadCandidateSnapshot> BuildCrashThreadSnapshotsImpl(ExceptionAnalysis analysis)
        {
            // Flatten all exception instances once so inference loops are O(N) not O(N*K)
            var allInstances = new List<ExceptionInstance>(capacity: 64);
            foreach (var list in analysis.ExceptionsByType.Values)
                allInstances.AddRange(list);

            int inferredCount = 0;
            int take = Math.Min(analysis.CrashThreadCandidates.Count, _options.TopCrashThreadCandidates);
            var snapshots = new List<CrashThreadCandidateSnapshot>(take);

            for (int ci = 0; ci < take; ci++)
            {
                var c = analysis.CrashThreadCandidates[ci];
                List<string>? original = null;
                bool inferred = false;
                string? inferredFrom = null;
                var confidence = InferenceConfidence.None;

                // Tier 1: candidate already has its own original stack (exact)
                if (c.OriginalExceptionStack != null && c.OriginalExceptionStack.Count > 0)
                {
                    original = TakeNormalized(c.OriginalExceptionStack, _options.MaxOriginalStackFramesToPrint);
                    confidence = InferenceConfidence.Exact;
                }

                // Tier 2: match by ThreadId
                if (original == null)
                {
                    for (int i = 0; i < allInstances.Count; i++)
                    {
                        var e = allInstances[i];
                        if (e.ThreadId.HasValue && e.ThreadId.Value == c.ThreadId && e.OriginalStackTrace.Count > 0)
                        {
                            original = TakeNormalized(e.OriginalStackTrace, _options.MaxOriginalStackFramesToPrint);
                            inferred = true;
                            inferredFrom = $"0x{e.Address:X} ({e.Type})";
                            confidence = InferenceConfidence.ThreadId;
                            break;
                        }
                    }
                }

                // Tier 3: match by Message + HResult
                if (original == null && !string.IsNullOrWhiteSpace(c.SampleMessage))
                {
                    for (int i = 0; i < allInstances.Count; i++)
                    {
                        var e = allInstances[i];
                        if (!string.IsNullOrWhiteSpace(e.Message)
                            && e.Message == c.SampleMessage
                            && e.HResult == c.SampleHResult
                            && e.OriginalStackTrace.Count > 0)
                        {
                            original = TakeNormalized(e.OriginalStackTrace, _options.MaxOriginalStackFramesToPrint);
                            inferred = true;
                            inferredFrom = $"0x{e.Address:X} ({e.Type})";
                            confidence = InferenceConfidence.MessageHResult;
                            break;
                        }
                    }
                }

                // Tier 4: match by PrimaryExceptionType + InnerExceptionType (last-resort)
                if (original == null)
                {
                    for (int i = 0; i < allInstances.Count; i++)
                    {
                        var e = allInstances[i];
                        if (e.Type == c.PrimaryExceptionType
                            && e.OriginalStackTrace.Count > 0
                            && (!string.IsNullOrWhiteSpace(c.SampleInnerExceptionType)
                                ? e.InnerExceptionType == c.SampleInnerExceptionType
                                : e.InnerExceptionType == null))
                        {
                            original = TakeNormalized(e.OriginalStackTrace, _options.MaxOriginalStackFramesToPrint);
                            inferred = true;
                            inferredFrom = $"0x{e.Address:X} ({e.Type})";
                            confidence = InferenceConfidence.TypeInnerType;
                            break;
                        }
                    }
                }

                if (inferred) inferredCount++;

                // Build top-frames list without LINQ
                var topFrames = new List<string>(Math.Min(c.CurrentThreadStack.Count, _options.MaxCurrentThreadFramesToPrint));
                for (int f = 0; f < c.CurrentThreadStack.Count && f < _options.MaxCurrentThreadFramesToPrint; f++)
                {
                    var frame = c.CurrentThreadStack[f];
                    topFrames.Add(NormalizeFrame(frame.Method?.Signature ?? frame.FrameName ?? frame.ToString() ?? StringConstants.UnknownType));
                }

                snapshots.Add(new CrashThreadCandidateSnapshot(
                    c.ThreadId,
                    c.OSThreadId,
                    c.ActiveExceptionCount,
                    c.PrimaryExceptionType,
                    topFrames,
                    original,
                    inferred,
                    inferredFrom,
                    confidence));
            }

            analysis.InferredTraceCount = inferredCount;
            return snapshots;
        }

        // Backwards-compatible private static wrapper used by unit tests that expect
        // a static helper. Delegates to an instance method that can use runtime options.
        private static IReadOnlyList<CrashThreadCandidateSnapshot> BuildCrashThreadSnapshots(ExceptionAnalysis analysis)
        {
            return new CrashAnalyzer().BuildCrashThreadSnapshotsImpl(analysis);
        }

        // Take up to max frames, normalizing each one (strips "at " prefix, simplifies async names)
        private static List<string> TakeNormalized(List<string> frames, int max)
        {
            var result = new List<string>(Math.Min(frames.Count, max));
            for (int i = 0; i < frames.Count && i < max; i++)
                result.Add(NormalizeFrame(frames[i]));
            return result;
        }

        // Normalize a single raw stack frame string:
        //  - strips leading "   at " or "at " prefix (we re-add it during rendering)
        //  - simplifies async state-machine names: Foo+<Bar>d__N.MoveNext() → Foo.Bar() [async]
        //  - simplifies lambda names: Foo+<>c.<Bar>b__N_M() → Foo.Bar [lambda]
        internal static string NormalizeFrame(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            ReadOnlySpan<char> s = raw.AsSpan().TrimStart();

            // Strip "at " prefix (added by ToString() or _stackTraceString)
            if (s.Length > 3 && s[0] == 'a' && s[1] == 't' && s[2] == ' ')
                s = s[3..].TrimStart();

            string frame = s.ToString();

            // Async state machine: SomeNamespace.TypeName+<AsyncMethod>d__N.MoveNext()
            int plus = frame.IndexOf('+');
            if (plus > 0 && plus < frame.Length - 1)
            {
                ReadOnlySpan<char> rest = frame.AsSpan(plus + 1);
                if (rest.StartsWith("<", StringComparison.Ordinal))
                {
                    // Find the closing >
                    int close = frame.IndexOf('>', plus + 2);
                    if (close > plus + 2)
                    {
                        string methodName = frame[(plus + 2)..close];
                        ReadOnlySpan<char> suffix = frame.AsSpan(close + 1);

                        if (suffix.StartsWith("d__", StringComparison.Ordinal) || suffix.StartsWith(">d__", StringComparison.Ordinal))
                        {
                            // Async: TypeName.MethodName() [async]
                            string typePart = frame[..plus];
                            return $"{typePart}.{methodName}() [async]";
                        }

                        if (suffix.StartsWith(">c.<", StringComparison.Ordinal) || (rest.StartsWith("<>c.<", StringComparison.Ordinal)))
                        {
                            // Lambda: TypeName.MethodName [lambda]  
                            string typePart = frame[..plus];
                            return $"{typePart}.{methodName} [lambda]";
                        }
                    }
                }
            }

            return frame;
        }

        // Returns true if a (normalized) frame belongs to a runtime/framework namespace
        internal static bool IsFrameworkFrame(string frame)
        {
            return frame.StartsWith("System.", StringComparison.Ordinal)
                || frame.StartsWith("Microsoft.", StringComparison.Ordinal)
                || frame.StartsWith("mscorlib.", StringComparison.Ordinal)
                || frame.StartsWith("Windows.", StringComparison.Ordinal)
                || frame.Contains("System.Runtime.", StringComparison.Ordinal)
                || frame.Contains("System.Threading.", StringComparison.Ordinal);
        }

        // Resolve whether a method-table corresponds to an exception type and return its resolved name.
        private static (bool IsException, string? TypeName) ResolveExceptionType(
            ClrHeap heap,
            ulong exceptionAddress,
            ulong mt,
            IDictionary<ulong, bool> exceptionMethodTables,
            IDictionary<ulong, string> methodTableNameCache,
            HeapIndexBuildResult? heapIdx)
        {
            if (mt == 0)
                return (false, null);

            if (!exceptionMethodTables.TryGetValue(mt, out bool isException))
            {
                ClrObject o = heap.GetObject(exceptionAddress);
                string? n = o.IsValid ? o.Type?.Name : null;
                isException = n?.Contains("Exception", StringComparison.Ordinal) == true;
                exceptionMethodTables[mt] = isException;
            }

            if (!isException)
                return (false, null);

            if (!methodTableNameCache.TryGetValue(mt, out string? typeName))
            {
                string resolved;
                if (heapIdx?.TypeAggregates.TryGetValue(mt, out var agg) == true && agg.SampleAddress != 0)
                {
                    ClrObject sample = heap.GetObject(agg.SampleAddress);
                    resolved = (sample.IsValid && sample.Type != null)
                        ? sample.Type.Name ?? string.Empty
                        : heap.GetObject(exceptionAddress).Type?.Name ?? string.Empty;
                }
                else
                {
                    resolved = heap.GetObject(exceptionAddress).Type?.Name ?? string.Empty;
                }
                methodTableNameCache[mt] = resolved;
                typeName = resolved;
            }

            return (true, typeName);
        }

        private IReadOnlyList<ExceptionInstanceSnapshot> BuildExceptionInstanceSnapshots(ExceptionAnalysis analysis)
        {
            // Collect and sort without LINQ SelectMany+OrderBy allocation chain
            var flat = new List<(string Type, ExceptionInstance Instance)>(capacity: 64);
            foreach (var kvp in analysis.ExceptionsByType)
                for (int i = 0; i < kvp.Value.Count; i++)
                    flat.Add((kvp.Key, kvp.Value[i]));

            flat.Sort(static (a, b) =>
            {
                // Active first, then most frames, then has message
                int cmp = (b.Instance.ThreadId.HasValue ? 1 : 0).CompareTo(a.Instance.ThreadId.HasValue ? 1 : 0);
                if (cmp != 0) return cmp;
                cmp = b.Instance.OriginalStackTrace.Count.CompareTo(a.Instance.OriginalStackTrace.Count);
                if (cmp != 0) return cmp;
                return (string.IsNullOrWhiteSpace(b.Instance.Message) ? 0 : 1)
                    .CompareTo(string.IsNullOrWhiteSpace(a.Instance.Message) ? 0 : 1);
            });

            int limit = Math.Min(flat.Count, _options.TopDetailedExceptionInstances);
            var snapshots = new List<ExceptionInstanceSnapshot>(limit);

            for (int i = 0; i < limit; i++)
            {
                var (typeName, inst) = flat[i];

                List<string>? threadFrames = null;
                if (inst.CurrentThreadStack.Count > 0)
                {
                    threadFrames = new List<string>(Math.Min(inst.CurrentThreadStack.Count, _options.MaxCurrentThreadFramesToPrint));
                    for (int f = 0; f < inst.CurrentThreadStack.Count && f < _options.MaxCurrentThreadFramesToPrint; f++)
                    {
                        var fr = inst.CurrentThreadStack[f];
                        threadFrames.Add(NormalizeFrame(fr.Method?.Signature ?? fr.FrameName ?? fr.ToString() ?? StringConstants.UnknownType));
                    }
                }

                List<string>? origFrames = null;
                if (inst.OriginalStackTrace.Count > 0)
                    origFrames = TakeNormalized(inst.OriginalStackTrace, _options.MaxOriginalStackFramesToPrint);

                snapshots.Add(new ExceptionInstanceSnapshot(
                    typeName,
                    inst.Address,
                    string.IsNullOrWhiteSpace(inst.Message) ? null : inst.Message,
                    inst.HResult == 0 ? null : inst.HResult,
                    inst.InnerExceptionType,
                    inst.ThreadId.HasValue,
                    inst.ThreadId,
                    inst.OSThreadId,
                    threadFrames,
                    origFrames));
            }

            return snapshots;
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

        private ExceptionAnalysis AnalyzeExceptions(ClrHeap heap, ClrRuntime runtime, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress)
        {
            var activeExceptions = BuildActiveExceptionLookup(runtime);

            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out var heapIdx))
            {
                // In-memory index: parallel over the flat entry array
                if (heapIdx.StorageKind == HeapIndexStorageKind.Memory && heapIdx.InMemoryEntries is { } entries)
                    return RunParallelExceptionScan(heap, inMemoryEntries: entries, heapIdx: heapIdx, activeExceptions: activeExceptions, progress: progress);

                // Disk-backed index: sequential (I/O bound)
                return RunSequentialExceptionScan(heap, heapCache, cache, activeExceptions, progress);
            }

            // No cache: parallel over GC segments
            return RunParallelExceptionScan(heap, inMemoryEntries: null, heapIdx: null, activeExceptions: activeExceptions, progress: progress);
        }

        // Unified parallel exception scanner — drives either a flat in-memory HeapEntry[]
        // or a per-segment ClrObject walk using the same concurrent accumulation logic.
        private ExceptionAnalysis RunParallelExceptionScan(
            ClrHeap heap,
            HeapEntry[]? inMemoryEntries,
            HeapIndexBuildResult? heapIdx,
            Dictionary<ulong, ActiveExceptionContext> activeExceptions,
            IProgress<AnalyzerProgressReport>? progress)
        {
            var exceptionMethodTables = new ConcurrentDictionary<ulong, bool>();
            var methodTableNameCache = new ConcurrentDictionary<ulong, string>();
            var exceptionTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var activeExceptionTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionInstances = new ConcurrentBag<(string TypeName, ExceptionInstance Instance, bool IsActive)>();
            var crashThreadCandidates = new ConcurrentDictionary<uint, CrashThreadCandidate>();
            var candidateLock = new object();
            int totalExceptions = 0, activeExceptionsCount = 0;
            long scanned = 0;
            const long progressInterval = 50_000;

            void ProcessEntry(ulong exceptionAddress, ulong mt)
            {
                long s = Interlocked.Increment(ref scanned);
                if (s % progressInterval == 0)
                    progress?.Report(new(s, "scanning for exceptions"));

                if (exceptionAddress == 0)
                    return;
                var (isException, typeName) = ResolveExceptionType(heap, exceptionAddress, mt, exceptionMethodTables, methodTableNameCache, heapIdx);
                if (!isException)
                    return;

                string key = typeName ?? StringConstants.UnknownType;
                Interlocked.Increment(ref totalExceptions);
                exceptionTypeCounts.AddOrUpdate(key, 1, (_, c) => c + 1);

                bool isActive = activeExceptions.TryGetValue(exceptionAddress, out var activeCtx);

                // Extract exception info now so we can capture the ORIGINAL stack trace for crash candidates.
                var exceptionInstance = ExtractExceptionInfo(heap, exceptionAddress, isActive ? activeCtx : null);

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

                        // Attach original exception stack (if any) so we can trace back to original call site.
                        if (exceptionInstance.OriginalStackTrace != null && exceptionInstance.OriginalStackTrace.Count > 0)
                        {
                            candidate.OriginalExceptionStack = exceptionInstance.OriginalStackTrace;
                        }
                        // store sample metadata for heuristic matching later
                        if (!string.IsNullOrWhiteSpace(exceptionInstance.Message))
                            candidate.SampleMessage = exceptionInstance.Message;
                        candidate.SampleHResult = exceptionInstance.HResult;
                        if (candidate.SampleInnerExceptionType == null)
                            candidate.SampleInnerExceptionType = exceptionInstance.InnerExceptionType;
                    }
                }

                exceptionInstances.Add((key, exceptionInstance, isActive));
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
            progress?.Report(new(scanned, "aggregating exceptions"));
            var exceptionsByType = new Dictionary<string, List<ExceptionInstance>>(StringComparer.Ordinal);
            foreach (var (typeName, instance, isActive) in exceptionInstances)
            {
                if (!exceptionsByType.TryGetValue(typeName, out var list))
                {
                    list = new List<ExceptionInstance>(capacity: _options.MaxExceptionsPerType);
                    exceptionsByType[typeName] = list;
                }
                if (list.Count < _options.MaxExceptionsPerType || isActive)
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
            Dictionary<ulong, ActiveExceptionContext> activeExceptions,
            IProgress<AnalyzerProgressReport>? progress)
        {
            var analysis = new ExceptionAnalysis();
            var exceptionsByType = new Dictionary<string, List<ExceptionInstance>>();
            var exceptionTypeCounts = new Dictionary<string, int>();
            var activeExceptionTypeCounts = new Dictionary<string, int>();
            var exceptionMethodTables = new Dictionary<ulong, bool>(capacity: 64);
            var methodTableNameCache = new Dictionary<ulong, string>(capacity: 64);
            var crashThreadCandidates = new Dictionary<uint, CrashThreadCandidate>();
            var scanCounter = new ObjectScanCounter("scanning for exceptions", progress);

            foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
            {
                scanCounter.Tick();

                ulong exceptionAddress = entry.Address;
                if (exceptionAddress == 0)
                    continue;

                if (!IsExceptionEntry(heap, entry, exceptionMethodTables))
                    continue;

                ulong mt = entry.MethodTable;
                    var (isException, typeName) = ResolveExceptionType(heap, exceptionAddress, mt, exceptionMethodTables, methodTableNameCache, null);
                if (isException)
                {
                    string key = typeName ?? StringConstants.UnknownType;
                    analysis.TotalExceptions++;
                    exceptionTypeCounts.TryGetValue(key, out int typeCount);
                    exceptionTypeCounts[key] = typeCount + 1;

                    bool isActive = activeExceptions.TryGetValue(exceptionAddress, out var activeExceptionContext);
                    if (isActive)
                    {
                        analysis.ActiveExceptions++;
                        activeExceptionTypeCounts.TryGetValue(key, out int activeTypeCount);
                        activeExceptionTypeCounts[key] = activeTypeCount + 1;

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

                    if (!exceptionsByType.TryGetValue(key, out var list))
                    {
                        list = new List<ExceptionInstance>(capacity: _options.MaxExceptionsPerType);
                        exceptionsByType[key] = list;
                    }
                    if (list.Count < _options.MaxExceptionsPerType || isActive)
                    {
                        var exceptionInstance = ExtractExceptionInfo(heap, exceptionAddress, isActive ? activeExceptionContext : null);
                        if (isActive && exceptionInstance.OriginalStackTrace != null && exceptionInstance.OriginalStackTrace.Count > 0)
                        {
                            // store original stack on the candidate so UI can show the trace back to original call site
                            crashThreadCandidates[activeExceptionContext.ThreadId].OriginalExceptionStack = exceptionInstance.OriginalStackTrace;
                        }
                        if (isActive)
                        {
                            var candidate = crashThreadCandidates[activeExceptionContext.ThreadId];
                            if (!string.IsNullOrWhiteSpace(exceptionInstance.Message))
                                candidate.SampleMessage = exceptionInstance.Message;
                            candidate.SampleHResult = exceptionInstance.HResult;
                            if (candidate.SampleInnerExceptionType == null)
                                candidate.SampleInnerExceptionType = exceptionInstance.InnerExceptionType;
                        }
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

        public void Dispose() { }

    }

}



