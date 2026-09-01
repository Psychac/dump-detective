using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Traversal;
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
        private CrashAnalysisOptions _options = new CrashAnalysisOptions();
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
        private Dictionary<ulong, bool>? _aggregateExceptionMethodTables;
        private Dictionary<ulong, string>? _methodTableNameCache;
        private Dictionary<uint, CrashThreadCandidate>? _crashThreadCandidates;
        private Dictionary<string, int>? _exceptionGen0Counts;
        private Dictionary<string, int>? _exceptionGen1Counts;
        private Dictionary<string, int>? _exceptionGen2Counts;
        private Dictionary<string, int>? _exceptionLohCounts;
        private Dictionary<string, int>? _aggregateInnerExceptionTypeCounts;
        private Dictionary<string, ulong>? _exceptionHeapSizeByType;
        private int _totalExceptions;
        private int _activeExceptionsCount;
        private int _aggregateExceptionCount;
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
            _options = options ?? new CrashAnalysisOptions();
        }

        public CrashAnalyzer(CrashAnalysisOptions options, ILogger<CrashAnalyzer>? logger)
        {
            _options = options ?? new CrashAnalysisOptions();
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
            _aggregateExceptionMethodTables = new Dictionary<ulong, bool>(capacity: 64);
            _methodTableNameCache = new Dictionary<ulong, string>(capacity: 64);
            _crashThreadCandidates = new Dictionary<uint, CrashThreadCandidate>();
            _exceptionGen0Counts = new Dictionary<string, int>();
            _exceptionGen1Counts = new Dictionary<string, int>();
            _exceptionGen2Counts = new Dictionary<string, int>();
            _exceptionLohCounts = new Dictionary<string, int>();
            _aggregateInnerExceptionTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            _exceptionHeapSizeByType = new Dictionary<string, ulong>(StringComparer.Ordinal);
            _totalExceptions = 0;
            _activeExceptionsCount = 0;
            _aggregateExceptionCount = 0;
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
            _aggregateExceptionCount += others.Sum(o => o._aggregateExceptionCount);

            foreach (CrashAnalyzer other in others)
            {
                MergeCounts(_exceptionTypeCounts!, other._exceptionTypeCounts!);
                MergeCounts(_activeExceptionTypeCounts!, other._activeExceptionTypeCounts!);
                // Gen0/Gen1/Gen2/LOH and aggregate-inner-type counts are computed unconditionally
                // per entry (never sampled), so they must be summed across every worker too —
                // previously missing here, which silently dropped all but the primary worker's
                // generation distribution on parallel disk-index scans.
                MergeCounts(_exceptionGen0Counts!, other._exceptionGen0Counts!);
                MergeCounts(_exceptionGen1Counts!, other._exceptionGen1Counts!);
                MergeCounts(_exceptionGen2Counts!, other._exceptionGen2Counts!);
                MergeCounts(_exceptionLohCounts!, other._exceptionLohCounts!);
                MergeCounts(_aggregateInnerExceptionTypeCounts!, other._aggregateInnerExceptionTypeCounts!);
                MergeSizes(_exceptionHeapSizeByType!, other._exceptionHeapSizeByType!);
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
                    {
                        existing.OriginalExceptionStack = incoming.OriginalExceptionStack;
                        existing.OriginalExceptionStackIsRethrown = incoming.OriginalExceptionStackIsRethrown;
                    }
                    if (!string.IsNullOrWhiteSpace(incoming.SampleMessage))
                        existing.SampleMessage = incoming.SampleMessage;
                    if (incoming.SampleHResult != 0)
                        existing.SampleHResult = incoming.SampleHResult;
                    if (existing.SampleInnerExceptionType == null)
                        existing.SampleInnerExceptionType = incoming.SampleInnerExceptionType;
                }
            }
        }

        private static void MergeCounts(Dictionary<string, int> target, Dictionary<string, int> source)
        {
            foreach (var kvp in source)
            {
                target.TryGetValue(kvp.Key, out int count);
                target[kvp.Key] = count + kvp.Value;
            }
        }

        private static void MergeSizes(Dictionary<string, ulong> target, Dictionary<string, ulong> source)
        {
            foreach (var kvp in source)
            {
                target.TryGetValue(kvp.Key, out ulong size);
                target[kvp.Key] = size + kvp.Value;
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
            var (isException, isAggregate, typeName) = ResolveExceptionType(_heap!, mt, _exceptionMethodTables!, _aggregateExceptionMethodTables!, _methodTableNameCache!);
            if (!isException)
                return;

            string key = typeName ?? StringConstants.UnknownType;
            _totalExceptions++;
            _exceptionTypeCounts!.TryGetValue(key, out int typeCount);
            _exceptionTypeCounts[key] = typeCount + 1;

            _exceptionHeapSizeByType!.TryGetValue(key, out ulong heapSize);
            _exceptionHeapSizeByType[key] = heapSize + entry.Size;

            // AggregateException unwrapping: computed unconditionally (not gated by
            // MaxExceptionsPerType) so AggregateInnerExceptionTypeCounts is an exact total, not a
            // sample — same rationale as the Gen0/Gen1/Gen2/LOH counts below.
            List<string>? aggregateInnerTypes = null;
            if (isAggregate)
            {
                aggregateInnerTypes = ExtractAggregateInnerExceptionTypes(_heap!, exceptionAddress);
                if (aggregateInnerTypes != null)
                {
                    _aggregateExceptionCount++;
                    for (int i = 0; i < aggregateInnerTypes.Count; i++)
                    {
                        string innerKey = aggregateInnerTypes[i];
                        _aggregateInnerExceptionTypeCounts!.TryGetValue(innerKey, out int innerCount);
                        _aggregateInnerExceptionTypeCounts[innerKey] = innerCount + 1;
                    }
                }
            }

            int generation = entry.Generation;
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
                exceptionInstance.Generation = generation;
                if (aggregateInnerTypes != null)
                    exceptionInstance.AggregateInnerExceptionTypes = CapForDisplay(aggregateInnerTypes);
                if (isActive && exceptionInstance.OriginalStackTrace != null && exceptionInstance.OriginalStackTrace.Count > 0)
                {
                    // store original stack on the candidate so UI can show the trace back to original call site
                    var activeCandidate = _crashThreadCandidates![activeExceptionContext.ThreadId];
                    activeCandidate.OriginalExceptionStack = exceptionInstance.OriginalStackTrace;
                    activeCandidate.OriginalExceptionStackIsRethrown = exceptionInstance.IsRethrown;
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

            return ValueTask.FromResult(BuildDomainResult(exceptionInfo, context.Heap, context.Cache).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            return BuildDomainResult(AnalyzeExceptions(heap, runtime, progress: null), heap, cache: null);
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
                ExceptionLohCounts = _exceptionLohCounts!,
                AggregateExceptionCount = _aggregateExceptionCount,
                AggregateInnerExceptionTypeCounts = _aggregateInnerExceptionTypeCounts!,
                ExceptionHeapSizeByType = _exceptionHeapSizeByType!
            };
        }

        private AnalyzerDomainResult BuildDomainResult(ExceptionAnalysis exceptionInfo, ClrHeap heap, IHeapAnalysisCache? cache)
        {
            var candidateSnapshots = BuildCrashThreadSnapshotsImpl(exceptionInfo);

            // Complete type-count dictionaries — no report-width cap here (§9.26 D5); the render
            // layer slices for display.
            var payloadExceptionTypeCounts = new Dictionary<string, int>(exceptionInfo.ExceptionTypeCounts);
            var payloadActiveExceptionTypeCounts = new Dictionary<string, int>(exceptionInfo.ActiveExceptionTypeCounts);
            var payloadAggregateInnerExceptionTypeCounts = new Dictionary<string, int>(exceptionInfo.AggregateInnerExceptionTypeCounts);
            var payloadExceptionHeapSizeByType = new Dictionary<string, ulong>(exceptionInfo.ExceptionHeapSizeByType);

            return new CrashDomainResult(
                exceptionInfo.TotalExceptions,
                exceptionInfo.ActiveExceptions,
                payloadExceptionTypeCounts,
                payloadActiveExceptionTypeCounts,
                candidateSnapshots,
                BuildExceptionInstanceSnapshots(exceptionInfo),
                exceptionInfo.InferredTraceCount,
                exceptionInfo.AggregateExceptionCount,
                payloadAggregateInnerExceptionTypeCounts,
                BuildMessageDistributions(exceptionInfo),
                BuildCrashBuckets(exceptionInfo),
                payloadExceptionHeapSizeByType,
                BuildGen2RetentionPaths(heap, cache, exceptionInfo));
        }

        // Pure candidate selection — Generation >= 2 covers Gen2 plus LOH/Pinned/Frozen/Unknown,
        // the same bucket ExceptionLohCounts already uses. Extracted from BuildGen2RetentionPaths
        // so the selection rule is testable without a live ClrHeap/cache.
        internal static List<ExceptionInstance> SelectGen2RetentionCandidates(ExceptionAnalysis analysis)
        {
            var candidates = new List<ExceptionInstance>();
            foreach (var kvp in analysis.ExceptionsByType)
            {
                List<ExceptionInstance> instances = kvp.Value;
                for (int i = 0; i < instances.Count; i++)
                {
                    if (instances[i].Generation >= 2)
                        candidates.Add(instances[i]);
                }
            }
            return candidates;
        }

        // E-1: retention paths for Gen2/LOH exception instances via the shared
        // RootPathFinder/reverse-edge-index infrastructure (EventLeakAnalyzer.PopulateEvidence is
        // the template this mirrors). Only runs when a cache is available (AnalyzeAsync's
        // pipeline path); the cache-less Analyze(runtime, heap) fallback has no reverse index or
        // root-set cache to query, so it no-ops rather than paying for an unindexed BFS. Bounded
        // by MaxRetentionPathEnrichmentMs — root-path search is real per-object work, unlike the
        // unconditional totals/counts elsewhere in this analyzer.
        internal IReadOnlyList<ExceptionRetentionPath> BuildGen2RetentionPaths(ClrHeap heap, IHeapAnalysisCache? cache, ExceptionAnalysis analysis)
        {
            if (cache is null)
                return [];

            List<ExceptionInstance> candidates = SelectGen2RetentionCandidates(analysis);
            if (candidates.Count == 0)
                return [];

            IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
            var provider = new ReferenceGraph(heap);
            var limits = new RootPathSearchLimits
            {
                MaxCandidateNodes = 5_000,
                MaxCandidateDepth = 8,
                MaxRootExpansionDepth = 12,
                LargeFanoutThreshold = 100,
            };
            var finder = new RootPathFinder(heap, provider, limits, RootPathSearchSupport.NoOpTelemetry,
                RootPathSearchSupport.IsNoisyType, static _ => false, cache.TryGetReverseIndexProvider(), cache);

            var budgetSw = System.Diagnostics.Stopwatch.StartNew();
            long maxMs = Math.Max(0, _options.MaxRetentionPathEnrichmentMs);
            var results = new List<ExceptionRetentionPath>(Math.Min(candidates.Count, 64));

            for (int i = 0; i < candidates.Count; i++)
            {
                if (budgetSw.ElapsedMilliseconds > maxMs)
                    break;

                ExceptionInstance instance = candidates[i];
                bool found = finder.TryFindAnyRootPath(
                    instance.Address, roots, out string? rootKind, out List<ulong>? path,
                    out bool truncated, out _, out _);
                if (!found)
                    continue;

                string formattedPath = RootPathSearchSupport.FormatPath(heap, rootKind!, path, cache);
                results.Add(new ExceptionRetentionPath(instance.Type, instance.Address, rootKind!, formattedPath, truncated));
            }

            return results;
        }

        // Crash bucket / fault signature: (ExceptionType, TopUserFrame) dedup key, same sampled
        // scope as BuildMessageDistributions (ExceptionsByType — capped per type, plus all active
        // instances). Distinguishes a systemic single-site fault (one bucket, high InstanceCount)
        // from scattered independent failures (many buckets, low InstanceCount each) that would
        // otherwise look identical in the plain per-type counts.
        internal static IReadOnlyList<CrashBucket> BuildCrashBuckets(ExceptionAnalysis analysis)
        {
            var buckets = new Dictionary<(string Type, string TopUserFrame), CrashBucketAccumulator>();
            foreach (var kvp in analysis.ExceptionsByType)
            {
                string type = kvp.Key;
                List<ExceptionInstance> instances = kvp.Value;
                for (int i = 0; i < instances.Count; i++)
                {
                    ExceptionInstance instance = instances[i];
                    string topUserFrame = DetermineTopUserFrame(instance.OriginalStackTrace);
                    var key = (type, topUserFrame);
                    if (!buckets.TryGetValue(key, out var acc))
                    {
                        acc = new CrashBucketAccumulator(instance.Address);
                        buckets[key] = acc;
                    }
                    acc.InstanceCount++;
                    if (instance.ThreadId.HasValue)
                        acc.ActiveInstanceCount++;
                }
            }

            var result = new List<CrashBucket>(buckets.Count);
            foreach (var kvp in buckets)
                result.Add(new CrashBucket(kvp.Key.Type, kvp.Key.TopUserFrame, kvp.Value.InstanceCount, kvp.Value.ActiveInstanceCount, kvp.Value.SampleAddress));

            result.Sort(static (a, b) => b.InstanceCount.CompareTo(a.InstanceCount));
            return result;
        }

        // First normalized frame that isn't framework/runtime infrastructure — the real
        // originating call site. Falls back to the top frame (or a sentinel) when the whole
        // captured trace is framework code or there is no trace at all.
        private static string DetermineTopUserFrame(List<string> originalStackTrace)
        {
            for (int i = 0; i < originalStackTrace.Count; i++)
            {
                string normalized = NormalizeFrame(originalStackTrace[i]);
                if (!IsFrameworkFrame(normalized))
                    return normalized;
            }

            return originalStackTrace.Count > 0 ? NormalizeFrame(originalStackTrace[0]) : "(no stack trace)";
        }

        // Message distribution per type — derived from the per-type sampled instance set already
        // held in ExceptionsByType (capped at MaxExceptionsPerType, plus all active instances), not
        // a fresh unconditional per-object Message read across the full heap: distinct message
        // counting and stack-trace/inner-exception extraction are the same class of "genuinely
        // expensive per-object work" that cap exists to bound (see CrashAnalysisOptions.MaxExceptionsPerType).
        internal static IReadOnlyList<ExceptionMessageDistribution> BuildMessageDistributions(ExceptionAnalysis analysis)
        {
            var distributions = new List<ExceptionMessageDistribution>(analysis.ExceptionsByType.Count);
            foreach (var kvp in analysis.ExceptionsByType)
            {
                List<ExceptionInstance> instances = kvp.Value;
                var messageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                var activeMessageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                int sampledCount = 0;

                for (int i = 0; i < instances.Count; i++)
                {
                    ExceptionInstance instance = instances[i];
                    if (string.IsNullOrWhiteSpace(instance.Message))
                        continue;

                    sampledCount++;
                    messageCounts.TryGetValue(instance.Message, out int count);
                    messageCounts[instance.Message] = count + 1;

                    if (instance.ThreadId.HasValue)
                    {
                        activeMessageCounts.TryGetValue(instance.Message, out int activeCount);
                        activeMessageCounts[instance.Message] = activeCount + 1;
                    }
                }

                if (sampledCount == 0)
                    continue;

                string? mostCommonMessage = null;
                int mostCommonMessageCount = 0;
                foreach (var mc in messageCounts)
                {
                    if (mc.Value > mostCommonMessageCount)
                    {
                        mostCommonMessage = mc.Key;
                        mostCommonMessageCount = mc.Value;
                    }
                }

                string? mostCommonActiveMessage = null;
                int mostCommonActiveMessageCount = 0;
                foreach (var mc in activeMessageCounts)
                {
                    if (mc.Value > mostCommonActiveMessageCount)
                    {
                        mostCommonActiveMessage = mc.Key;
                        mostCommonActiveMessageCount = mc.Value;
                    }
                }

                distributions.Add(new ExceptionMessageDistribution(
                    kvp.Key,
                    sampledCount,
                    messageCounts.Count,
                    mostCommonMessage,
                    mostCommonMessageCount,
                    mostCommonActiveMessage,
                    mostCommonActiveMessageCount));
            }

            return distributions;
        }

        internal IReadOnlyList<CrashThreadCandidateSnapshot> BuildCrashThreadSnapshotsImpl(ExceptionAnalysis analysis)
        {
            // Flatten all exception instances once so inference loops are O(N) not O(N*K)
            var allInstances = new List<ExceptionInstance>(capacity: 64);
            foreach (var list in analysis.ExceptionsByType.Values)
                allInstances.AddRange(list);

            int inferredCount = 0;
            // Complete candidate list — bounded by distinct thread count, never heap-scale. No
            // report-width cap here (§9.26 D5); the render layer slices for display.
            int take = analysis.CrashThreadCandidates.Count;
            var snapshots = new List<CrashThreadCandidateSnapshot>(take);

            for (int ci = 0; ci < take; ci++)
            {
                var c = analysis.CrashThreadCandidates[ci];
                List<string>? original = null;
                bool inferred = false;
                string? inferredFrom = null;
                var confidence = InferenceConfidence.None;
                bool sourceIsRethrown = false;

                // Tier 1: candidate already has its own original stack (exact)
                if (c.OriginalExceptionStack != null && c.OriginalExceptionStack.Count > 0)
                {
                    original = NormalizeAll(c.OriginalExceptionStack);
                    confidence = InferenceConfidence.Exact;
                    sourceIsRethrown = c.OriginalExceptionStackIsRethrown;
                }

                // Tier 2: match by ThreadId
                if (original == null)
                {
                    for (int i = 0; i < allInstances.Count; i++)
                    {
                        var e = allInstances[i];
                        if (e.ThreadId.HasValue && e.ThreadId.Value == c.ThreadId && e.OriginalStackTrace.Count > 0)
                        {
                            original = NormalizeAll(e.OriginalStackTrace);
                            inferred = true;
                            inferredFrom = $"0x{e.Address:X} ({e.Type})";
                            confidence = InferenceConfidence.ThreadId;
                            sourceIsRethrown = e.IsRethrown;
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
                            original = NormalizeAll(e.OriginalStackTrace);
                            inferred = true;
                            inferredFrom = $"0x{e.Address:X} ({e.Type})";
                            confidence = InferenceConfidence.MessageHResult;
                            sourceIsRethrown = e.IsRethrown;
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
                            original = NormalizeAll(e.OriginalStackTrace);
                            inferred = true;
                            inferredFrom = $"0x{e.Address:X} ({e.Type})";
                            confidence = InferenceConfidence.TypeInnerType;
                            sourceIsRethrown = e.IsRethrown;
                            break;
                        }
                    }
                }

                if (inferred) inferredCount++;

                // A rethrown source's top frames are the rethrow site, not the original throw
                // site — lower confidence one tier to reflect that, regardless of which tier
                // matched (even an Exact match is less trustworthy if the trace itself is stale).
                if (sourceIsRethrown)
                    confidence = DowngradeForRethrow(confidence);

                // Build full frames list without LINQ — no report-width cap here (§9.26 D5). While
                // we still hold the raw ClrStackFrame (not yet collapsed to text), resolve the
                // owning module of the first user-code frame directly via
                // Method.Type.Module — no ModuleDomainResult cross-reference needed, and more
                // accurate than the string-prefix heuristic used for FrameworkCode/ThirdParty/
                // UserCode classification elsewhere in this report.
                var topFrames = new List<string>(c.CurrentThreadStack.Count);
                string? topUserFrameModule = null;
                bool foundUserFrame = false;
                for (int f = 0; f < c.CurrentThreadStack.Count; f++)
                {
                    var frame = c.CurrentThreadStack[f];
                    string normalizedFrame = NormalizeFrame(frame.Method?.Signature ?? frame.FrameName ?? frame.ToString() ?? StringConstants.UnknownType);
                    topFrames.Add(normalizedFrame);

                    if (!foundUserFrame && !IsFrameworkFrame(normalizedFrame))
                    {
                        topUserFrameModule = frame.Method?.Type?.Module?.Name;
                        foundUserFrame = true;
                    }
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
                    confidence,
                    sourceIsRethrown,
                    topUserFrameModule));
            }

            analysis.InferredTraceCount = inferredCount;
            return snapshots;
        }

        // Normalize every frame (strips "at " prefix, simplifies async names) — no report-width
        // cap here (§9.26 D5); the render layer slices for display.
        // One tier worse, in the Exact→ThreadId→MessageHResult→TypeInnerType→None quality order.
        // TypeInnerType is already the lowest non-None tier, so it stays put rather than
        // collapsing all the way to None — the match itself is still valid, just the frame text
        // it points to is a rethrow site rather than the original throw site.
        private static InferenceConfidence DowngradeForRethrow(InferenceConfidence confidence) => confidence switch
        {
            InferenceConfidence.Exact => InferenceConfidence.ThreadId,
            InferenceConfidence.ThreadId => InferenceConfidence.MessageHResult,
            InferenceConfidence.MessageHResult => InferenceConfidence.TypeInnerType,
            _ => confidence,
        };

        private static List<string> NormalizeAll(List<string> frames)
        {
            var result = new List<string>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
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
        //
        // OPT (docs/cache/cache-architecture.md Phase 6): mt is already a parameter here,
        // so both IsException and Name resolve directly via heap.GetTypeByMethodTable(mt) — no
        // heap.GetObject/exceptionAddress re-read needed at all. This also removes the old
        // sample-address fallback path, which required a HeapIndexBuildResult that both call sites
        // below always pass as null (dead code), and the redundant re-resolution of exceptionAddress
        // the audit flagged (previously re-read up to 3x per exception across the two branches).
        private static (bool IsException, bool IsAggregateException, string? TypeName) ResolveExceptionType(
            ClrHeap heap,
            ulong mt,
            IDictionary<ulong, bool> exceptionMethodTables,
            IDictionary<ulong, bool> aggregateExceptionMethodTables,
            IDictionary<ulong, string> methodTableNameCache)
        {
            if (mt == 0)
                return (false, false, null);

            if (!exceptionMethodTables.TryGetValue(mt, out bool isException))
            {
                isException = heap.GetTypeByMethodTable(mt)?.IsException == true;
                exceptionMethodTables[mt] = isException;
            }

            if (!isException)
                return (false, false, null);

            if (!aggregateExceptionMethodTables.TryGetValue(mt, out bool isAggregate))
            {
                isAggregate = IsAggregateExceptionType(heap.GetTypeByMethodTable(mt));
                aggregateExceptionMethodTables[mt] = isAggregate;
            }

            if (!methodTableNameCache.TryGetValue(mt, out string? typeName))
            {
                string resolved = heap.GetTypeByMethodTable(mt)?.Name ?? string.Empty;
                methodTableNameCache[mt] = resolved;
                typeName = resolved;
            }

            return (true, isAggregate, typeName);
        }

        // Walks the base-type chain so AggregateException subclasses (rare, but legal — e.g.
        // custom wrapper exceptions) are still unwrapped, not just the sealed-in-practice BCL type.
        private static bool IsAggregateExceptionType(ClrType? type)
        {
            while (type != null)
            {
                if (type.Name == "System.AggregateException")
                    return true;
                type = type.BaseType;
            }
            return false;
        }

        // Unwraps AggregateException.InnerExceptions (backing field "_innerExceptions", a direct
        // Exception[] on .NET Core/5+). Returns the full, uncapped set of inner exception type
        // names — callers decide whether to cap for display; global tallies must stay exact.
        private List<string>? ExtractAggregateInnerExceptionTypes(ClrHeap heap, ulong exceptionAddress)
        {
            try
            {
                ClrObject exceptionObj = heap.GetObject(exceptionAddress);
                if (!exceptionObj.IsValid || exceptionObj.Type is null)
                    return null;

                var innerExceptionsField = exceptionObj.Type.GetFieldByName("_innerExceptions");
                if (innerExceptionsField is null)
                    return null;

                ClrObject arrayObj = innerExceptionsField.ReadObject(exceptionObj, interior: false);
                if (!arrayObj.IsValid || !arrayObj.IsArray)
                    return null;

                ClrArray array = arrayObj.AsArray();
                if (array.Length <= 0)
                    return null;

                var types = new List<string>(array.Length);
                for (int i = 0; i < array.Length; i++)
                {
                    ClrObject inner = array.GetObjectValue(i);
                    if (inner.IsValid && inner.Type != null)
                        types.Add(inner.Type.Name ?? StringConstants.UnknownType);
                }
                return types.Count > 0 ? types : null;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error extracting AggregateException inner exceptions from 0x{Address:X}", exceptionAddress);
                return null;
            }
        }

        // Render-layer-only bound on the per-instance inner-type list embedded in one
        // ExceptionInstance (§9.26 D5 does not apply — this caps a single field's payload size for
        // report legibility, not the exact AggregateInnerExceptionTypeCounts totals).
        private const int MaxDisplayedAggregateInnerExceptionTypes = 25;

        private static List<string> CapForDisplay(List<string> types) =>
            types.Count > MaxDisplayedAggregateInnerExceptionTypes
                ? types.GetRange(0, MaxDisplayedAggregateInnerExceptionTypes)
                : types;

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

            // Complete list — already bounded by MaxExceptionsPerType upstream, never heap-scale.
            // No further report-width cap here (§9.26 D5); the render layer slices for display.
            int limit = flat.Count;
            var snapshots = new List<ExceptionInstanceSnapshot>(limit);

            for (int i = 0; i < limit; i++)
            {
                var (typeName, inst) = flat[i];

                List<string>? threadFrames = null;
                if (inst.CurrentThreadStack.Count > 0)
                {
                    threadFrames = new List<string>(inst.CurrentThreadStack.Count);
                    for (int f = 0; f < inst.CurrentThreadStack.Count; f++)
                    {
                        var fr = inst.CurrentThreadStack[f];
                        threadFrames.Add(NormalizeFrame(fr.Method?.Signature ?? fr.FrameName ?? fr.ToString() ?? StringConstants.UnknownType));
                    }
                }

                List<string>? origFrames = null;
                if (inst.OriginalStackTrace.Count > 0)
                    origFrames = NormalizeAll(inst.OriginalStackTrace);

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
                    origFrames,
                    inst.AggregateInnerExceptionTypes,
                    inst.IsRethrown));
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
            var aggregateExceptionMethodTables = new ConcurrentDictionary<ulong, bool>();
            var methodTableNameCache = new ConcurrentDictionary<ulong, string>();
            var exceptionTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var activeExceptionTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionInstances = new ConcurrentBag<(string TypeName, ExceptionInstance Instance, bool IsActive)>();
            var crashThreadCandidates = new ConcurrentDictionary<uint, CrashThreadCandidate>();
            var exceptionGen0Counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionGen1Counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionGen2Counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionLohCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var aggregateInnerExceptionTypeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var exceptionHeapSizeByType = new ConcurrentDictionary<string, ulong>(StringComparer.Ordinal);
            var candidateLock = new object();
            int totalExceptions = 0, activeExceptionsCount = 0, aggregateExceptionCount = 0;
            var scanCounter = new DumpDetective.Analysis.Cache.ObjectScanCounter("scanning for exceptions", progress, reportEveryObjects: 50_000, reportEveryElapsed: TimeSpan.FromSeconds(2));

            void ProcessEntry(ulong exceptionAddress, ulong mt, ulong size)
            {
                scanCounter.Tick();

                if (exceptionAddress == 0)
                    return;
                var (isException, isAggregate, typeName) = ResolveExceptionType(heap, mt, exceptionMethodTables, aggregateExceptionMethodTables, methodTableNameCache);
                if (!isException)
                    return;

                string key = typeName ?? StringConstants.UnknownType;
                Interlocked.Increment(ref totalExceptions);
                exceptionTypeCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
                exceptionHeapSizeByType.AddOrUpdate(key, size, (_, existing) => existing + size);

                List<string>? aggregateInnerTypes = null;
                if (isAggregate)
                {
                    aggregateInnerTypes = ExtractAggregateInnerExceptionTypes(heap, exceptionAddress);
                    if (aggregateInnerTypes != null)
                    {
                        Interlocked.Increment(ref aggregateExceptionCount);
                        for (int i = 0; i < aggregateInnerTypes.Count; i++)
                            aggregateInnerExceptionTypeCounts.AddOrUpdate(aggregateInnerTypes[i], 1, (_, c) => c + 1);
                    }
                }

                int resolvedGeneration = -1;
                try
                {
                    var seg = heap.GetSegmentByAddress(exceptionAddress);
                    if (seg != null)
                    {
                        resolvedGeneration = (int)seg.GetGeneration(exceptionAddress);
                        switch (resolvedGeneration)
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
                exceptionInstance.Generation = resolvedGeneration;
                if (aggregateInnerTypes != null)
                    exceptionInstance.AggregateInnerExceptionTypes = CapForDisplay(aggregateInnerTypes);

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
                            candidate.OriginalExceptionStackIsRethrown = exceptionInstance.IsRethrown;
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
                    ProcessEntry(obj.Address, mt, obj.Size);
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
                ExceptionLohCounts = new Dictionary<string, int>(exceptionLohCounts, StringComparer.Ordinal),
                AggregateExceptionCount = aggregateExceptionCount,
                AggregateInnerExceptionTypeCounts = new Dictionary<string, int>(aggregateInnerExceptionTypeCounts, StringComparer.Ordinal),
                ExceptionHeapSizeByType = new Dictionary<string, ulong>(exceptionHeapSizeByType, StringComparer.Ordinal)
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

                // Full stack — no artificial per-thread frame cap (§9.26). Only threads with an
                // active exception reach here, never heap-scale.
                lookup[thread.CurrentException.Address] = new ActiveExceptionContext
                {
                    ThreadId = (uint)thread.ManagedThreadId,
                    OSThreadId = thread.OSThreadId,
                    CurrentThreadStack = thread.EnumerateStackTrace().ToList()
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

            // OPT (docs/cache/cache-architecture.md Phase 6): entry.MethodTable is already
            // known — resolve via the metadata cache instead of materializing a ClrObject.
            isException = heap.GetTypeByMethodTable(entry.MethodTable)?.IsException == true;
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
                (instance.OriginalStackTrace, instance.IsRethrown) = ExtractExceptionStackTrace(heap, exceptionAddress);

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

        private (List<string> Frames, bool IsRethrown) ExtractExceptionStackTrace(ClrHeap heap, ulong exceptionAddress)
        {
            var stackFrames = new List<string>();

            ClrObject exceptionObj = heap.GetObject(exceptionAddress);
            if (!exceptionObj.IsValid || exceptionObj.Type == null)
                return (stackFrames, false);

            bool isRethrown = false;
            string? remoteStack = null;

            try
            {
                // _remoteStackTraceString presence means the exception was rethrown via `throw;`
                // or ExceptionDispatchInfo.Throw() — checked independent of whether it ends up
                // supplying the frame text below, since _stackTraceString is often already
                // populated even when the exception has been rethrown.
                var remoteStackField = exceptionObj.Type?.GetFieldByName("_remoteStackTraceString");
                if (remoteStackField != null)
                {
                    var remoteStackObj = remoteStackField.ReadObject(exceptionObj, interior: false);
                    if (remoteStackObj.IsValid)
                    {
                        remoteStack = remoteStackObj.AsString();
                        isRethrown = !string.IsNullOrEmpty(remoteStack);
                    }
                }

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
                            return (stackFrames, isRethrown);
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
                            return (stackFrames, isRethrown);
                    }
                }

                // If still no stack, fall back to the remote stack text already read above
                if (stackFrames.Count == 0 && !string.IsNullOrEmpty(remoteStack))
                {
                    stackFrames.Add(remoteStack);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error extracting stack trace from exception at 0x{Address:X}", exceptionAddress);
            }

            return (stackFrames, isRethrown);
        }

        public void Dispose() { }

    }

}



