using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;

namespace DumpDetective.Analysis.Analyzers
{
    public class CrashAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant
    {
        private CrashAnalysisOptions _options = CrashAnalysisOptions.Default;
        private ILogger<CrashAnalyzer>? _logger;

        public string Name => "Crash Analysis";
        public string Category => "Crash";

        // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
        // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
        // OnHeapEntry; consumed by AnalyzeAsync once the shared index scan has completed.
        private ClrHeap? _heap;
        private Dictionary<ulong, ActiveExceptionContext>? _activeExceptions;
        private Dictionary<string, List<ExceptionInstance>>? _exceptionsByType;
        private Dictionary<string, int>? _exceptionTypeCounts;
        private Dictionary<string, int>? _activeExceptionTypeCounts;
        private Dictionary<ulong, bool>? _exceptionMethodTables;
        private Dictionary<ulong, string>? _methodTableNameCache;
        private Dictionary<uint, CrashThreadCandidate>? _crashThreadCandidates;
        private Dictionary<string, int>? _exceptionGen0Counts;
        private Dictionary<string, int>? _exceptionGen1Counts;
        private Dictionary<string, int>? _exceptionGen2Counts;
        private Dictionary<string, int>? _exceptionLohCounts;
        private int _totalExceptions;
        private int _activeExceptionsCount;
        private ObjectScanCounter? _scanCounter;
        // Set by OnHeapIndexScanCompleted — the single source of truth for whether the
        // participant-accumulated state above is trustworthy. Avoids re-deriving "did the
        // shared scan run" from a second cache.TryGetHeapIndex call in AnalyzeAsync.
        private bool _participantScanSucceeded;

        public CrashAnalyzer()
        {
        }

        public CrashAnalyzer(CrashAnalysisOptions options)
        {
            _options = options ?? CrashAnalysisOptions.Default;
        }

        public CrashAnalyzer(CrashAnalysisOptions options, ILogger<CrashAnalyzer>? logger)
        {
            _options = options ?? CrashAnalysisOptions.Default;
            _logger = logger;
        }

        /// <summary>
        /// Builds the active-exception lookup from live thread state and resets the per-entry
        /// accumulator fields ahead of the shared heap-index scan pass.
        /// </summary>
        public void BeforeHeapIndexScan(AnalysisContext context)
        {
            _options = context.AnalysisOptions.Crash;
            _heap = context.Heap;
            _activeExceptions = BuildActiveExceptionLookup(context.Runtime);

            _exceptionsByType = new Dictionary<string, List<ExceptionInstance>>();
            _exceptionTypeCounts = new Dictionary<string, int>();
            _activeExceptionTypeCounts = new Dictionary<string, int>();
            _exceptionMethodTables = new Dictionary<ulong, bool>(capacity: 64);
            _methodTableNameCache = new Dictionary<ulong, string>(capacity: 64);
            _crashThreadCandidates = new Dictionary<uint, CrashThreadCandidate>();
            _exceptionGen0Counts = new Dictionary<string, int>();
            _exceptionGen1Counts = new Dictionary<string, int>();
            _exceptionGen2Counts = new Dictionary<string, int>();
            _exceptionLohCounts = new Dictionary<string, int>();
            _totalExceptions = 0;
            _activeExceptionsCount = 0;
            _scanCounter = new ObjectScanCounter("scanning for exceptions", context.Progress);
        }

        /// <summary>
        /// Called once per disk-backed index entry, in address order, during the shared heap-index
        /// scan pass. Mirrors the historical <c>RunSequentialExceptionScan</c> loop body, operating
        /// on instance fields. Explicit interface implementation because <see cref="HeapEntry"/> is
        /// internal and this class is public — an implicit implementation would leak the internal
        /// type as public API.
        /// </summary>
        void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => OnHeapEntry(in entry);

        public void OnHeapIndexScanCompleted(bool succeeded) => _participantScanSucceeded = succeeded;

        IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new CrashAnalyzer(_options, _logger);

        // Workers cover disjoint, ascending-address record ranges (see HeapIndexScanDispatcher.
        // RunParallelPass), and `partials` arrives in that same ascending order with `this`
        // covering the lowest range. Merging in that order lets each per-type exception list be
        // recapped exactly as the sequential pass would have: concatenate in address order, then
        // replay the same "active always kept, non-active capped at MaxExceptionsPerType" rule.
        void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
        {
            var others = new List<CrashAnalyzer>(partials.Count);
            foreach (IHeapIndexScanParticipant p in partials)
                others.Add((CrashAnalyzer)p);

            _totalExceptions += others.Sum(o => o._totalExceptions);
            _activeExceptionsCount += others.Sum(o => o._activeExceptionsCount);

            foreach (CrashAnalyzer other in others)
            {
                foreach (var kvp in other._exceptionTypeCounts!)
                {
                    _exceptionTypeCounts!.TryGetValue(kvp.Key, out int count);
                    _exceptionTypeCounts[kvp.Key] = count + kvp.Value;
                }

                foreach (var kvp in other._activeExceptionTypeCounts!)
                {
                    _activeExceptionTypeCounts!.TryGetValue(kvp.Key, out int count);
                    _activeExceptionTypeCounts[kvp.Key] = count + kvp.Value;
                }
            }

            var typeKeys = new HashSet<string>(_exceptionsByType!.Keys, StringComparer.Ordinal);
            foreach (CrashAnalyzer other in others)
                typeKeys.UnionWith(other._exceptionsByType!.Keys);

            foreach (string typeKey in typeKeys)
            {
                var merged = new List<ExceptionInstance>(capacity: _options.MaxExceptionsPerType);
                int nonActiveKept = 0;

                if (_exceptionsByType!.TryGetValue(typeKey, out var selfList))
                    AppendCapped(selfList, merged, ref nonActiveKept, _options.MaxExceptionsPerType);

                foreach (CrashAnalyzer other in others)
                {
                    if (other._exceptionsByType!.TryGetValue(typeKey, out var otherList))
                        AppendCapped(otherList, merged, ref nonActiveKept, _options.MaxExceptionsPerType);
                }

                _exceptionsByType[typeKey] = merged;
            }

            // Re-sort keys by merged total count so BuildAnalysisFromParticipantState's ordering
            // (which iterates _exceptionTypeCounts, not _exceptionsByType, for ordering) stays correct;
            // _exceptionTypeCounts was already updated above, no further action needed there.

            foreach (CrashAnalyzer other in others)
            {
                foreach (var kvp in other._crashThreadCandidates!)
                {
                    if (!_crashThreadCandidates!.TryGetValue(kvp.Key, out var existing))
                    {
                        _crashThreadCandidates[kvp.Key] = kvp.Value;
                        continue;
                    }

                    CrashThreadCandidate incoming = kvp.Value;
                    existing.ActiveExceptionCount += incoming.ActiveExceptionCount;
                    if (incoming.OriginalExceptionStack != null)
                        existing.OriginalExceptionStack = incoming.OriginalExceptionStack;
                    if (!string.IsNullOrWhiteSpace(incoming.SampleMessage))
                        existing.SampleMessage = incoming.SampleMessage;
                    if (incoming.SampleHResult != 0)
                        existing.SampleHResult = incoming.SampleHResult;
                    if (existing.SampleInnerExceptionType == null)
                        existing.SampleInnerExceptionType = incoming.SampleInnerExceptionType;
                }
            }
        }

        // Appends entries from a worker-local (already Max-capped) instance list into the merged
        // list, keeping active instances unconditionally and non-active ones until the shared
        // running count reaches MaxExceptionsPerType — mirrors OnHeapEntry's per-entry admission rule.
        private static void AppendCapped(List<ExceptionInstance> source, List<ExceptionInstance> destination, ref int nonActiveKept, int maxExceptionsPerType)
        {
            foreach (ExceptionInstance instance in source)
            {
                bool isActive = instance.ThreadId.HasValue;
                if (isActive)
                {
                    destination.Add(instance);
                    continue;
                }

                if (nonActiveKept < maxExceptionsPerType)
                {
                    destination.Add(instance);
                    nonActiveKept++;
                }
            }
        }

        private void OnHeapEntry(in HeapEntry entry)
        {
            _scanCounter!.Tick();

            ulong exceptionAddress = entry.Address;
            if (exceptionAddress == 0)
                return;

            if (!IsExceptionEntry(_heap!, entry, _exceptionMethodTables!))
                return;

            ulong mt = entry.MethodTable;
            var (isException, typeName) = ResolveExceptionType(_heap!, exceptionAddress, mt, _exceptionMethodTables!, _methodTableNameCache!, null);
            if (!isException)
                return;

            string key = typeName ?? StringConstants.UnknownType;
            _totalExceptions++;
            _exceptionTypeCounts!.TryGetValue(key, out int typeCount);
            _exceptionTypeCounts[key] = typeCount + 1;

            try
            {
                var seg = _heap!.GetSegmentByAddress(exceptionAddress);
                if (seg != null)
                {
                    int generation = (int)seg.GetGeneration(exceptionAddress);
                    switch (generation)
                    {
                        case 0:
                            _exceptionGen0Counts!.TryGetValue(key, out int gen0);
                            _exceptionGen0Counts[key] = gen0 + 1;
                            break;
                        case 1:
                            _exceptionGen1Counts!.TryGetValue(key, out int gen1);
                            _exceptionGen1Counts[key] = gen1 + 1;
                            break;
                        case 2:
                            _exceptionGen2Counts!.TryGetValue(key, out int gen2);
                            _exceptionGen2Counts[key] = gen2 + 1;
                            break;
                        default:
                            _exceptionLohCounts!.TryGetValue(key, out int loh);
                            _exceptionLohCounts[key] = loh + 1;
                            break;
                    }
                }
            }
            catch { }

            bool isActive = _activeExceptions!.TryGetValue(exceptionAddress, out var activeExceptionContext);
            if (isActive)
            {
                _activeExceptionsCount++;
                _activeExceptionTypeCounts!.TryGetValue(key, out int activeTypeCount);
                _activeExceptionTypeCounts[key] = activeTypeCount + 1;

                if (!_crashThreadCandidates!.TryGetValue(activeExceptionContext.ThreadId, out var candidate))
                {
                    candidate = new CrashThreadCandidate
                    {
                        ThreadId = activeExceptionContext.ThreadId,
                        OSThreadId = activeExceptionContext.OSThreadId,
                        CurrentThreadStack = activeExceptionContext.CurrentThreadStack,
                        PrimaryExceptionType = typeName
                    };
                    _crashThreadCandidates[activeExceptionContext.ThreadId] = candidate;
                }
                candidate.ActiveExceptionCount++;
            }

            if (!_exceptionsByType!.TryGetValue(key, out var list))
            {
                list = new List<ExceptionInstance>(capacity: _options.MaxExceptionsPerType);
                _exceptionsByType[key] = list;
            }
            if (list.Count < _options.MaxExceptionsPerType || isActive)
            {
                var exceptionInstance = ExtractExceptionInfo(_heap!, exceptionAddress, isActive ? activeExceptionContext : null);
                if (isActive && exceptionInstance.OriginalStackTrace != null && exceptionInstance.OriginalStackTrace.Count > 0)
                {
                    // store original stack on the candidate so UI can show the trace back to original call site
                    _crashThreadCandidates![activeExceptionContext.ThreadId].OriginalExceptionStack = exceptionInstance.OriginalStackTrace;
                }
                if (isActive)
                {
                    var candidate = _crashThreadCandidates![activeExceptionContext.ThreadId];
                    if (!string.IsNullOrWhiteSpace(exceptionInstance.Message))
                        candidate.SampleMessage = exceptionInstance.Message;
                    candidate.SampleHResult = exceptionInstance.HResult;
                    if (candidate.SampleInnerExceptionType == null)
                        candidate.SampleInnerExceptionType = exceptionInstance.InnerExceptionType;
                }
                list.Add(exceptionInstance);
            }
        }

        // Relies on the pipeline dispatcher having already called BeforeHeapIndexScan/OnHeapEntry
        // for this context before AnalyzeAsync runs (see AnalysisPipeline.ExecuteAsync).
        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ExceptionAnalysis exceptionInfo = _participantScanSucceeded
                ? BuildAnalysisFromParticipantState()
                : AnalyzeExceptions(context.Heap, context.Runtime, context.Progress);

            return ValueTask.FromResult(BuildDomainResult(exceptionInfo).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            return BuildDomainResult(AnalyzeExceptions(heap, runtime, progress: null));
        }

        private ExceptionAnalysis BuildAnalysisFromParticipantState()
        {
            _scanCounter?.Complete();

            var sortedTypeNames = _exceptionTypeCounts!.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key);
            var sortedExceptionsByType = new Dictionary<string, List<ExceptionInstance>>(_exceptionsByType!.Count);
            foreach (string typeName in sortedTypeNames)
            {
                if (_exceptionsByType.TryGetValue(typeName, out var list))
                    sortedExceptionsByType[typeName] = list;
            }

            return new ExceptionAnalysis
            {
                TotalExceptions = _totalExceptions,
                ActiveExceptions = _activeExceptionsCount,
                ExceptionTypeCounts = _exceptionTypeCounts,
                ActiveExceptionTypeCounts = _activeExceptionTypeCounts!,
                ExceptionsByType = sortedExceptionsByType,
                CrashThreadCandidates = _crashThreadCandidates!.Values
                    .OrderByDescending(c => c.ActiveExceptionCount)
                    .ToList(),
                ExceptionGen0Counts = _exceptionGen0Counts!,
                ExceptionGen1Counts = _exceptionGen1Counts!,
                ExceptionGen2Counts = _exceptionGen2Counts!,
                ExceptionLohCounts = _exceptionLohCounts!
            };
        }

        private AnalyzerDomainResult BuildDomainResult(ExceptionAnalysis exceptionInfo)
        {
            var candidateSnapshots = BuildCrashThreadSnapshotsImpl(exceptionInfo);

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
                isException = o.IsValid && o.Type?.IsException == true;
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
                    inst.ChainDepth,
                    inst.ThreadId.HasValue,
                    inst.ThreadId,
                    inst.OSThreadId,
                    threadFrames,
                    origFrames));
            }

            return snapshots;
        }

        // No-index fallback path only: used when there's no on-disk/in-memory heap index to drive
        // the shared IHeapIndexScanParticipant dispatcher pass (e.g. the public Analyze(runtime, heap)
        // overload, or AnalyzeAsync when the context's cache has no prebuilt index).
        private ExceptionAnalysis AnalyzeExceptions(ClrHeap heap, ClrRuntime runtime, IProgress<AnalyzerProgressReport>? progress)
        {
            var activeExceptions = BuildActiveExceptionLookup(runtime);
            return RunParallelExceptionScan(heap, activeExceptions, progress);
        }

        // Parallel exception scanner over GC segments — the no-index fallback.
        private ExceptionAnalysis RunParallelExceptionScan(
            ClrHeap heap,
            Dictionary<ulong, ActiveExceptionContext> activeExceptions,
            IProgress<AnalyzerProgressReport>? progress)
        {
            var exceptionMethodTables = new ConcurrentDictionary<ulong, bool>();
            var methodTableNameCache = new ConcurrentDictionary<ulong, string>();
            var exceptionTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var activeExceptionTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionInstances = new ConcurrentBag<(string TypeName, ExceptionInstance Instance, bool IsActive)>();
            var crashThreadCandidates = new ConcurrentDictionary<uint, CrashThreadCandidate>();
            var exceptionGen0Counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionGen1Counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionGen2Counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionLohCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var candidateLock = new object();
            int totalExceptions = 0, activeExceptionsCount = 0;
            var scanCounter = new DumpDetective.Analysis.Cache.ObjectScanCounter("scanning for exceptions", progress, reportEveryObjects: 50_000, reportEveryElapsed: TimeSpan.FromSeconds(2));

            void ProcessEntry(ulong exceptionAddress, ulong mt)
            {
                scanCounter.Tick();

                if (exceptionAddress == 0)
                    return;
                var (isException, typeName) = ResolveExceptionType(heap, exceptionAddress, mt, exceptionMethodTables, methodTableNameCache, null);
                if (!isException)
                    return;

                string key = typeName ?? StringConstants.UnknownType;
                Interlocked.Increment(ref totalExceptions);
                exceptionTypeCounts.AddOrUpdate(key, 1, (_, c) => c + 1);

                try
                {
                    var seg = heap.GetSegmentByAddress(exceptionAddress);
                    if (seg != null)
                    {
                        int generation = (int)seg.GetGeneration(exceptionAddress);
                        switch (generation)
                        {
                            case 0:
                                exceptionGen0Counts.AddOrUpdate(key, 1, (_, c) => c + 1);
                                break;
                            case 1:
                                exceptionGen1Counts.AddOrUpdate(key, 1, (_, c) => c + 1);
                                break;
                            case 2:
                                exceptionGen2Counts.AddOrUpdate(key, 1, (_, c) => c + 1);
                                break;
                            default:
                                exceptionLohCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
                                break;
                        }
                    }
                }
                catch { }

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

            // Sequential post-processing: build per-type exception list with cap enforcement.
            // exceptionInstances came off a ConcurrentBag whose order depends on thread scheduling;
            // sort by address first so the capped "first N per type" set is deterministic and agrees
            // with the disk-backed scan (which enumerates entries in ascending address order).
            long scanned = scanCounter.Scanned;
            progress?.Report(new(scanned, "aggregating exceptions"));
            var orderedInstances = exceptionInstances.ToArray();
            Array.Sort(orderedInstances, static (a, b) => a.Instance.Address.CompareTo(b.Instance.Address));
            var exceptionsByType = new Dictionary<string, List<ExceptionInstance>>(StringComparer.Ordinal);
            foreach (var (typeName, instance, isActive) in orderedInstances)
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
                    .ToList(),
                ExceptionGen0Counts = new Dictionary<string, int>(exceptionGen0Counts, StringComparer.Ordinal),
                ExceptionGen1Counts = new Dictionary<string, int>(exceptionGen1Counts, StringComparer.Ordinal),
                ExceptionGen2Counts = new Dictionary<string, int>(exceptionGen2Counts, StringComparer.Ordinal),
                ExceptionLohCounts = new Dictionary<string, int>(exceptionLohCounts, StringComparer.Ordinal)
            };
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
                    CurrentThreadStack = thread.EnumerateStackTrace().Take(_options.MaxCurrentThreadFramesToPrint).ToList()
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
            isException = obj.IsValid && obj.Type?.IsException == true;
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
                var clrException = exceptionObj.AsException();
                if (clrException != null)
                {
                    // Use ClrException wrapper for typed field access
                    instance.Message = clrException.Message ?? "";
                    instance.HResult = clrException.HResult;
                }

                // Get inner exception via field access (ClrException doesn't expose it)
                var innerExceptionField = exceptionObj.Type?.GetFieldByName("_innerException");
                if (innerExceptionField != null)
                {
                    var innerObj = innerExceptionField.ReadObject(exceptionObj, interior: false);
                    if (innerObj.IsValid && innerObj.Type != null)
                    {
                        instance.InnerExceptionType = innerObj.Type.Name;
                    }
                }

                instance.ChainDepth = ComputeExceptionChainDepth(heap, exceptionAddress);

                // Get the ORIGINAL stack trace from exception object (not thread stack)
                instance.OriginalStackTrace = ExtractExceptionStackTrace(heap, exceptionAddress);

                if (activeContext != null)
                {
                    instance.ThreadId = activeContext.ThreadId;
                    instance.OSThreadId = activeContext.OSThreadId;
                    instance.CurrentThreadStack = activeContext.CurrentThreadStack;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error extracting exception info from 0x{Address:X}", exceptionAddress);
            }

            return instance;
        }

        private static int ComputeExceptionChainDepth(ClrHeap heap, ulong exceptionAddress)
        {
            const int MaxDepth = 16;
            int depth = 1;
            var seen = new HashSet<ulong>();
            ulong currentAddress = exceptionAddress;

            while (currentAddress != 0 && depth < MaxDepth && seen.Add(currentAddress))
            {
                ClrObject current = heap.GetObject(currentAddress);
                if (!current.IsValid || current.Type is null)
                    break;

                var innerField = current.Type.GetFieldByName("_innerException");
                if (innerField is null)
                    break;

                ClrObject inner = innerField.ReadObject(current, interior: false);
                if (!inner.IsValid || inner.Type is null)
                    break;

                depth++;
                currentAddress = inner.Address;
            }

            return depth;
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

                // Try ClrException.StackTrace (the correct API for stack frames)
                var clrException = exceptionObj.AsException();
                if (clrException?.StackTrace != null)
                {
                    var strace = clrException.StackTrace;
                    if (strace.Count() > 0)
                    {
                        int count = strace.Count();
                        for (int i = 0; i < Math.Min(count, 50); i++)
                        {
                            var frame = strace[i];
                            if (frame?.Method != null)
                            {
                                stackFrames.Add($"   at {frame.Method.Signature}");
                            }
                        }
                        if (stackFrames.Count > 0)
                            return stackFrames;
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
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error extracting stack trace from exception at 0x{Address:X}", exceptionAddress);
            }

            return stackFrames;
        }

        public void Dispose() { }

    }

}



