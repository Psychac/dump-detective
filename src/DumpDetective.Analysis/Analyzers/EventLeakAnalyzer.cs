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

        private readonly Dictionary<string, HashSet<string>> _eventNameCache = new(StringComparer.Ordinal);
        private readonly object _eventNameCacheLock = new();

        public string Name => "Event Leak Analysis";
        public string Category => "Events";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EventLeakOptions options = context.GetOption<EventLeakOptions>();

            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, EventLeakOptions options)
        {
            return Analyze(heap, cache: null, options, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache? cache, EventLeakOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            var eventLeaks = FindEventLeaks(heap, cache, options, progress,
                out int eventsScanned, out int publisherInstances);

            if (eventLeaks.Count == 0)
            {
                return new EventLeakDomainResult(0, 0, 0, 0,
                    TotalEventsScanned: eventsScanned,
                    TotalPublisherInstances: publisherInstances);
            }

            var groupedLeaks = GroupEventLeaks(eventLeaks);

            // Build type-size map once for EstimatedSubscriberRetainedBytes computation.
            Dictionary<string, ulong> typeSizeMap = BuildTypeSizeMap(heap, cache);

            int totalSubscribers = 0;
            int staticLeaks = 0;
            int instanceLeaks = 0;
            for (int i = 0; i < groupedLeaks.Count; i++)
            {
                totalSubscribers += groupedLeaks[i].TotalSubscribers;
                if (groupedLeaks[i].IsStatic) staticLeaks++; else instanceLeaks++;
            }

            var topPublisherEvents = new List<NameCountEntry>(Math.Min(10, groupedLeaks.Count));
            // Manual top-10 by TotalSubscribers (avoid LINQ on large lists)
            var sortedGroups = new List<EventGroupInfo>(groupedLeaks);
            sortedGroups.Sort((a, b) => b.TotalSubscribers.CompareTo(a.TotalSubscribers));
            for (int i = 0; i < Math.Min(10, sortedGroups.Count); i++)
                topPublisherEvents.Add(new NameCountEntry($"{sortedGroups[i].PublisherType}.{sortedGroups[i].EventFieldName}", sortedGroups[i].TotalSubscribers));

            var topLeakGroups = new List<EventLeakGroupSnapshot>(groupedLeaks.Count);
            for (int i = 0; i < groupedLeaks.Count; i++)
            {
                var g = groupedLeaks[i];
                // Aggregate subscriber types across all instances for this group.
                var subTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int j = 0; j < g.Instances.Count; j++)
                {
                    foreach (SubscriberInfo s in g.Instances[j].Subscribers)
                    {
                        subTypeCounts.TryGetValue(s.Type, out int cnt);
                        subTypeCounts[s.Type] = cnt + 1;
                    }
                }
                var topSubTypes = new List<NameCountEntry>(Math.Min(options.TopSubscriberTypesToShow, subTypeCounts.Count));
                foreach (var kvp in subTypeCounts.OrderByDescending(kv => kv.Value).Take(options.TopSubscriberTypesToShow))
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
                    EstimateGroupRetainedBytes(g, typeSizeMap)));
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
                    var subTypeList = new List<string>(Math.Min(options.TopSubscriberTypesToShow, subTypeCounts.Count));
                    foreach (var kvp in subTypeCounts.OrderByDescending(kv => kv.Value).Take(options.TopSubscriberTypesToShow))
                        subTypeList.Add($"{kvp.Key} ({kvp.Value:N0})");

                    topLeakInstances.Add(new EventLeakInstanceSnapshot(
                        g.PublisherType,
                        g.EventFieldName,
                        g.IsStatic,
                        inst.PublisherAddress,
                        inst.SeverityScore,
                        inst.SubscriberCount,
                        string.IsNullOrWhiteSpace(inst.RootHint) ? null : inst.RootHint,
                        subTypeList));
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
                TotalPublisherInstances: publisherInstances);
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

        private List<EventLeakInfo> FindEventLeaks(ClrHeap heap, IHeapAnalysisCache? cache, EventLeakOptions options, IProgress<AnalyzerProgressReport>? progress, out int eventsScanned, out int publisherInstances)
        {
            eventsScanned = 0;
            publisherInstances = 0;
            var leaks = new List<EventLeakInfo>();
            // FIX-3: processedObjects removed — heap-index enumeration and heap.EnumerateObjects() both
            // yield each object address exactly once, so the deduplication HashSet was redundant.
            // It grew to hold ALL scanned addresses (up to 20 M entries / ~320 MB) and triggered 30+
            // Entry[] resize allocations accounting for most of the 386 MB HashSet<ulong> allocation cost.
            // processedStaticMethodTables and processedStaticDelegates are still needed and are
            // pre-sized to avoid resize churn (unique type count and static delegate count are small).
            var processedStaticMethodTables = new HashSet<ulong>(capacity: 64);
            var processedStaticDelegates    = new HashSet<ulong>(capacity: 64);
            var appDomains = heap.Runtime.AppDomains;
            var rootHints = BuildRootHintMap(heap, cache);
            var scanCounter = new ObjectScanCounter("scanning event handlers", progress);

            // Per-MethodTable delegate-field presence cache: one ClrType field inspection per unique
            // type (thousands), O(1) dictionary lookup for every subsequent object of the same type.
            // Avoids heap.GetObject() for the ~99% of 87M objects whose type has no delegate fields.
            var mtHasDelegateFields = new Dictionary<ulong, bool>(capacity: 4096);

            foreach (HeapEntry entry in EnumerateEventEntries(heap, cache))
            {
                scanCounter.Tick();

                ulong objectAddress = entry.Address;
                if (objectAddress == 0)
                    continue;

                // Fast-path: skip objects whose MethodTable is known to have no delegate fields.
                ulong methodTableFast = entry.MethodTable;
                if (methodTableFast != 0)
                {
                    if (!mtHasDelegateFields.TryGetValue(methodTableFast, out bool hasDelegateFields))
                    {
                        ClrType? mtType = heap.GetTypeByMethodTable(methodTableFast);
                        hasDelegateFields = mtType != null
                            && !TypeFilterHelper.IsCompilerGenerated(mtType.Name)
                            && HasDelegateFields(mtType);
                        mtHasDelegateFields[methodTableFast] = hasDelegateFields;
                    }
                    if (!hasDelegateFields)
                        continue;
                }

                ClrObject obj = heap.GetObject(objectAddress);
                if (!obj.IsValid)
                    continue;

                if (obj.Type == null || TypeFilterHelper.IsSystemType(obj.Type.Name)
                    || TypeFilterHelper.IsCompilerGenerated(obj.Type.Name))
                    continue;

                bool hadEventField = false;

                int minSubscribers = options.MinSubscribers;
                bool includeNonLeaking = options.IncludeNonLeakingEvents;

                // Process instance event fields
                foreach (ClrInstanceField field in obj.Type.Fields)
                {
                    if (TypeFilterHelper.IsDelegateType(field.Type)
                        && IsLikelyEventField(obj.Type, field.Name)
                        && !TypeFilterHelper.IsCompilerGenerated(field.Name))
                    {
                        eventsScanned++;
                        hadEventField = true;
                        var subscribers = GetEventSubscribers(heap, obj.Address, field);

                        if (subscribers.Count > 0 && (includeNonLeaking || subscribers.Count >= minSubscribers))
                        {
                            leaks.Add(CreateLeakInfo(
                                publisherAddress: obj.Address,
                                publisherType: obj.Type.Name ?? StringConstants.UnknownType,
                                eventFieldName: field.Name ?? StringConstants.UnknownType,
                                isStatic: false,
                                subscribers,
                                rootHints,
                                options));
                        }
                    }
                }

                // Process static event fields once per unique type
                ulong methodTable = obj.Type.MethodTable;
                if (methodTable != 0 && processedStaticMethodTables.Add(methodTable))
                {
                    foreach (ClrStaticField field in obj.Type.StaticFields)
                    {
                        if (TypeFilterHelper.IsDelegateType(field.Type)
                            && IsLikelyEventField(obj.Type, field.Name)
                            && !TypeFilterHelper.IsCompilerGenerated(field.Name))
                        {
                            eventsScanned++;
                            hadEventField = true;
                            var subscribers = GetStaticEventSubscribers(heap, field, appDomains, processedStaticDelegates);

                            if (subscribers.Count > 0 && (includeNonLeaking || subscribers.Count >= minSubscribers))
                            {
                                leaks.Add(CreateLeakInfo(
                                    publisherAddress: 0,
                                    publisherType: obj.Type.Name ?? StringConstants.UnknownType,
                                    eventFieldName: field.Name ?? StringConstants.UnknownType,
                                    isStatic: true,
                                    subscribers,
                                    rootHints,
                                    options));
                            }
                        }
                    }
                }

                if (hadEventField) publisherInstances++;
            }

            // Cover static-only publisher types by reading static roots directly.
            FindStaticRootOnlyEventLeaks(heap, cache, processedStaticDelegates, rootHints, options, leaks, ref eventsScanned);

            scanCounter.Complete();

            return leaks;
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
                    return [];

                ClrObject publisher = heap.GetObject(publisherAddress);
                if (!publisher.IsValid)
                    return [];

                ClrObject eventDelegate = eventField.ReadObject(publisher, interior: false);
                if (!eventDelegate.IsValid)
                    return [];

                return ExtractSubscribersFromDelegateAddress(heap, eventDelegate.Address);
            }
            catch
            {
                return [];
            }
        }

        private static List<SubscriberInfo> GetStaticEventSubscribers(ClrHeap heap, ClrStaticField field, IReadOnlyList<ClrAppDomain> appDomains, HashSet<ulong>? processedStaticDelegates = null)
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

        private static EventLeakInfo CreateLeakInfo(
            ulong publisherAddress,
            string publisherType,
            string eventFieldName,
            bool isStatic,
            List<SubscriberInfo> subscribers,
            Dictionary<ulong, string> rootHints,
            EventLeakOptions options,
            string? preferredRootHint = null)
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

            return new EventLeakInfo
            {
                PublisherAddress = publisherAddress,
                PublisherType = publisherType,
                EventFieldName = eventFieldName,
                IsStatic = isStatic,
                SubscriberCount = subscribers.Count,
                Subscribers = subscribers,
                RootHint = rootHint,
                SeverityScore = CalculateSeverity(isStatic, subscribers.Count, rootHint, options)
            };
        }

        internal static int CalculateSeverity(bool isStatic, int subscriberCount, string rootHint, EventLeakOptions options)
        {
            int score = subscriberCount;
            if (subscriberCount >= options.SeveritySubscriberThreshold) score += options.SeveritySubscriberBonus;
            if (isStatic) score += options.SeverityStaticPublisherBonus;
            if (!string.IsNullOrEmpty(rootHint)) score += options.SeverityRootHintBonus;
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

        private static void FindStaticRootOnlyEventLeaks(
            ClrHeap heap,
            IHeapAnalysisCache? cache,
            HashSet<ulong> processedStaticDelegates,
            Dictionary<ulong, string> rootHints,
            EventLeakOptions options,
            List<EventLeakInfo> leaks,
            ref int eventsScanned)
        {
            IReadOnlyList<(string RootKind, ulong Address)> roots;
            if (cache is not null)
            {
                roots = cache.GetOrBuildValidRoots(heap);
            }
            else
            {
                var tmp = new List<(string RootKind, ulong Address)>();
                foreach (ClrRoot r in heap.EnumerateRoots())
                {
                    tmp.Add((r.RootKind.ToString(), r.Object.Address));
                }
                roots = tmp;
            }

            foreach (var root in roots)
            {
                string rootKind = root.RootKind ?? string.Empty;
                if (!rootKind.Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                ulong rootAddress = root.Address;
                if (rootAddress == 0)
                    continue;

                ClrObject rootObj = heap.GetObject(rootAddress);
                if (!rootObj.IsValid || rootObj.Type == null || !TypeFilterHelper.IsDelegateType(rootObj.Type))
                    continue;

                if (!processedStaticDelegates.Add(rootObj.Address))
                    continue;

                eventsScanned++;

                string rootDescription = cache?.GetRootDescription(rootAddress) ?? (rootKind + " @ 0x" + rootAddress.ToString("X"));
                ParseRootPublisher(rootDescription, out string publisherType, out string eventFieldName);

                if (TypeFilterHelper.IsCompilerGenerated(publisherType)
                    || TypeFilterHelper.IsCompilerGenerated(eventFieldName))
                {
                    continue;
                }

                var subscribers = ExtractSubscribersFromDelegateAddress(heap, rootObj.Address);
                if (subscribers.Count == 0 || (!options.IncludeNonLeakingEvents && subscribers.Count < options.MinSubscribers))
                    continue;

                leaks.Add(CreateLeakInfo(
                    publisherAddress: 0,
                    publisherType,
                    eventFieldName,
                    isStatic: true,
                    subscribers,
                    rootHints,
                    options,
                    preferredRootHint: rootDescription));
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

        private bool IsLikelyEventField(ClrType ownerType, string? fieldName)
        {
            if (string.IsNullOrEmpty(fieldName))
                return false;

            if (TypeFilterHelper.IsCompilerGenerated(fieldName))
                return false;

            var eventNames = GetEventNames(ownerType);
            if (eventNames.Count == 0)
                return true;

            return eventNames.Contains(fieldName);
        }

        private HashSet<string> GetEventNames(ClrType type)
        {
            string cacheKey = type.Name ?? StringConstants.UnknownType;

            lock (_eventNameCacheLock)
            {
                if (_eventNameCache.TryGetValue(cacheKey, out var cached))
                    return cached;

                // Step 1: collect only the concrete type's own add_/remove_ pairs.
                // ClrType.Methods returns methods declared on this specific type only.
                var ownAddNames    = new HashSet<string>(StringComparer.Ordinal);
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

                // Step 2: if this type declares NO own events, return empty so that
                // IsLikelyEventField falls through to the all-pass branch.
                // Walking the hierarchy here would convert previously-empty (all-pass) types
                // into non-empty (strict) types, silently dropping all non-matching delegate
                // fields (e.g. inherited backing fields whose add_/remove_ are only in the base
                // type's method table and thus never visible for this concrete type lookup).
                if (ownEvents.Count == 0)
                {
                    _eventNameCache[cacheKey] = ownEvents; // empty → all-pass
                    return ownEvents;
                }

                // Step 3: the concrete type HAS its own events, so eventNames is non-empty and
                // IsLikelyEventField will be strict. Walk the base hierarchy and add inherited
                // events so that inherited delegate backing fields (which ARE in obj.Type.Fields
                // because ClrMD exposes the full heap layout) are not incorrectly filtered out.
                var allAddNames    = new HashSet<string>(ownAddNames,    StringComparer.Ordinal);
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

                _eventNameCache[cacheKey] = names;
                return names;
            }
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
        private static List<SubscriberInfo> ExtractSubscribersFromDelegateAddress(ClrHeap heap, ulong delegateAddress)
        {
            if (delegateAddress == 0)
                return [];

            ClrObject eventDelegate = heap.GetObject(delegateAddress);
            return ExtractSubscribersFromDelegateObject(eventDelegate);
        }

        private static List<SubscriberInfo> ExtractSubscribersFromDelegateObject(ClrObject eventDelegate)
        {
            var subscribers = new List<SubscriberInfo>();

            if (!eventDelegate.IsValid || eventDelegate.Type == null)
                return subscribers;

            var invocationListField = DelegateHelper.GetCachedField(eventDelegate.Type, StringConstants.DelegateInvocationListField);
            if (invocationListField != null)
            {
                ClrObject invocationList = invocationListField.ReadObject(eventDelegate, interior: false);
                if (invocationList.IsValid && invocationList.IsArray)
                {
                    ExtractMulticastSubscribers(eventDelegate, subscribers);
                    return subscribers;
                }
            }

            ExtractSingleSubscriber(eventDelegate, subscribers);
            return subscribers;
        }

        private static void ExtractMulticastSubscribers(ClrObject eventDelegate, List<SubscriberInfo> subscribers)
        {
            var invocationListField = DelegateHelper.GetCachedField(eventDelegate.Type, StringConstants.DelegateInvocationListField);
            if (invocationListField == null)
                return;

            ClrObject invocationList = invocationListField.ReadObject(eventDelegate, interior: false);

            if (!invocationList.IsValid || !invocationList.IsArray)
                return;

            var array = invocationList.AsArray();
            for (int i = 0; i < array.Length; i++)
            {
                ClrObject delegateObj = array.GetObjectValue(i);
                if (delegateObj.IsValid)
                {
                    ExtractSingleSubscriber(delegateObj, subscribers);
                }
            }
        }

        private static void ExtractSingleSubscriber(ClrObject delegateObj, List<SubscriberInfo> subscribers)
        {
            if (delegateObj.Type == null)
                return;

            var targetField = DelegateHelper.GetCachedField(delegateObj.Type, StringConstants.DelegateTargetField);
            if (targetField == null)
                return;

            ClrObject target = targetField.ReadObject(delegateObj, interior: false);
            if (target.IsValid && target.Type != null)
            {
                subscribers.Add(new SubscriberInfo
                {
                    Address = target.Address,
                    Type = target.Type.Name ?? StringConstants.UnknownType
                });
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
                        Type = StringConstants.StaticMethodSubscriber
                    });
                }
            }
        }
    }
}


