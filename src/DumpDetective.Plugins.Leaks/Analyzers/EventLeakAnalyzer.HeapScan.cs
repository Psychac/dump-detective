using DumpDetective.Analysis.Analyzers.Leaks.EventLeak;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Runs the retyped <see cref="EventLeakAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Bespoke, serial <see cref="IHeapIndexScanParticipant"/> (not
/// <c>IParallelHeapIndexScanParticipant</c> -- the pre-retyping analyzer never parallelized this
/// scan either) -- carries essentially the entire pre-retyping analyzer's scan and
/// detail-building logic unchanged, dump-side. Its hot path (<c>EventLeakFastScanner</c>)
/// deliberately reads delegate fields via raw <c>IMemoryReader.ReadPointer(address + offset, ...)</c>
/// rather than named-field lookups, specifically to avoid ClrMD overhead -- the source's own
/// history documents a real regression (a scope-widening mistake once pushed
/// <c>PublisherRegistry.Build</c> from ~22.8s to ~48s on the 3.3GB reference dump; that build is
/// documented as ~57% of wall time on a 25.6GB dump). Retargeting this path onto the Tier-2
/// <c>IDumpObjectFieldQuery.TryReadField</c> (named-field, per-call <c>ClrType.GetFieldByName</c>
/// resolution) would very likely reintroduce exactly that class of regression this project has
/// already hit and fixed twice (<c>BoxingAnalyzer</c>, <c>CollectionAnalyzer</c>) -- so this batch
/// does not attempt it.
///
/// The retyped inner <see cref="EventLeakAnalyzer"/> receives this adapter's finished
/// <c>List&lt;EventGroupInfo&gt;</c> (already-resolved subscriber type/method names, disposal
/// state, root hints -- exactly what the pre-retyping analyzer produced) plus scalar scan counts
/// via <see cref="EventLeakAnalyzerOptions"/>, and does only the capability-light tail:
/// significance filtering, severity-ranked snapshot assembly, retained-bytes estimation (via
/// <c>IHeapTypeStatisticsQuery</c>/<c>IHeapDominatorQuery</c>), and root-path evidence (via
/// <c>IHeapRootPathQuery</c>) -- the same "adapter accumulates real data during the pipeline's
/// shared scan, hands its inner analyzer the finished result" shape
/// <c>HangAnalyzerLegacyAdapter</c>/<c>StringAnalyzerLegacyAdapter</c> already established.
///
/// <b>Dropped, not ported</b>: <c>HasDelegateFields(ClrType)</c>, <c>MergeAccumulatorEntry</c>, and
/// <c>IsFieldBackedDelegate</c> -- all three were dead code with zero callers anywhere in the
/// codebase (confirmed by search, not assumed) even before this retyping. <c>AddFindings</c>
/// (an <c>InsightFinding</c> builder) was likewise dead -- defined but never invoked from
/// <c>AnalyzeAsync</c> or anywhere else.
/// </remarks>
public sealed partial class EventLeakAnalyzer : IHeapIndexScanParticipant, IRequiresReachableGraphIndex, IRequiresDominatorTreeIndex, ICustomizesAnalysisContext
{
    private readonly ILogger<EventLeakAnalyzer>? _logger;

    public EventLeakAnalyzer() : this(null)
    {
    }

    public EventLeakAnalyzer(ILogger<EventLeakAnalyzer>? logger)
    {
        _logger = logger;
    }

        // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
        // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
        // OnHeapEntry; consumed by FindEventLeaks once the shared index scan has completed.
        private EventLeakFastScanner? _participantFastScanner;
        private PublisherRegistry? _participantRegistry;
        private Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator>? _participantGroupAcc;
        // P2-2 (docs/analysis/phase1/eventleak-analyzer-audit.md): distinct publisher MTs that
        // produced at least one accepted leak, tracked by AddToAccumulator — compared against
        // PublisherRegistry.CandidatePublisherCount to report "types scanned, zero leaking".
        private HashSet<ulong>? _participantLeakingMTs;
        private Dictionary<ulong, string>? _participantRootHints;
        private IReadOnlyList<ClrAppDomain>? _participantAppDomains;
        private List<(ulong addr, ulong mt, ulong delegateAddr)>? _participantBuf;
        private EventLeakOptions? _participantOptions;
        private int _participantEventsScanned;
        private int _participantPublisherInstances;
        // Set by OnHeapIndexScanCompleted — the single source of truth for whether the
        // participant-accumulated state above is trustworthy. Avoids re-deriving "did the
        // shared scan run" from a second cache.TryGetHeapIndex call in FindEventLeaks.
        private bool _participantScanSucceeded;

        // Resets per-entry accumulator fields ahead of the shared heap-index scan pass.
        // Explicit interface implementations: EventLeakAnalyzer is public but HeapEntry is
        // internal, so these members must not be exposed on the public API surface.
        void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context)
        {
            _participantScanSucceeded = false;
            _participantOptions = context.AnalysisOptions.EventLeak;
            _participantGroupAcc = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator>();
            _participantLeakingMTs = new HashSet<ulong>();
            _participantAppDomains = context.Heap.Runtime.AppDomains;
            context.ReportSubPhase?.Invoke("building root hint map");
            var __rootHintSw = System.Diagnostics.Stopwatch.StartNew();
            _participantRootHints = BuildRootHintMap(context.Heap, context.Cache);
            _logger?.LogDebug("EventLeakAnalyzer.BuildRootHintMap: {ElapsedSeconds:F2}s", __rootHintSw.Elapsed.TotalSeconds);
            context.ReportSubPhase?.Invoke("building publisher registry");
            var __registrySw = System.Diagnostics.Stopwatch.StartNew();
            // No CancellationToken to thread through here — IHeapIndexScanParticipant.BeforeHeapIndexScan
            // doesn't carry one (AnalysisContext has none either); widening that shared interface for
            // every participant is out of scope for this fix. The FindEventLeaks call site below does
            // have one and passes it through.
            _participantRegistry = PublisherRegistry.Build(context.Heap, context.Cache);
            _logger?.LogDebug("EventLeakAnalyzer.PublisherRegistry.Build: {ElapsedSeconds:F2}s", __registrySw.Elapsed.TotalSeconds);
            var __fastScannerSw = System.Diagnostics.Stopwatch.StartNew();
            _participantFastScanner = new EventLeakFastScanner(context.Heap, _participantRegistry, context.Progress);
            _logger?.LogDebug("EventLeakAnalyzer.new EventLeakFastScanner: {ElapsedSeconds:F2}s", __fastScannerSw.Elapsed.TotalSeconds);

            _participantBuf = new List<(ulong addr, ulong mt, ulong delegateAddr)>(capacity: 64);
            _participantEventsScanned = 0;
            _participantPublisherInstances = 0;
        }

        void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
        {
            if (_participantFastScanner is null)
                return;

            _participantFastScanner.ScanEntry(
                in entry, _participantBuf!, _participantGroupAcc!, _participantRootHints!,
                _participantOptions!, _participantLeakingMTs!, ref _participantEventsScanned, ref _participantPublisherInstances);
        }

        void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded)
        {
            _participantScanSucceeded = succeeded;
            if (_participantFastScanner is not null)
            {
                double processMs = _participantFastScanner.GetScanTimings();
                _logger?.LogDebug(
                    "EventLeakFastScanner.ProcessPublisherEntry (per-object): {ProcessSeconds:F2}s",
                    processMs / 1000.0);
            }
        }

        // internal so EventLeakFastScanner can reference the type without reflection.
        // P3-4 (docs/analysis/phase1/eventleak-analyzer-audit.md): fixed, ordered subscriber-count
        // buckets for the cross-group distribution histogram — distinguishes "one giant leaking
        // publisher" from "many small leaks adding up". Small and allocation-free per group
        // (plain int[] indexed by bucket, not a Dictionary); GetSubscriberCountBucketIndex is an
        // O(BucketCount) linear scan over 8 entries, the only per-leak cost.
        internal static readonly (string Label, int MaxInclusive)[] SubscriberCountHistogramBuckets =
        [
            ("1", 1),
            ("2", 2),
            ("3-5", 5),
            ("6-10", 10),
            ("11-25", 25),
            ("26-50", 50),
            ("51-100", 100),
            ("101+", int.MaxValue)
        ];

        internal static int GetSubscriberCountBucketIndex(int subscriberCount)
        {
            for (int i = 0; i < SubscriberCountHistogramBuckets.Length; i++)
                if (subscriberCount <= SubscriberCountHistogramBuckets[i].MaxInclusive)
                    return i;
            return SubscriberCountHistogramBuckets.Length - 1;
        }

        internal class GroupAccumulator
        {
            public int InstanceCount;
            public int TotalSubscribers;
            public int MinSubscribers = int.MaxValue;
            public int MaxSubscribers = 0;
            public int MaxSeverity = 0;
            public List<EventLeakInfo> TopInstances = new List<EventLeakInfo>();
            // Subscriber type counts aggregated across ALL instances (not just TopInstances).
            // TopInstances is capped at TopDetailedInstancesPerGroup; without this dict the
            // per-type breakdown in the report only reflects those few stored instances.
            public Dictionary<string, int> AllSubscriberTypeCounts = new(StringComparer.Ordinal);
            // Handler-method counts aggregated across ALL instances (design §7 / Phase 5 correlation).
            // Populated in the same loop as AllSubscriberTypeCounts — MethodName is already resident
            // on SubscriberInfo, just discarded there.
            public Dictionary<(string Type, string? MethodName), int> AllSubscriberMethodCounts = new();
            // P3-4: subscriber-count histogram, aggregated across ALL instances (not just TopInstances)
            // for the same reason as the two dictionaries above.
            public int[] SubscriberCountBuckets = new int[SubscriberCountHistogramBuckets.Length];
        }

        // internal so EventLeakFastScanner can call without reflection.
        // leakingMTs is the single choke point every accepted leak (instance or static) passes
        // through, so it's the natural place to mark a publisher MT as "leaking" for P2-2's
        // clean-vs-leaking count (docs/analysis/phase1/eventleak-analyzer-audit.md). Optional so
        // callers that don't care about that count (tests) don't need to thread a set through.
        internal static void AddToAccumulator(
            Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator> acc,
            EventLeakInfo leak,
            int capacity,
            HashSet<ulong>? leakingMTs = null)
        {
            leakingMTs?.Add(leak.PublisherMethodTable);

            var key = (leak.PublisherType, leak.EventFieldName, leak.IsStatic);
            if (!acc.TryGetValue(key, out var a))
            {
                a = new GroupAccumulator();
                acc[key] = a;
            }

            a.InstanceCount++;
            a.TotalSubscribers += leak.SubscriberCount;
            if (leak.SubscriberCount < a.MinSubscribers) a.MinSubscribers = leak.SubscriberCount;
            if (leak.SubscriberCount > a.MaxSubscribers) a.MaxSubscribers = leak.SubscriberCount;
            if (leak.SeverityScore > a.MaxSeverity) a.MaxSeverity = leak.SeverityScore;
            a.SubscriberCountBuckets[GetSubscriberCountBucketIndex(leak.SubscriberCount)]++;

            // Accumulate subscriber type counts from ALL instances so the report type-breakdown
            // reflects the full population, not just the top-N stored instances.
            foreach (SubscriberInfo s in leak.Subscribers)
            {
                a.AllSubscriberTypeCounts.TryGetValue(s.Type, out int typeCount);
                a.AllSubscriberTypeCounts[s.Type] = typeCount + 1;

                var methodKey = (s.Type, s.MethodName);
                a.AllSubscriberMethodCounts.TryGetValue(methodKey, out int methodCount);
                a.AllSubscriberMethodCounts[methodKey] = methodCount + 1;
            }

            var list = a.TopInstances;
            if (capacity <= 0) capacity = 1;
            if (list.Count < capacity)
            {
                list.Add(leak);
                return;
            }

            // Replace smallest subscriber-count instance if this one is larger
            int minIdx = 0;
            int minVal = list[0].SubscriberCount;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].SubscriberCount < minVal)
                {
                    minVal = list[i].SubscriberCount;
                    minIdx = i;
                }
            }
            if (leak.SubscriberCount > minVal)
            {
                list[minIdx] = leak;
            }
        }

        /// <summary>
        /// Converts the heap's ClrObject stream into <see cref="HeapEntry"/> structs
        /// so the fast scanner can consume a uniform entry format even without a pre-built index.
        /// </summary>
        private static IEnumerable<HeapEntry> StreamObjectsAsEntries(ClrHeap heap)
        {
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;
                ulong mt = obj.Type.MethodTable;
                if (mt == 0) continue;
                yield return new HeapEntry(obj.Address, mt, obj.Size);
            }
        }

        private List<EventGroupInfo> FindEventLeaks(ClrHeap heap, IHeapAnalysisCache? cache, EventLeakOptions options, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken, out int eventsScanned, out int publisherInstances, out int candidatePublisherTypeCount, out int leakingPublisherTypeCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            eventsScanned = 0;
            publisherInstances = 0;
            var appDomains = heap.Runtime.AppDomains;
            var scanCounter = new ObjectScanCounter("scanning event handlers", progress);

            Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator> groupAcc;
            Dictionary<ulong, string> rootHints;
            PublisherRegistry registry;
            // P2-2 (docs/analysis/phase1/eventleak-analyzer-audit.md): distinct publisher MTs
            // marked by AddToAccumulator as having produced at least one accepted leak — see
            // that method's leakingMTs parameter for where entries are added.
            HashSet<ulong> leakingMTs;

            // BeforeHeapIndexScan/OnHeapEntry already ran via the pipeline's
            // HeapIndexScanDispatcher before AnalyzeAsync executes when a disk-backed heap
            // index exists. Reuse that participant-accumulated state instead of re-scanning
            // the index. If the shared scan failed partway (isolated by the dispatcher),
            // fall back to a fresh scan instead of trusting partial state.
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out _)
                && _participantScanSucceeded && _participantGroupAcc is not null)
            {
                groupAcc = _participantGroupAcc;
                rootHints = _participantRootHints!;
                registry = _participantRegistry!;
                leakingMTs = _participantLeakingMTs!;
                eventsScanned = _participantEventsScanned;
                publisherInstances = _participantPublisherInstances;
            }
            else
            {
                groupAcc = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator>();
                leakingMTs = new HashSet<ulong>();
                var __phaseSw = System.Diagnostics.Stopwatch.StartNew();
                rootHints = BuildRootHintMap(heap, cache);
                _logger?.LogDebug("FindEventLeaks.BuildRootHintMap: {ElapsedSeconds:F2}s ({RootCount} roots)", __phaseSw.Elapsed.TotalSeconds, rootHints.Count);

                __phaseSw.Restart();
                registry = PublisherRegistry.Build(heap, cache, cancellationToken: cancellationToken);
                _logger?.LogDebug("FindEventLeaks.PublisherRegistry.Build: {ElapsedSeconds:F2}s", __phaseSw.Elapsed.TotalSeconds);

                // ── Fast scanner: direct IMemoryReader.ReadPointer — no heap.GetObject ────
                __phaseSw.Restart();
                var fastScanner = new EventLeakFastScanner(heap, registry, progress);
                _logger?.LogDebug("FindEventLeaks.new EventLeakFastScanner: {ElapsedSeconds:F2}s", __phaseSw.Elapsed.TotalSeconds);

                IEnumerable<HeapEntry> streamingEntries = cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out _)
                    ? hc.EnumerateIndexedEntries()
                    : StreamObjectsAsEntries(heap);

                __phaseSw.Restart();
                fastScanner.Scan(streamingEntries, groupAcc, rootHints, options, leakingMTs,
                    ref eventsScanned, ref publisherInstances);
                double __scanWall = __phaseSw.Elapsed.TotalSeconds;

                double freshProcessMs = fastScanner.GetScanTimings();
                _logger?.LogDebug(
                    "FindEventLeaks.Scan WALL: {ScanWallSeconds:F2}s  (ProcessPublisherEntry {ProcessSeconds:F2}s + unattributed {UnattributedSeconds:F2}s)",
                    __scanWall, freshProcessMs / 1000.0, __scanWall - freshProcessMs / 1000.0);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Statics run exactly once here — the single registry-driven sweep (design §6).
            // No longer processed on the hot path, so there is nothing left to dedup against.
            var __sweepSw = System.Diagnostics.Stopwatch.StartNew();
            SweepRegistryStatics(heap, appDomains, rootHints, options, registry,
                leak => AddToAccumulator(groupAcc, leak, options.TopDetailedInstancesPerGroup, leakingMTs), ref eventsScanned, cancellationToken, progress);
            _logger?.LogDebug("FindEventLeaks.SweepRegistryStatics: {ElapsedSeconds:F2}s", __sweepSw.Elapsed.TotalSeconds);

            candidatePublisherTypeCount = registry.CandidatePublisherCount;
            leakingPublisherTypeCount = leakingMTs.Count;

            scanCounter.Complete();

            // Convert accumulators into EventGroupInfo list
            __sweepSw.Restart();
            var result = new List<EventGroupInfo>(groupAcc.Count);
            foreach (var kvp in groupAcc)
            {
                var key = kvp.Key;
                var acc = kvp.Value;
                int instanceCount = acc.InstanceCount;
                int totalSubs = acc.TotalSubscribers;
                int minSubs = acc.MinSubscribers == int.MaxValue ? 0 : acc.MinSubscribers;
                int maxSubs = acc.MaxSubscribers;
                int maxSeverity = acc.MaxSeverity;

                // sort top instances by severity desc then subscriber count
                acc.TopInstances.Sort((a, b) =>
                {
                    int cmp = b.SeverityScore.CompareTo(a.SeverityScore);
                    return cmp != 0 ? cmp : b.SubscriberCount.CompareTo(a.SubscriberCount);
                });

                result.Add(new EventGroupInfo
                {
                    PublisherType = key.PublisherType,
                    EventFieldName = key.EventFieldName,
                    IsStatic = key.IsStatic,
                    SeverityScore = maxSeverity,
                    InstanceCount = instanceCount,
                    TotalSubscribers = totalSubs,
                    AverageSubscribers = instanceCount == 0 ? 0.0 : (double)totalSubs / instanceCount,
                    MaxSubscribers = maxSubs,
                    MinSubscribers = minSubs,
                    Instances = acc.TopInstances,
                    AllSubscriberTypeCounts = acc.AllSubscriberTypeCounts,
                    AllSubscriberMethodCounts = acc.AllSubscriberMethodCounts,
                    SubscriberCountBuckets = acc.SubscriberCountBuckets
                });
            }

            // Sort by total subscribers
            result.Sort((a, b) => b.TotalSubscribers.CompareTo(a.TotalSubscribers));
            _logger?.LogDebug("FindEventLeaks.BuildGroupInfos+Sort: {ElapsedSeconds:F2}s ({GroupCount} groups)", __sweepSw.Elapsed.TotalSeconds, result.Count);

            return result;
        }

        // internal so EventLeakFastScanner can call it directly.
        internal static List<SubscriberInfo> GetStaticEventSubscribers(ClrHeap heap, ClrStaticField field, IReadOnlyList<ClrAppDomain> appDomains)
        {
            var subscribers = new List<SubscriberInfo>();
            // Deduplicate at the DELEGATE-OBJECT level only: it is theoretically possible for two
            // app domains to read the same delegate instance from a shared static field, in which
            // case enumerating both would double-count its subscriptions.
            // We do NOT deduplicate subscriber addresses across domains. In a multi-domain process
            // each app domain owns its own copy of the static delegate chain. The same subscriber
            // object (same heap address) subscribed in domain 1 AND domain 2 represents two
            // independent GC retention paths — both must be counted. The previous per-subscriber
            // deduplication was collapsing ~6 domains × N subscriptions down to just N, explaining
            // the ~6× undercount vs tools that count per-domain subscriptions correctly.
            var seenDelegateAddresses = new HashSet<ulong>(capacity: appDomains.Count);

            foreach (var appDomain in appDomains)
            {
                try
                {
                    ClrObject eventDelegate = field.ReadObject(appDomain);
                    if (!eventDelegate.IsValid)
                        continue;

                    // Heuristic filters for static fields as well.
                    if (eventDelegate.Type != null && IsNoiseType(eventDelegate.Type.Name ?? string.Empty))
                        continue;

                    // NOTE: do NOT re-apply LooksLikeEventField here.
                    // All callers (SweepRegistryStatics) already validated the field name before
                    // calling this method. Re-filtering here silently drops fields whose names are
                    // in the type's event set (e.g. "DataReceived", "Completed") but don't match
                    // the heuristic patterns.

                    // Skip if this exact delegate object was already processed from another domain.
                    if (!seenDelegateAddresses.Add(eventDelegate.Address))
                        continue;

                    var domainSubscribers = ExtractSubscribersFromDelegateAddress(heap, eventDelegate.Address);
                    foreach (var subscriber in domainSubscribers)
                    {
                        if (subscriber.Address != 0)
                            subscribers.Add(subscriber);
                    }
                }
                catch
                {
                }
            }

            return subscribers;
        }

        // internal so EventLeakFastScanner can call it directly.
        internal static EventLeakInfo CreateLeakInfo(
            ulong publisherAddress,
            string publisherType,
            string eventFieldName,
            bool isStatic,
            List<SubscriberInfo> subscribers,
            Dictionary<ulong, string> rootHints,
            EventLeakOptions options,
            ClrHeap? heap = null,
            string? preferredRootHint = null,
            int publisherGeneration = -1,
            bool hasLifetimeMismatch = false,
            Dictionary<ulong, bool>? disposableTypeCache = null,
            ulong publisherMethodTable = 0)
        {
            string rootHint = preferredRootHint ?? string.Empty;

            if (string.IsNullOrEmpty(rootHint))
            {
                foreach (var subscriber in subscribers)
                {
                    if (rootHints.TryGetValue(subscriber.Address, out var hint))
                    {
                        rootHint = hint;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(rootHint) && publisherAddress != 0 && rootHints.TryGetValue(publisherAddress, out var publisherHint))
            {
                rootHint = publisherHint;
            }

            int duplicateCount = CountDuplicateSubscriptions(subscribers);
            bool isDisposedButSubscribed = heap != null && disposableTypeCache != null
                && HasDisposableSubscriber(heap, subscribers, disposableTypeCache);

            bool hasLowIncoming = false;
            // PERF GUARD: heap-scan per subscriber is O(N×M) on large dumps.
            // Only run when explicitly opted-in via options.
            if (heap != null && options.EnableLowIncomingRefsCheck)
            {
                try { hasLowIncoming = HasLowIncomingRefsSignal(heap, subscribers, options); }
                catch { hasLowIncoming = false; }
            }

            return new EventLeakInfo
            {
                PublisherAddress = publisherAddress,
                PublisherMethodTable = publisherMethodTable,
                PublisherType = publisherType,
                EventFieldName = eventFieldName,
                IsStatic = isStatic,
                SubscriberCount = subscribers.Count,
                Subscribers = subscribers,
                RootHint = rootHint,
                PublisherGeneration = publisherGeneration,
                DuplicateSubscriptionCount = duplicateCount,
                IsDisposedButSubscribed = isDisposedButSubscribed,
                HasLifetimeMismatch = hasLifetimeMismatch,
                HasLowIncomingRefs = hasLowIncoming,
                SeverityScore = CalculateSeverity(isStatic, subscribers.Count, rootHint, options,
                    publisherGeneration, duplicateCount, isDisposedButSubscribed, hasLifetimeMismatch, hasLowIncoming)
            };
        }

        internal static bool IsLikelyPublisher(EventLeakInfo leak, EventLeakOptions options)
        {
            if (leak == null) return false;

            // Must meet subscriber threshold
            if (leak.SubscriberCount < options.PublisherSubscriberThreshold) return false;

            // Static publishers are considered long-lived
            if (leak.IsStatic) return true;

            // Exclude compiler-generated / closure types regardless of generation
            if (TypeFilterHelper.IsCompilerGenerated(leak.PublisherType) || IsNoiseType(leak.PublisherType))
                return false;

            // Accept all non-noise publishers. Generation is a severity signal (bonus score),
            // not an acceptance gate — Gen0/Gen1 publishers can hold large subscriber chains too.
            return true;
        }

        internal static int CalculateSeverity(
            bool isStatic, int subscriberCount, string rootHint, EventLeakOptions options,
            int publisherGeneration = -1, int duplicateCount = 0, bool isDisposedButSubscribed = false, bool hasLifetimeMismatch = false,
            bool hasLowIncomingRefs = false) =>
            EventLeakSeverityClassifier.ComputeSeverity(
                isStatic, subscriberCount, rootHint, EventLeakSeverityWeights.FromOptions(options),
                publisherGeneration, duplicateCount, isDisposedButSubscribed, hasLifetimeMismatch, hasLowIncomingRefs);

        private static Dictionary<ulong, string> BuildRootHintMap(ClrHeap heap, IHeapAnalysisCache? cache)
        {
            var map = new Dictionary<ulong, string>();

            if (cache is not null)
            {
                var roots = cache.GetOrBuildValidRoots(heap);
                foreach ((string kind, ulong address) in roots)
                {
                    if (address == 0) continue;
                    map.TryAdd(address, kind);
                }
                return map;
            }

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                if (!root.Object.IsValid)
                    continue;

                ulong address = root.Object.Address;
                map.TryAdd(address, root.ToString() ?? root.RootKind.ToString());
            }

            return map;
        }

        /// <summary>
        /// Registry-driven statics pass (design §6). Iterates <see cref="PublisherRegistry.StaticPublisherMTs"/>
        /// once, reading static delegate fields at the offsets/names <see cref="PublisherRegistry.Build"/>
        /// already resolved (event-name matching happened once there, not per-call here). Statics
        /// no longer run on <see cref="EventLeakFastScanner"/>'s hot path, so this single sweep is the
        /// only place static event fields are counted — a type with both heap instances and a static
        /// event field is counted exactly once (closes the former double-count bug, where
        /// <c>SweepModuleStaticFields</c> accepted a <c>processedStaticMTs</c> dedup set but never
        /// consulted it).
        /// </summary>
        private static void SweepRegistryStatics(
            ClrHeap heap,
            IReadOnlyList<ClrAppDomain> appDomains,
            Dictionary<ulong, string> rootHints,
            EventLeakOptions options,
            PublisherRegistry registry,
            Action<EventLeakInfo> addLeak,
            ref int eventsScanned,
            CancellationToken cancellationToken,
            IProgress<AnalyzerProgressReport>? progress = null)
        {
            var disposableTypeCache = registry.DisposableTypeCache;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int typesChecked = 0;

            foreach (ulong mt in registry.StaticPublisherMTs)
            {
                typesChecked++;
                if ((typesChecked & 8191) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (typesChecked % 500 == 0)
                    progress?.Report(new AnalyzerProgressReport(typesChecked,
                        "scanning static event fields",
                        $"{typesChecked:N0} types checked",
                        sw.Elapsed));

                if (!registry.TryGetDescriptors(mt, out EventFieldDescriptor[]? descriptors) || descriptors is null)
                    continue;

                ClrType? type = heap.GetTypeByMethodTable(mt);
                if (type is null) continue;

                foreach (EventFieldDescriptor descriptor in descriptors)
                {
                    if (!descriptor.IsStatic) continue;

                    ClrStaticField? sField = type.GetStaticFieldByName(descriptor.FieldName);
                    if (sField is null) continue;

                    eventsScanned++;

                    var subs = GetStaticEventSubscribers(heap, sField, appDomains);
                    if (subs.Count == 0) continue;

                    bool mismatch = CheckLifetimeMismatch(heap, 2, subs, options);
                    var leak = CreateLeakInfo(
                        publisherAddress: 0,
                        publisherType: type.Name ?? StringConstants.UnknownType,
                        eventFieldName: descriptor.FieldName,
                        isStatic: true,
                        subs, rootHints, options, heap: heap,
                        publisherGeneration: 2,
                        hasLifetimeMismatch: mismatch,
                        disposableTypeCache: disposableTypeCache,
                        publisherMethodTable: mt);

                    if (IsLikelyPublisher(leak, options))
                        addLeak(leak);
                }
            }
        }

        internal static void ParseRootPublisher(string rootDescription, out string publisherType, out string eventFieldName)
        {
            publisherType = "StaticRoot";
            eventFieldName = StringConstants.UnknownType;

            int lastDot = rootDescription.LastIndexOf('.');
            if (lastDot > 0 && lastDot < rootDescription.Length - 1)
            {
                publisherType = rootDescription[..lastDot].Trim();
                eventFieldName = rootDescription[(lastDot + 1)..].Trim();
            }
        }

        // internal so EventLeakFastScanner can reuse the same heuristic.
        // NOTE: this is only called after the field type has been confirmed as a delegate type,
        // so the _ prefix rule is safe from matching "any private field" — it's "any private
        // delegate-typed backing field". That alone is not enough to be event-specific, though:
        // see allowBareUnderscorePrefix below (P2-3, docs/analysis/phase1/eventleak-analyzer-audit.md).
        internal static bool LooksLikeEventFieldName(string? fieldName, bool allowBareUnderscorePrefix = true)
        {
            if (string.IsNullOrEmpty(fieldName)) return false;

            // common patterns and backing-field markers
            if (fieldName.IndexOf("Event", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.IndexOf("Changed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.IndexOf("Handler", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.IndexOf("Callback", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.IndexOf("Raised", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.IndexOf("Fired", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fieldName.Contains("k__BackingField", StringComparison.Ordinal)) return true;
            // Standard C# explicit event backing field convention: _myEvent, _clickHandler, etc.
            // A bare "_" prefix alone is NOT event-specific — an ordinary callback/factory field
            // (_onComplete, _factory, _selector, _predicate) matches it just as well as a real
            // event backing field. allowBareUnderscorePrefix is false when the caller already
            // knows the type declares zero real events (PublisherRegistry's eventNames.Count == 0
            // path) — in that case only the stronger name-pattern signals above should qualify a
            // field, since there is no corroborating evidence this delegate field is an event at
            // all. When the type DOES declare at least one real event, the prefix is trusted as a
            // secondary signal for matching that event's (possibly non-standard-named) backing
            // field.
            if (allowBareUnderscorePrefix && fieldName.StartsWith("_", StringComparison.Ordinal)) return true;

            return false;
        }

        private static bool IsNoiseType(string typeName) => IsNoiseTypeName(typeName);

        // internal so EventLeakFastScanner can reuse the same heuristic.
        internal static bool IsNoiseTypeName(string? typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            if (typeName.IndexOf("System.Threading", StringComparison.Ordinal) >= 0) return true;
            if (typeName.IndexOf("System.Linq", StringComparison.Ordinal) >= 0) return true;
            if (typeName.IndexOf("System.Threading.Tasks", StringComparison.Ordinal) >= 0) return true;
            if (typeName.Contains("<>", StringComparison.Ordinal)) return true;
            if (typeName.Contains("DisplayClass", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Pure logic core of <see cref="EventNameResolver.GetEventNames"/>: given two collections of method name
        /// tokens (the "add" side and the "remove" side), returns the set of event names that
        /// have a matching pair. Extracted so unit tests can exercise the logic without ClrMD.
        /// </summary>
        internal static HashSet<string> BuildEventNameSet(
            IEnumerable<string> addNames,
            IEnumerable<string> removeNames)
        {
            var removeSet = removeNames as HashSet<string>
                ?? new HashSet<string>(removeNames, StringComparer.Ordinal);
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in addNames)
                if (removeSet.Contains(e)) result.Add(e);
            return result;
        }

        // All C# delegates derive from MulticastDelegate. Dispatch is based on
        // whether _invocationList is a non-null array (multiple subscribers) vs null (single subscriber).
        private static List<SubscriberInfo> ExtractSubscribersFromDelegateAddress(ClrHeap heap, ulong delegateAddress, Dictionary<string, ulong>? typeSizeMap = null)
        {
            if (delegateAddress == 0)
                return [];

            ClrObject eventDelegate = heap.GetObject(delegateAddress);
            return ExtractSubscribersFromDelegateObject(heap.Runtime, eventDelegate, typeSizeMap);
        }

        private static List<SubscriberInfo> ExtractSubscribersFromDelegateObject(ClrRuntime runtime, ClrObject eventDelegate, Dictionary<string, ulong>? typeSizeMap)
        {
            var subscribers = new List<SubscriberInfo>();

            if (!eventDelegate.IsValid || eventDelegate.Type == null)
                return subscribers;

            foreach (var delegateObj in GetDelegateTargets(eventDelegate))
            {
                if (delegateObj.IsValid)
                    ExtractSingleSubscriber(runtime, delegateObj, subscribers, typeSizeMap);
            }

            return subscribers;
        }

        private static IEnumerable<ClrObject> GetDelegateTargets(ClrObject del)
        {
            if (!del.IsValid || del.Type == null)
                yield break;

            var invocationListField = DelegateHelper.GetCachedField(del.Type, StringConstants.DelegateInvocationListField);

            ClrObject invocationObj = invocationListField != null
                ? invocationListField.ReadObject(del, interior: false)
                : new ClrObject();

            if (invocationObj.IsValid)
            {
                if (invocationObj.IsArray)
                {
                    var arr = invocationObj.AsArray();
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var item = arr.GetObjectValue(i);
                        if (item.IsValid)
                            yield return item;
                    }
                    yield break;
                }
                else if (invocationObj.Type?.BaseType?.Name == "System.MulticastDelegate")
                {
                    yield return invocationObj;
                    yield break;
                }
            }

            // fallback: single-cast uses the delegate object itself
            yield return del;
        }

        // Count incoming references to a target by sampling objects on the heap.
        // Scans up to `maxScan` objects and returns the observed count of incoming refs.
        internal static int CountIncomingRefs(ClrHeap heap, ulong targetAddress, int maxScan = 1000)
        {
            if (heap == null || targetAddress == 0) return 0;

            int seen = 0;
            int incoming = 0;

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (++seen > maxScan) break;
                if (!obj.IsValid || obj.Address == 0 || obj.Type == null) continue;

                try
                {
                    foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
                    {
                        if (child.IsValid && child.Address == targetAddress)
                        {
                            incoming++;
                            // early exit if multiple found in same object is irrelevant
                            break;
                        }
                    }
                }
                catch
                {
                    // ignore problematic objects
                }
            }

            return incoming;
        }

        // Sample subscribers and return true if a significant fraction appear to have very few incoming refs.
        internal static bool HasLowIncomingRefsSignal(ClrHeap heap, List<SubscriberInfo> subscribers, EventLeakOptions options)
        {
            if (heap == null || subscribers == null || subscribers.Count == 0) return false;

            int probes = Math.Min(subscribers.Count, options.LifetimeMismatchProbeLimit);
            int lowCount = 0;
            int tried = 0;

            for (int i = 0; i < subscribers.Count && tried < probes; i++)
            {
                var s = subscribers[i];
                if (s.Address == 0) continue;
                tried++;
                int incoming = CountIncomingRefs(heap, s.Address, maxScan: 500);
                if (incoming <= 2) lowCount++;
            }

            if (tried == 0) return false;
            double frac = (double)lowCount / tried;
            return frac >= 0.25; // if >=25% of probed subscribers have very few inbound refs, flag
        }

        private static void ExtractSingleSubscriber(ClrRuntime runtime, ClrObject delegateObj, List<SubscriberInfo> subscribers, Dictionary<string, ulong>? typeSizeMap)
        {
            if (delegateObj.Type == null)
                return;

            var targetField = DelegateHelper.GetCachedField(delegateObj.Type, StringConstants.DelegateTargetField);
            if (targetField == null)
                return;

            string? methodName = ResolveMethodName(runtime, delegateObj);

            ClrObject target = targetField.ReadObject(delegateObj, interior: false);
            if (target.IsValid && target.Type != null)
            {
                var typeName = target.Type.Name ?? StringConstants.UnknownType;
                if (IsLikelySubscriber(typeName))
                {
                    subscribers.Add(new SubscriberInfo
                    {
                        Address = target.Address,
                        MethodTable = target.Type.MethodTable,
                        Type = typeName,
                        MethodName = methodName,
                        SubscriberSize = typeSizeMap != null && typeSizeMap.TryGetValue(typeName, out ulong sz) ? sz : 0
                    });
                }
            }
            else
            {
                // _target is null for static method handlers. The delegate object itself
                // has a unique heap address — use it so each static subscription is counted
                // and deduplicated correctly in GetStaticEventSubscribers.
                if (delegateObj.Address != 0)
                {
                    subscribers.Add(new SubscriberInfo
                    {
                        Address = delegateObj.Address,
                        Type = StringConstants.StaticMethodSubscriber,
                        MethodName = methodName
                    });
                }
            }
        }

        /// <summary>
        /// Reads _methodPtr from a delegate object and resolves it to a method signature
        /// via <see cref="ClrRuntime.GetMethodByInstructionPointer"/>.
        /// Returns null when the pointer is zero or the method cannot be resolved.
        /// </summary>
        private static string? ResolveMethodName(ClrRuntime runtime, ClrObject delegateObj)
        {
            if (delegateObj.Type == null) return null;
            try
            {
                // Try common delegate fields in order: _methodPtr, _methodPtrAux
                ClrType? cur = delegateObj.Type;
                ClrInstanceField? fPtr = null;
                while (cur != null && fPtr == null)
                {
                    fPtr = cur.GetFieldByName("_methodPtr");
                    cur = cur.BaseType;
                }

                if (fPtr != null)
                {
                    ulong ptr = (ulong)fPtr.Read<IntPtr>(delegateObj, interior: false);
                    if (ptr != 0)
                    {
                        var m = runtime.GetMethodByInstructionPointer(ptr);
                        if (m != null)
                        {
                            string sig = m.Signature ?? m.Name ?? string.Empty;
                            if (!string.IsNullOrEmpty(sig)) return sig;
                        }
                    }
                }

                // Try _methodPtrAux as a fallback
                cur = delegateObj.Type;
                ClrInstanceField? fAux = null;
                while (cur != null && fAux == null)
                {
                    fAux = cur.GetFieldByName("_methodPtrAux");
                    cur = cur.BaseType;
                }

                if (fAux != null)
                {
                    ulong ptrAux = (ulong)fAux.Read<IntPtr>(delegateObj, interior: false);
                    if (ptrAux != 0)
                    {
                        var m2 = runtime.GetMethodByInstructionPointer(ptrAux);
                        if (m2 != null)
                        {
                            string sig = m2.Signature ?? m2.Name ?? string.Empty;
                            if (!string.IsNullOrEmpty(sig)) return sig;
                        }
                    }
                }

                // Last resort: inspect _methodBase (MethodBase/RuntimeMethodHandle) for a pointer-like field
                cur = delegateObj.Type;
                ClrInstanceField? fBase = null;
                while (cur != null && fBase == null)
                {
                    fBase = cur.GetFieldByName("_methodBase");
                    cur = cur.BaseType;
                }

                if (fBase != null)
                {
                    ClrObject mb = fBase.ReadObject(delegateObj, interior: false);
                    if (mb.IsValid && mb.Type != null)
                    {
                        // Heuristic: look for any native-int sized instance field on the MethodBase wrapper
                        foreach (var field in mb.Type.Fields)
                        {
                            try
                            {
                                if (field.ElementType == ClrElementType.NativeInt || field.ElementType == ClrElementType.Int64 || field.ElementType == ClrElementType.UInt64)
                                {
                                    ulong val = 0;
                                    try { val = (ulong)field.Read<IntPtr>(mb, interior: false); }
                                    catch { continue; }
                                    if (val == 0) continue;
                                    var m3 = runtime.GetMethodByInstructionPointer(val);
                                    if (m3 != null)
                                    {
                                        string sig = m3.Signature ?? m3.Name ?? string.Empty;
                                        if (!string.IsNullOrEmpty(sig)) return sig;
                                    }
                                }
                            }
                            catch { /* ignore field read errors */ }
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsLikelySubscriber(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            // Drop compiler-generated closures — they have no stable type identity
            // and are already counted via their enclosing instance's _target.
            if (TypeFilterHelper.IsCompilerGenerated(typeName)) return false;
            if (typeName.Contains("<>", StringComparison.Ordinal)) return false;
            if (typeName.Contains("DisplayClass", StringComparison.Ordinal)) return false;
            // Do NOT filter System.* — framework types (System.Windows.Forms.*,
            // System.Web.*, System.ComponentModel.*, etc.) are legitimate subscribers
            // that retain memory. Dropping them produces artificially low counts.
            return true;
        }

        // ── Heuristic helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Counts the number of <em>extra</em> occurrences of any subscriber address beyond the
        /// first. A count > 0 means the same subscriber object was registered more than once.
        /// </summary>
        private static int CountDuplicateSubscriptions(List<SubscriberInfo> subscribers)
        {
            if (subscribers.Count <= 1) return 0;
            var seen = new Dictionary<ulong, int>(subscribers.Count);
            for (int i = 0; i < subscribers.Count; i++)
            {
                ulong addr = subscribers[i].Address;
                if (addr == 0) continue;
                seen.TryGetValue(addr, out int cnt);
                seen[addr] = cnt + 1;
            }
            int duplicates = 0;
            foreach (int cnt in seen.Values)
                if (cnt > 1) duplicates += cnt - 1;
            return duplicates;
        }

        /// <summary>
        /// Returns true when at least one subscriber's type implements <see cref="IDisposable"/> —
        /// a strong signal that the subscriber was meant to be torn down (and unsubscribed) but
        /// is still being kept alive by the publisher's delegate chain. Disposable-ness is
        /// resolved once per unique MethodTable and cached by the caller for the lifetime of the
        /// <c>Analyze</c> call (design §9).
        /// </summary>
        private static bool HasDisposableSubscriber(ClrHeap heap, List<SubscriberInfo> subscribers, Dictionary<ulong, bool> disposableTypeCache)
        {
            for (int i = 0; i < subscribers.Count; i++)
            {
                ulong mt = subscribers[i].MethodTable;
                if (mt == 0) continue;

                if (!disposableTypeCache.TryGetValue(mt, out bool isDisposable))
                {
                    isDisposable = false;
                    try
                    {
                        ClrType? type = heap.GetTypeByMethodTable(mt);
                        if (type != null)
                        {
                            foreach (ClrInterface iface in type.EnumerateInterfaces())
                            {
                                if (string.Equals(iface.Name, "System.IDisposable", StringComparison.Ordinal))
                                {
                                    isDisposable = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch { isDisposable = false; }
                    disposableTypeCache[mt] = isDisposable;
                }

                if (isDisposable) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when a publisher is retaining predominantly younger subscribers —
        /// i.e. publisherGeneration == 2 and many subscribers are Gen0/Gen1. Probes every
        /// subscriber (§9.19) — each generation lookup is an O(1) segment lookup, cheap
        /// regardless of scale, so no cap was needed.
        /// </summary>
        internal static bool CheckLifetimeMismatch(ClrHeap heap, int publisherGeneration, List<SubscriberInfo> subscribers, EventLeakOptions options)
        {
            if (publisherGeneration != 2) return false;
            if (subscribers.Count == 0) return false;
            int gen01Count = 0;
            int probed = 0;
            for (int i = 0; i < subscribers.Count; i++)
            {
                ulong addr = subscribers[i].Address;
                if (addr == 0 || subscribers[i].Type == StringConstants.StaticMethodSubscriber) continue;
                int gen = SegmentKindMapper.ResolveGeneration(heap,addr);
                if (gen == 0 || gen == 1) gen01Count++;
                probed++;
            }
            if (probed == 0) return false;
            return (double)gen01Count / probed >= options.LifetimeMismatchGen01Threshold;
        }

    Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context)
    {
        EventLeakOptions options = context.AnalysisOptions.EventLeak;

        List<EventGroupInfo> groupedLeaks = FindEventLeaks(
            context.Heap, context.Cache, options, context.Progress, CancellationToken.None,
            out int eventsScanned, out int publisherInstances,
            out int candidatePublisherTypeCount, out int leakingPublisherTypeCount);

        var eventLeakOptions = new EventLeakAnalyzerOptions(
            options, groupedLeaks, eventsScanned, publisherInstances,
            candidatePublisherTypeCount, leakingPublisherTypeCount);

        return AnalysisContextBuilder.Build(context, eventLeakOptions);
    }
}
