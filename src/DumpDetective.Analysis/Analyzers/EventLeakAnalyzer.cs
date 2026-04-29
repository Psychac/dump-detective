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
        private const int TopSubscriberTypesToShow = 5;
        private const int TopDetailedInstancesPerGroup = 5;
        private const int SeveritySubscriberThreshold = 10;
        private const int SeveritySubscriberBonus = 5;
        private const int SeverityStaticPublisherBonus = 10;
        private const int SeverityRootHintBonus = 5;

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
            int minSubscribers = options.MinSubscribers;
            var eventLeaks = FindEventLeaks(heap, cache, minSubscribers, progress);

            if (eventLeaks.Count == 0)
            {
                return new EventLeakDomainResult(0, 0, 0, 0);
            }

            var groupedLeaks = GroupEventLeaks(eventLeaks);

            int totalSubscribers = groupedLeaks.Sum(g => g.TotalSubscribers);
            int staticLeaks = groupedLeaks.Count(g => g.IsStatic);
            int instanceLeaks = groupedLeaks.Count(g => !g.IsStatic);
            var topPublisherEvents = groupedLeaks
                .OrderByDescending(g => g.TotalSubscribers)
                .Take(10)
                .Select(g => new NameCountEntry($"{g.PublisherType}.{g.EventFieldName}", g.TotalSubscribers))
                .ToList();

            var topLeakGroups = groupedLeaks
                .Select(g => new EventLeakGroupSnapshot(
                    g.PublisherType,
                    g.EventFieldName,
                    g.IsStatic,
                    g.SeverityScore,
                    g.InstanceCount,
                    g.TotalSubscribers,
                    g.AverageSubscribers,
                    g.MinSubscribers,
                    g.MaxSubscribers,
                    g.Instances
                        .SelectMany(i => i.Subscribers)
                        .GroupBy(s => s.Type)
                        .OrderByDescending(x => x.Count())
                        .Take(TopSubscriberTypesToShow)
                        .Select(x => new NameCountEntry(x.Key, x.Count()))
                        .ToList()))
                .ToList();

            var topLeakInstances = groupedLeaks
                .SelectMany(g => g.Instances.Select(i => new EventLeakInstanceSnapshot(
                    g.PublisherType,
                    g.EventFieldName,
                    g.IsStatic,
                    i.PublisherAddress,
                    i.SeverityScore,
                    i.SubscriberCount,
                    string.IsNullOrWhiteSpace(i.RootHint) ? null : i.RootHint,
                    i.Subscribers
                        .GroupBy(s => s.Type)
                        .OrderByDescending(x => x.Count())
                        .Take(TopSubscriberTypesToShow)
                        .Select(x => $"{x.Key} ({x.Count():N0})")
                        .ToList())))
                .OrderByDescending(i => i.SeverityScore)
                .ThenByDescending(i => i.SubscriberCount)
                .ToList();

            return new EventLeakDomainResult(
                    groupedLeaks.Count,
                    totalSubscribers,
                    staticLeaks,
                    instanceLeaks,
                    topPublisherEvents,
                    topLeakGroups,
                    topLeakInstances);
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

        private List<EventLeakInfo> FindEventLeaks(ClrHeap heap, IHeapAnalysisCache? cache, int minSubscribers, IProgress<AnalyzerProgressReport>? progress)
        {
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

            foreach (HeapEntry entry in EnumerateEventEntries(heap, cache))
            {
                scanCounter.Tick();

                ulong objectAddress = entry.Address;
                if (objectAddress == 0)
                    continue;

                ClrObject obj = heap.GetObject(objectAddress);
                if (!obj.IsValid)
                    continue;

                if (obj.Type == null || TypeFilterHelper.IsSystemType(obj.Type.Name)
                    || TypeFilterHelper.IsCompilerGenerated(obj.Type.Name))
                    continue;

                // Process instance event fields
                foreach (ClrInstanceField field in obj.Type.Fields)
                {
                    if (TypeFilterHelper.IsDelegateType(field.Type)
                        && IsLikelyEventField(obj.Type, field.Name)
                        && !TypeFilterHelper.IsCompilerGenerated(field.Name))
                    {
                        var subscribers = GetEventSubscribers(heap, obj.Address, field);

                        if (subscribers.Count > 0 && subscribers.Count >= minSubscribers)
                        {
                            leaks.Add(CreateLeakInfo(
                                publisherAddress: obj.Address,
                                publisherType: obj.Type.Name ?? StringConstants.UnknownType,
                                eventFieldName: field.Name ?? StringConstants.UnknownType,
                                isStatic: false,
                                subscribers,
                                rootHints));
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
                            var subscribers = GetStaticEventSubscribers(heap, field, appDomains, processedStaticDelegates);

                            if (subscribers.Count > 0 && subscribers.Count >= minSubscribers)
                            {
                                leaks.Add(CreateLeakInfo(
                                    publisherAddress: 0,
                                    publisherType: obj.Type.Name ?? StringConstants.UnknownType,
                                    eventFieldName: field.Name ?? StringConstants.UnknownType,
                                    isStatic: true,
                                    subscribers,
                                    rootHints));
                            }
                        }
                    }
                }
            }

            // Cover static-only publisher types by reading static roots directly.
            FindStaticRootOnlyEventLeaks(heap, cache, processedStaticDelegates, rootHints, minSubscribers, leaks);

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

        private List<EventGroupInfo> GroupEventLeaks(List<EventLeakInfo> eventLeaks)
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
            var seen = new HashSet<ulong>();

            foreach (var appDomain in appDomains)
            {
                try
                {
                    ClrObject eventDelegate = field.ReadObject(appDomain);
                    if (eventDelegate.IsValid)
                        processedStaticDelegates?.Add(eventDelegate.Address);

                    var domainSubscribers = ExtractSubscribersFromDelegateAddress(heap, eventDelegate.Address);

                    foreach (var subscriber in domainSubscribers)
                    {
                        if (subscriber.Address != 0 && seen.Add(subscriber.Address))
                        {
                            subscribers.Add(subscriber);
                        }
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
                SeverityScore = CalculateSeverity(isStatic, subscribers.Count, rootHint)
            };
        }

        private static int CalculateSeverity(bool isStatic, int subscriberCount, string rootHint)
        {
            int score = subscriberCount;
            if (subscriberCount >= SeveritySubscriberThreshold) score += SeveritySubscriberBonus;
            if (isStatic) score += SeverityStaticPublisherBonus;
            if (!string.IsNullOrEmpty(rootHint)) score += SeverityRootHintBonus;
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
            int minSubscribers,
            List<EventLeakInfo> leaks)
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

                string rootDescription = cache?.GetRootDescription(rootAddress) ?? (rootKind + " @ 0x" + rootAddress.ToString("X"));
                ParseRootPublisher(rootDescription, out string publisherType, out string eventFieldName);

                if (TypeFilterHelper.IsCompilerGenerated(publisherType)
                    || TypeFilterHelper.IsCompilerGenerated(eventFieldName))
                {
                    continue;
                }

                var subscribers = ExtractSubscribersFromDelegateAddress(heap, rootObj.Address);
                if (subscribers.Count == 0 || subscribers.Count < minSubscribers)
                    continue;

                leaks.Add(CreateLeakInfo(
                    publisherAddress: 0,
                    publisherType,
                    eventFieldName,
                    isStatic: true,
                    subscribers,
                    rootHints,
                    preferredRootHint: rootDescription));
            }
        }

        private static void ParseRootPublisher(string rootDescription, out string publisherType, out string eventFieldName)
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

                var addNames = new HashSet<string>(StringComparer.Ordinal);
                var removeNames = new HashSet<string>(StringComparer.Ordinal);

                foreach (var method in type.Methods)
                {
                    var name = method.Name;
                    if (name == null)
                        continue;

                    if (name.StartsWith("add_", StringComparison.Ordinal) && name.Length > 4)
                        addNames.Add(name[4..]);
                    else if (name.StartsWith("remove_", StringComparison.Ordinal) && name.Length > 7)
                        removeNames.Add(name[7..]);
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var eventName in addNames)
                {
                    if (removeNames.Contains(eventName))
                        names.Add(eventName);
                }

                _eventNameCache[cacheKey] = names;
                return names;
            }
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
        }
    }
}


