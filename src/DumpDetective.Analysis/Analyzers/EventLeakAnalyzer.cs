using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers
{
    public class EventLeakAnalyzer : IAnalyzer
    {
        // Presentation and severity tuning moved to EventLeakOptions

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, HashSet<string>> _eventNameCache = new();

        public string Name => "Event Leak Analysis";
        public string Category => "Events";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EventLeakOptions options = context.AnalysisOptions.EventLeak;

            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, EventLeakOptions options)
        {
            return Analyze(heap, cache: null, options, progress: null);
        }

        // internal so EventLeakFastScanner can reference the type without reflection.
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
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache? cache, EventLeakOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            var groupedLeaks = FindEventLeaks(heap, cache, options, progress,
                out int eventsScanned, out int publisherInstances);

            if (groupedLeaks.Count == 0)
            {
                return new EventLeakDomainResult(0, 0, 0, 0,
                    TotalEventsScanned: eventsScanned,
                    TotalPublisherInstances: publisherInstances);
            }

            // Build type-size map once for EstimatedSubscriberRetainedBytes computation.
            Dictionary<string, ulong> typeSizeMap = BuildTypeSizeMap(heap, cache);

            // Back-fill SubscriberSize on stored instances now that typeSizeMap is available.
            for (int i = 0; i < groupedLeaks.Count; i++)
                foreach (EventLeakInfo inst in groupedLeaks[i].Instances)
                    foreach (SubscriberInfo s in inst.Subscribers)
                        if (s.SubscriberSize == 0 && typeSizeMap.TryGetValue(s.Type, out ulong sz))
                            s.SubscriberSize = sz;

            int totalSubscribers = 0;
            int staticLeaks = 0;
            int instanceLeaks = 0;
            for (int i = 0; i < groupedLeaks.Count; i++)
            {
                totalSubscribers += groupedLeaks[i].TotalSubscribers;
                if (groupedLeaks[i].IsStatic) staticLeaks++; else instanceLeaks++;
            }

            var topPublisherEvents = new List<NameCountEntry>(Math.Min(10, groupedLeaks.Count));
            // Manual top-50 by TotalSubscribers (avoid LINQ on large lists)
            var sortedGroups = new List<EventGroupInfo>(groupedLeaks);
            sortedGroups.Sort((a, b) => b.TotalSubscribers.CompareTo(a.TotalSubscribers));
            int topN = Math.Min(50, sortedGroups.Count);
            for (int i = 0; i < topN; i++)
                topPublisherEvents.Add(new NameCountEntry($"{sortedGroups[i].PublisherType}.{sortedGroups[i].EventFieldName}", sortedGroups[i].TotalSubscribers));

            // Richer summary rows for the report table (separate class/event, add instance count + retained size).
            var topPublisherEventsFull = new List<PublisherEventSummary>(topN);
            for (int i = 0; i < topN; i++)
            {
                var g = sortedGroups[i];
                topPublisherEventsFull.Add(new PublisherEventSummary(
                    g.PublisherType,
                    g.EventFieldName,
                    g.TotalSubscribers,
                    g.InstanceCount,
                    EstimateGroupRetainedBytes(g, typeSizeMap)));
            }

            var topLeakGroups = new List<EventLeakGroupSnapshot>(groupedLeaks.Count);
            for (int i = 0; i < groupedLeaks.Count; i++)
            {
                var g = groupedLeaks[i];
                // Use pre-aggregated subscriber type counts from ALL instances.
                // g.Instances is capped at TopDetailedInstancesPerGroup — re-deriving
                // subTypeCounts from it would only reflect those few stored instances.
                var subTypeCounts = g.AllSubscriberTypeCounts
                    ?? new Dictionary<string, int>(StringComparer.Ordinal);
                bool groupHasDuplicates = false;
                int groupOrphanedInstances = 0;
                bool groupHasLifetimeMismatch = false;
                for (int j = 0; j < g.Instances.Count; j++)
                {
                    var gInst = g.Instances[j];
                    if (gInst.DuplicateSubscriptionCount > 0) groupHasDuplicates = true;
                    if (gInst.OrphanedSubscriberCount > 0) groupOrphanedInstances++;
                    if (gInst.HasLifetimeMismatch) groupHasLifetimeMismatch = true;
                }
                var topSubTypes = new List<NameCountEntry>(subTypeCounts.Count);
                foreach (var kvp in subTypeCounts.OrderByDescending(kv => kv.Value))
                    topSubTypes.Add(new NameCountEntry(kvp.Key, kvp.Value));

                topLeakGroups.Add(new EventLeakGroupSnapshot(
                    g.PublisherType,
                    g.EventFieldName,
                    g.IsStatic,
                    g.SeverityScore,
                    g.InstanceCount,
                    g.TotalSubscribers,
                    g.AverageSubscribers,
                    g.MinSubscribers,
                    g.MaxSubscribers,
                    topSubTypes,
                    EstimateGroupRetainedBytes(g, typeSizeMap),
                    HasDuplicateSubscriptions: groupHasDuplicates,
                    OrphanedSubscriberInstances: groupOrphanedInstances,
                    HasLifetimeMismatch: groupHasLifetimeMismatch));
            }

            var topLeakInstances = new List<EventLeakInstanceSnapshot>();
            for (int i = 0; i < groupedLeaks.Count; i++)
            {
                var g = groupedLeaks[i];
                for (int j = 0; j < g.Instances.Count; j++)
                {
                    var inst = g.Instances[j];
                    var subTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (SubscriberInfo s in inst.Subscribers)
                    {
                        subTypeCounts.TryGetValue(s.Type, out int cnt);
                        subTypeCounts[s.Type] = cnt + 1;
                    }
                    var subTypeList = new List<string>(subTypeCounts.Count);
                    foreach (var kvp in subTypeCounts.OrderByDescending(kv => kv.Value))
                        subTypeList.Add($"{kvp.Key} ({kvp.Value:N0})");

                    // Build per-subscriber detail rows (deduplicated by type+method, summed count).
                    var detailKey = new Dictionary<(string Type, string? Method), (int Count, ulong Size)>(inst.Subscribers.Count);
                    foreach (SubscriberInfo s in inst.Subscribers)
                    {
                        var key = (s.Type, s.MethodName);
                        detailKey.TryGetValue(key, out var existing);
                        detailKey[key] = (existing.Count + 1, s.SubscriberSize > 0 ? s.SubscriberSize : existing.Size);
                    }
                    var subDetails = new List<SubscriberDetail>(detailKey.Count);
                    foreach (var kvp in detailKey.OrderByDescending(kv => kv.Value.Count))
                        subDetails.Add(new SubscriberDetail(kvp.Key.Type, kvp.Key.Method, kvp.Value.Size, kvp.Value.Count));

                    topLeakInstances.Add(new EventLeakInstanceSnapshot(
                        g.PublisherType,
                        g.EventFieldName,
                        g.IsStatic,
                        inst.PublisherAddress,
                        inst.SeverityScore,
                        inst.SubscriberCount,
                        string.IsNullOrWhiteSpace(inst.RootHint) ? null : inst.RootHint,
                        subTypeList,
                        PublisherGeneration: inst.PublisherGeneration,
                        DuplicateSubscriptionCount: inst.DuplicateSubscriptionCount,
                        OrphanedSubscriberCount: inst.OrphanedSubscriberCount,
                        HasLifetimeMismatch: inst.HasLifetimeMismatch,
                        SubscriberDetails: subDetails));
                }
            }
            topLeakInstances.Sort((a, b) =>
            {
                int cmp = b.SeverityScore.CompareTo(a.SeverityScore);
                return cmp != 0 ? cmp : b.SubscriberCount.CompareTo(a.SubscriberCount);
            });

            return new EventLeakDomainResult(
                groupedLeaks.Count,
                totalSubscribers,
                staticLeaks,
                instanceLeaks,
                topPublisherEvents,
                topLeakGroups,
                topLeakInstances,
                TotalEventsScanned: eventsScanned,
                TotalPublisherInstances: publisherInstances,
                TopPublisherEvents: topPublisherEventsFull);
        }

        private static Dictionary<string, ulong> BuildTypeSizeMap(ClrHeap heap, IHeapAnalysisCache? cache)
        {
            if (cache is not HeapAnalysisCache heapCache)
                return new Dictionary<string, ulong>(0, StringComparer.Ordinal);

            Dictionary<string, CachedTypeStatistics> typeStats = heapCache.GetOrBuildTypeStatistics(heap);
            var map = new Dictionary<string, ulong>(typeStats.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, CachedTypeStatistics> kvp in typeStats)
            {
                if (kvp.Value.Count > 0)
                    map[kvp.Key] = kvp.Value.TotalSize / (ulong)kvp.Value.Count;
            }
            return map;
        }

        private static ulong EstimateGroupRetainedBytes(EventGroupInfo g, Dictionary<string, ulong> typeSizeMap)
        {
            ulong total = 0;
            for (int i = 0; i < g.Instances.Count; i++)
            {
                foreach (SubscriberInfo s in g.Instances[i].Subscribers)
                    total += typeSizeMap.TryGetValue(s.Type, out ulong sz) ? sz : 64UL;
            }
            return total;
        }

        /// <summary>
        /// Returns true if <paramref name="type"/> declares at least one instance or static field
        /// whose base type is <c>System.MulticastDelegate</c> (i.e. a delegate / event backing field).
        /// Called once per unique MethodTable to populate the MT pre-filter cache.
        /// </summary>
        private static bool HasDelegateFields(ClrType type)
        {
            foreach (ClrInstanceField field in type.Fields)
            {
                if (TypeFilterHelper.IsDelegateType(field.Type))
                    return true;
            }
            foreach (ClrStaticField field in type.StaticFields)
            {
                if (TypeFilterHelper.IsDelegateType(field.Type))
                    return true;
            }
            return false;
        }

        // internal so EventLeakFastScanner can call without reflection.
        internal static void AddToAccumulator(
            Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator> acc,
            EventLeakInfo leak,
            int capacity)
        {
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

            // Accumulate subscriber type counts from ALL instances so the report type-breakdown
            // reflects the full population, not just the top-N stored instances.
            foreach (SubscriberInfo s in leak.Subscribers)
            {
                a.AllSubscriberTypeCounts.TryGetValue(s.Type, out int typeCount);
                a.AllSubscriberTypeCounts[s.Type] = typeCount + 1;
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

        /// <summary>Merges one partial accumulator into the target dictionary (used by parallel scan merge step).</summary>
        internal static void MergeAccumulatorEntry(
            Dictionary<(string, string, bool), GroupAccumulator> target,
            (string, string, bool) key,
            GroupAccumulator source,
            int capacity)
        {
            if (!target.TryGetValue(key, out GroupAccumulator? dest))
            {
                dest = new GroupAccumulator();
                target[key] = dest;
            }

            dest.InstanceCount += source.InstanceCount;
            dest.TotalSubscribers += source.TotalSubscribers;
            if (source.MinSubscribers < dest.MinSubscribers) dest.MinSubscribers = source.MinSubscribers;
            if (source.MaxSubscribers > dest.MaxSubscribers) dest.MaxSubscribers = source.MaxSubscribers;
            if (source.MaxSeverity > dest.MaxSeverity) dest.MaxSeverity = source.MaxSeverity;

            foreach (var kvp in source.AllSubscriberTypeCounts)
            {
                dest.AllSubscriberTypeCounts.TryGetValue(kvp.Key, out int existing);
                dest.AllSubscriberTypeCounts[kvp.Key] = existing + kvp.Value;
            }

            foreach (EventLeakInfo inst in source.TopInstances)
            {
                if (dest.TopInstances.Count < capacity)
                {
                    dest.TopInstances.Add(inst);
                }
                else
                {
                    int minIdx = 0;
                    int minVal = dest.TopInstances[0].SubscriberCount;
                    for (int i = 1; i < dest.TopInstances.Count; i++)
                    {
                        if (dest.TopInstances[i].SubscriberCount < minVal)
                        {
                            minVal = dest.TopInstances[i].SubscriberCount;
                            minIdx = i;
                        }
                    }
                    if (inst.SubscriberCount > minVal)
                        dest.TopInstances[minIdx] = inst;
                }
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

        private static void AddFindings(List<InsightFinding> findings, List<EventGroupInfo> groupedLeaks)
        {
            int findingsToEmit = Math.Min(5, groupedLeaks.Count);
            for (int i = 0; i < findingsToEmit; i++)
            {
                var group = groupedLeaks[i];
                var severity = group.SeverityScore >= 35
                    ? FindingSeverity.Critical
                    : group.SeverityScore >= 20
                        ? FindingSeverity.Warning
                        : FindingSeverity.Info;

                findings.Add(new InsightFinding(
                    Analyzer: nameof(EventLeakAnalyzer),
                    Category: "Leak",
                    Severity: severity,
                    Title: $"Potential {(group.IsStatic ? "static" : "instance")} event retention in {group.PublisherType}.{group.EventFieldName}",
                    Evidence: $"{group.InstanceCount:N0} publisher instance(s), {group.TotalSubscribers:N0} total subscribers, max {group.MaxSubscribers:N0} per instance.",
                    Recommendation: "Ensure subscribers are unsubscribed and avoid long-lived static event publishers where possible.",
                    Tags: group.IsStatic
                        ? ["event-leak", "static-event", "retention"]
                        : ["event-leak", "instance-event", "retention"],
                    MetricValue: group.TotalSubscribers,
                    MetricUnit: "subscribers"));
            }
        }

        private List<EventGroupInfo> FindEventLeaks(ClrHeap heap, IHeapAnalysisCache? cache, EventLeakOptions options, IProgress<AnalyzerProgressReport>? progress, out int eventsScanned, out int publisherInstances)
        {
            eventsScanned = 0;
            publisherInstances = 0;
            var groupAcc = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), GroupAccumulator>();

            var processedStaticMethodTables = new HashSet<ulong>(capacity: 64);
            var processedStaticDelegates = new HashSet<ulong>(capacity: 64);
            var appDomains = heap.Runtime.AppDomains;
            var rootHints = BuildRootHintMap(heap, cache);
            var scanCounter = new ObjectScanCounter("scanning event handlers", progress);

            // ── Fast scanner: direct IMemoryReader.ReadPointer — no heap.GetObject ────
            var fastScanner = new EventLeakFastScanner(heap, GetEventNames, progress);

            HeapEntry[]? inMemoryArray = null;
            int objectCount = 0;
            IEnumerable<HeapEntry>? streamingEntries = null;

            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out var heapIdx))
            {
                if (heapIdx.InMemoryEntries is { } arr)
                {
                    inMemoryArray = arr;
                    objectCount = (int)Math.Min(heapIdx.ObjectCount, (long)arr.Length);
                }
                else
                {
                    streamingEntries = heapCache.EnumerateIndexedEntries();
                }
            }
            else
            {
                streamingEntries = StreamObjectsAsEntries(heap);
            }

            fastScanner.Scan(streamingEntries, inMemoryArray, objectCount,
                groupAcc, rootHints, appDomains,
                processedStaticMethodTables, processedStaticDelegates, options,
                ref eventsScanned, ref publisherInstances);

            // Cover static-only publisher types (types with no heap instances) by sweeping
            // all types known to the runtime via modules. ProcessPublisherEntry already handles
            // static fields for types that have heap instances; this catches the rest.
            SweepModuleStaticFields(heap, appDomains, processedStaticMethodTables, processedStaticDelegates, rootHints, options,
                leak => AddToAccumulator(groupAcc, leak, options.TopDetailedInstancesPerGroup), ref eventsScanned, progress);

            scanCounter.Complete();

            // Convert accumulators into EventGroupInfo list
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
                    AllSubscriberTypeCounts = acc.AllSubscriberTypeCounts
                });
            }

            // Sort by total subscribers
            result.Sort((a, b) => b.TotalSubscribers.CompareTo(a.TotalSubscribers));

            return result;
        }

        private static IEnumerable<HeapEntry> EnumerateEventEntries(ClrHeap heap, IHeapAnalysisCache? cache)
        {
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out _))
            {
                foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
                    yield return entry;

                yield break;
            }

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null)
                    continue;

                ulong methodTable = obj.Type.MethodTable;
                if (methodTable == 0)
                    continue;

                yield return new HeapEntry(obj.Address, methodTable, obj.Size);
            }
        }

        internal List<EventGroupInfo> GroupEventLeaks(List<EventLeakInfo> eventLeaks)
        {
            // Pre-allocate with expected capacity
            var groups = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), List<EventLeakInfo>>();

            // Manual grouping to avoid LINQ overhead
            foreach (var leak in eventLeaks)
            {
                var key = (leak.PublisherType, leak.EventFieldName, leak.IsStatic);

                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<EventLeakInfo>();
                    groups[key] = list;
                }
                list.Add(leak);
            }

            // Convert to EventGroupInfo and calculate stats
            var result = new List<EventGroupInfo>(groups.Count);

            foreach (var kvp in groups)
            {
                var instances = kvp.Value;
                int totalSubs = 0;
                int minSubs = int.MaxValue;
                int maxSubs = 0;
                int maxSeverity = 0;

                foreach (var instance in instances)
                {
                    int count = instance.SubscriberCount;
                    totalSubs += count;
                    if (count < minSubs) minSubs = count;
                    if (count > maxSubs) maxSubs = count;
                    if (instance.SeverityScore > maxSeverity) maxSeverity = instance.SeverityScore;
                }

                result.Add(new EventGroupInfo
                {
                    PublisherType = kvp.Key.PublisherType,
                    EventFieldName = kvp.Key.EventFieldName,
                    IsStatic = kvp.Key.IsStatic,
                    SeverityScore = maxSeverity,
                    InstanceCount = instances.Count,
                    TotalSubscribers = totalSubs,
                    AverageSubscribers = (double)totalSubs / instances.Count,
                    MaxSubscribers = maxSubs,
                    MinSubscribers = minSubs,
                    Instances = instances
                });
            }

            // Sort by total subscribers
            result.Sort((a, b) => b.TotalSubscribers.CompareTo(a.TotalSubscribers));

            return result;
        }


        private static List<SubscriberInfo> GetEventSubscribers(ClrHeap heap, ulong publisherAddress, ClrInstanceField eventField)
        {
            try
            {
                if (publisherAddress == 0)
                    return new List<SubscriberInfo>();

                ClrObject publisher = heap.GetObject(publisherAddress);
                if (!publisher.IsValid)
                    return new List<SubscriberInfo>();

                ClrObject eventDelegate = eventField.ReadObject(publisher, interior: false);
                if (!eventDelegate.IsValid)
                    return new List<SubscriberInfo>();

                // Early filters: ensure this is a field-backed delegate, looks like an event field,
                // and is not clearly noise (framework/compiler generated types).
                try
                {
                    if (!IsFieldBackedDelegate(publisher, eventField, eventDelegate))
                        return new List<SubscriberInfo>();

                    if (!IsLikelyEventField(publisher.Type, eventField.Name))
                        return new List<SubscriberInfo>();

                    if (eventDelegate.Type != null && IsNoiseType(eventDelegate.Type.Name ?? string.Empty))
                        return new List<SubscriberInfo>();
                }
                catch
                {
                    return new List<SubscriberInfo>();
                }

                return ExtractSubscribersFromDelegateAddress(heap, eventDelegate.Address);
            }
            catch
            {
                return new List<SubscriberInfo>();
            }
        }

        // internal so EventLeakFastScanner can call it directly.
        internal static List<SubscriberInfo> GetStaticEventSubscribers(ClrHeap heap, ClrStaticField field, IReadOnlyList<ClrAppDomain> appDomains, HashSet<ulong>? processedStaticDelegates = null)
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
                    // All callers (SweepModuleStaticFields, ProcessPublisherEntry, fast-scanner post-pass)
                    // already validated the field name before calling this method.
                    // Re-filtering here silently drops fields whose names are in the type's event set
                    // (e.g. "DataReceived", "Completed") but don't match the heuristic patterns.

                    // Skip if this exact delegate object was already processed from another domain.
                    if (!seenDelegateAddresses.Add(eventDelegate.Address))
                        continue;

                    processedStaticDelegates?.Add(eventDelegate.Address);

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
            bool hasLifetimeMismatch = false)
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
            int orphanedCount = CountOrphanedSubscribers(subscribers, rootHints);

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
                PublisherType = publisherType,
                EventFieldName = eventFieldName,
                IsStatic = isStatic,
                SubscriberCount = subscribers.Count,
                Subscribers = subscribers,
                RootHint = rootHint,
                PublisherGeneration = publisherGeneration,
                DuplicateSubscriptionCount = duplicateCount,
                OrphanedSubscriberCount = orphanedCount,
                HasLifetimeMismatch = hasLifetimeMismatch,
                HasLowIncomingRefs = hasLowIncoming,
                SeverityScore = CalculateSeverity(isStatic, subscribers.Count, rootHint, options,
                    publisherGeneration, duplicateCount, orphanedCount, hasLifetimeMismatch, hasLowIncoming)
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
            int publisherGeneration = -1, int duplicateCount = 0, int orphanedCount = 0, bool hasLifetimeMismatch = false,
            bool hasLowIncomingRefs = false)
        {
            int score = subscriberCount;
            if (subscriberCount >= options.SeveritySubscriberThreshold) score += options.SeveritySubscriberBonus;
            if (isStatic) score += options.SeverityStaticPublisherBonus;
            if (!string.IsNullOrEmpty(rootHint)) score += options.SeverityRootHintBonus;
            if (publisherGeneration == 2) score += options.SeverityGen2PublisherBonus;
            if (duplicateCount > 0) score += options.SeverityDuplicateSubscriptionBonus;
            if (orphanedCount > 0) score += Math.Min(orphanedCount * options.SeverityOrphanedSubscriberBonus, options.SeverityOrphanedSubscriberCap);
            if (hasLifetimeMismatch) score += options.SeverityLifetimeMismatchBonus;
            if (hasLowIncomingRefs) score += options.SeverityLowIncomingRefsBonus;
            return score;
        }

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
        /// Sweeps all types known to the runtime via modules and processes static delegate fields
        /// for any type that was NOT already covered by the heap scan (no instances on the heap).
        /// This catches pure-static publisher classes.
        /// </summary>
        private static void SweepModuleStaticFields(
            ClrHeap heap,
            IReadOnlyList<ClrAppDomain> appDomains,
            HashSet<ulong> processedStaticMTs,
            HashSet<ulong> processedStaticDelegates,
            Dictionary<ulong, string> rootHints,
            EventLeakOptions options,
            Action<EventLeakInfo> addLeak,
            ref int eventsScanned,
            IProgress<AnalyzerProgressReport>? progress = null)
        {
            int minSubs = options.MinSubscribers;
            bool includeNonLeaking = options.IncludeNonLeakingEvents;

            var seenModuleMTs = new HashSet<ulong>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int typesChecked = 0;

            foreach (ClrAppDomain domain in appDomains)
            {
                foreach (ClrModule module in domain.Modules)
                {
                    foreach (ClrType type in module.EnumerateTypeDefToMethodTableMap()
                                                   .Select(pair => heap.GetTypeByMethodTable(pair.MethodTable))
                                                   .Where(t => t is not null)!)
                    {
                        ulong mt = type!.MethodTable;
                        if (mt == 0) continue;
                        if (!seenModuleMTs.Add(mt)) continue;         // dedup across domains

                        typesChecked++;
                        if (typesChecked % 500 == 0)
                            progress?.Report(new AnalyzerProgressReport(typesChecked,
                                "scanning static event fields",
                                $"{typesChecked:N0} types checked",
                                sw.Elapsed));

                        if (TypeFilterHelper.IsSystemType(type.Name)
                            || TypeFilterHelper.IsCompilerGenerated(type.Name)) continue;

                        HashSet<string> eventNames = GetEventNames(type);

                        foreach (ClrStaticField sField in type.StaticFields)
                        {
                            if (!TypeFilterHelper.IsDelegateType(sField.Type)
                                || TypeFilterHelper.IsCompilerGenerated(sField.Name)
                                || string.IsNullOrEmpty(sField.Name)) continue;

                            if (eventNames.Count > 0
                                && !eventNames.Contains(sField.Name!)
                                && !LooksLikeEventFieldName(sField.Name)) continue;

                            if (eventNames.Count == 0 && !LooksLikeEventFieldName(sField.Name)) continue;

                            eventsScanned++;

                            var subs = GetStaticEventSubscribers(heap, sField, appDomains, processedStaticDelegates);
                            if (subs.Count == 0) continue;
                            if (!includeNonLeaking && subs.Count < minSubs) continue;

                            bool mismatch = CheckLifetimeMismatch(heap, 2, subs, options);
                            var leak = CreateLeakInfo(
                                publisherAddress: 0,
                                publisherType: type.Name ?? StringConstants.UnknownType,
                                eventFieldName: sField.Name!,
                                isStatic: true,
                                subs, rootHints, options, heap: heap,
                                publisherGeneration: 2,
                                hasLifetimeMismatch: mismatch);

                            if (IsLikelyPublisher(leak, options))
                                addLeak(leak);
                        }
                    }
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

        private static bool IsLikelyEventField(ClrType? ownerType, string? fieldName)
        {
            if (string.IsNullOrEmpty(fieldName))
                return false;

            if (TypeFilterHelper.IsCompilerGenerated(fieldName))
                return false;

            // Exclude obvious noisy owner types
            if (ownerType != null && !string.IsNullOrEmpty(ownerType.Name) && IsNoiseType(ownerType.Name))
                return false;

            var eventNames = ownerType is null ? new HashSet<string>(0, StringComparer.Ordinal) : GetEventNames(ownerType);
            if (eventNames.Count == 0)
                return LooksLikeEventField(fieldName);

            return eventNames.Contains(fieldName) || LooksLikeEventField(fieldName);
        }

        private static bool LooksLikeEventField(string? fieldName) => LooksLikeEventFieldName(fieldName);

        // internal so EventLeakFastScanner can reuse the same heuristic.
        // NOTE: this is only called after the field type has been confirmed as a delegate type,
        // so the _ prefix rule is safe — it's not a broad "any private field" match, it's
        // "any private delegate-typed backing field", which is a standard C# event pattern.
        internal static bool LooksLikeEventFieldName(string? fieldName)
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
            // Safe here because callers already require field.Type to be a delegate type.
            if (fieldName.StartsWith("_", StringComparison.Ordinal)) return true;

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

        private static bool IsFieldBackedDelegate(ClrObject parent, ClrInstanceField field, ClrObject value)
        {
            try
            {
                if (field == null) return false;
                if (!field.IsObjectReference) return false;
                if (!value.IsValid || value.Type == null) return false;
                return TypeFilterHelper.IsDelegateType(value.Type);
            }
            catch
            {
                return false;
            }
        }

        private static HashSet<string> GetEventNames(ClrType type)
        {
            // Key the cache by the MethodTable which is unique per type/assembly.
            ulong mt = type.MethodTable;

            if (_eventNameCache.TryGetValue(mt, out var cached))
                return cached;

            // Step 1: collect only the concrete type's own add_/remove_ pairs.
            var ownAddNames = new HashSet<string>(StringComparer.Ordinal);
            var ownRemoveNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var method in type.Methods)
            {
                var name = method.Name;
                if (name == null) continue;

                if (name.StartsWith("add_", StringComparison.Ordinal) && name.Length > 4)
                    ownAddNames.Add(name[4..]);
                else if (name.StartsWith("remove_", StringComparison.Ordinal) && name.Length > 7)
                    ownRemoveNames.Add(name[7..]);
            }

            var ownEvents = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in ownAddNames)
                if (ownRemoveNames.Contains(e)) ownEvents.Add(e);

            // Step 2: if this type declares NO own events, cache empty to mean "all-pass".
            if (ownEvents.Count == 0)
            {
                _eventNameCache[mt] = ownEvents;
                return ownEvents;
            }

            // Step 3: include inherited add/remove pairs so inherited backing fields are not lost.
            var allAddNames = new HashSet<string>(ownAddNames, StringComparer.Ordinal);
            var allRemoveNames = new HashSet<string>(ownRemoveNames, StringComparer.Ordinal);

            ClrType? current = type.BaseType;
            while (current != null
                && current.Name != "System.Object"
                && current.Name != "System.Delegate"
                && current.Name != "System.MulticastDelegate")
            {
                foreach (var method in current.Methods)
                {
                    var name = method.Name;
                    if (name == null) continue;

                    if (name.StartsWith("add_", StringComparison.Ordinal) && name.Length > 4)
                        allAddNames.Add(name[4..]);
                    else if (name.StartsWith("remove_", StringComparison.Ordinal) && name.Length > 7)
                        allRemoveNames.Add(name[7..]);
                }
                current = current.BaseType;
            }

            var names = BuildEventNameSet(allAddNames, allRemoveNames);
            _eventNameCache[mt] = names;
            return names;
        }

        /// <summary>
        /// Pure logic core of <see cref="GetEventNames"/>: given two collections of method name
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

        /// <summary>Returns the GC generation (0/1/2) of an object, or -1 if unknown/invalid.</summary>
        private static int GetObjectGeneration(ClrHeap heap, ulong address)
        {
            if (address == 0) return -1;
            ClrSegment? seg = heap.GetSegmentByAddress(address);
            if (seg == null) return -1;
            try { return (int)seg.GetGeneration(address); }
            catch { return -1; }
        }

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
        /// Counts subscribers whose address does not appear in <paramref name="rootHints"/>,
        /// meaning they have no independent GC root — their only reachability is through the
        /// delegate chain (dead-subscriber pattern).
        /// </summary>
        private static int CountOrphanedSubscribers(List<SubscriberInfo> subscribers, Dictionary<ulong, string> rootHints)
        {
            int count = 0;
            for (int i = 0; i < subscribers.Count; i++)
            {
                ulong addr = subscribers[i].Address;
                if (addr != 0
                    && subscribers[i].Type != StringConstants.StaticMethodSubscriber
                    && !rootHints.ContainsKey(addr))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Returns true when a publisher is retaining predominantly younger subscribers —
        /// i.e. publisherGeneration == 2 and many subscribers are Gen0/Gen1.
        /// Probes at most <see cref="EventLeakOptions.LifetimeMismatchProbeLimit"/> subscribers
        /// to keep the cost bounded.
        /// </summary>
        internal static bool CheckLifetimeMismatch(ClrHeap heap, int publisherGeneration, List<SubscriberInfo> subscribers, EventLeakOptions options)
        {
            if (publisherGeneration != 2) return false;
            if (subscribers.Count == 0) return false;
            int probeLimit = Math.Min(subscribers.Count, options.LifetimeMismatchProbeLimit);
            int gen01Count = 0;
            int probed = 0;
            for (int i = 0; i < subscribers.Count && probed < probeLimit; i++)
            {
                ulong addr = subscribers[i].Address;
                if (addr == 0 || subscribers[i].Type == StringConstants.StaticMethodSubscriber) continue;
                int gen = GetObjectGeneration(heap, addr);
                if (gen == 0 || gen == 1) gen01Count++;
                probed++;
            }
            if (probed == 0) return false;
            return (double)gen01Count / probed >= options.LifetimeMismatchGen01Threshold;
        }

        /// <summary>
        /// Simple pairwise lifetime mismatch check between a publisher and a subscriber.
        /// Returns true when publisher is Gen2 (long-lived) and subscriber is Gen0/Gen1 (short-lived).
        /// </summary>
        internal static bool IsLifetimeMismatch(ClrHeap heap, ulong publisherAddress, ulong subscriberAddress)
        {
            int p = GetObjectGeneration(heap, publisherAddress);
            int s = GetObjectGeneration(heap, subscriberAddress);
            return IsLifetimeMismatch(p, s);
        }

        internal static bool IsLifetimeMismatch(int publisherGeneration, int subscriberGeneration)
        {
            return publisherGeneration == 2 && (subscriberGeneration == 0 || subscriberGeneration == 1);
        }

        public void Dispose() { }
    }
}


