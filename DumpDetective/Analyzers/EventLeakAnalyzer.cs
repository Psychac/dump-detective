using Microsoft.Diagnostics.Runtime;
using DumpDetective.Configuration;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class EventLeakAnalyzer
    {
        private const int TopSubscriberTypesToShow = 5;
        private const int TopDetailedInstancesPerGroup = 5;
        private const int SeveritySubscriberThreshold = 10;
        private const int SeveritySubscriberBonus = 5;
        private const int SeverityStaticPublisherBonus = 10;
        private const int SeverityRootHintBonus = 5;

        private readonly OutputWriter _writer;
        private readonly AnalysisConfiguration _config;
        private static readonly Dictionary<string, HashSet<string>> _eventNameCache = new(StringComparer.Ordinal);
        private static readonly object _eventNameCacheLock = new();

        public EventLeakAnalyzer(OutputWriter writer, AnalysisConfiguration config)
        {
            _writer = writer;
            _config = config;
        }

        public IReadOnlyList<InsightFinding> Analyze(ClrHeap heap)
        {
            int minSubscribers = _config.EventLeakMinSubscribers;
            var eventLeaks = FindEventLeaks(heap, minSubscribers);
            var findings = new List<InsightFinding>(capacity: 5);

            if (eventLeaks.Count == 0)
            {
                _writer.WriteLine("No event leaks detected!");
                findings.Add(new InsightFinding(
                    Analyzer: nameof(EventLeakAnalyzer),
                    Category: "Leak",
                    Severity: FindingSeverity.Info,
                    Title: "No event-leak signatures detected",
                    Evidence: $"No event instances exceeded the {minSubscribers:N0} subscriber threshold.",
                    Recommendation: "No immediate action required for event retention patterns.",
                    Tags: ["event", "leak", "subscriptions"],
                    MetricValue: 0,
                    MetricUnit: "subscribers"));
                return findings;
            }

            var groupedLeaks = GroupEventLeaks(eventLeaks);
            PrintSummary(eventLeaks, groupedLeaks);
            PrintDetailedInstances(groupedLeaks);
            AddFindings(findings, groupedLeaks);
            return findings;
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

        private List<EventLeakInfo> FindEventLeaks(ClrHeap heap, int minSubscribers)
        {
            var leaks = new List<EventLeakInfo>();
            var processedObjects = new HashSet<ulong>();
            var processedStaticTypes = new HashSet<string>();
            var processedStaticDelegates = new HashSet<ulong>();
            var appDomains = heap.Runtime.AppDomains;
            var rootHints = BuildRootHintMap(heap);
            var scanCounter = new ObjectScanCounter("Event leak object scan");

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                scanCounter.Tick();

                if (!obj.IsValid || !processedObjects.Add(obj.Address))
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
                        var subscribers = GetEventSubscribers(obj, field);

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
                if (processedStaticTypes.Add(obj.Type.Name ?? string.Empty))
                {
                    foreach (ClrStaticField field in obj.Type.StaticFields)
                    {
                        if (TypeFilterHelper.IsDelegateType(field.Type)
                            && IsLikelyEventField(obj.Type, field.Name)
                            && !TypeFilterHelper.IsCompilerGenerated(field.Name))
                        {
                            var subscribers = GetStaticEventSubscribers(field, appDomains, processedStaticDelegates);

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
            FindStaticRootOnlyEventLeaks(heap, processedStaticDelegates, rootHints, minSubscribers, leaks);

            scanCounter.Complete();

            return leaks;
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

        private void PrintSummary(List<EventLeakInfo> eventLeaks, List<EventGroupInfo> groupedLeaks)
        {
            _writer.WriteLine($"\nFound {eventLeaks.Count} event instance(s) across {groupedLeaks.Count} event type(s):\n");
            _writer.WriteLine("SUMMARY BY EVENT TYPE:");
            _writer.WriteLine(new string('=', 80));

            int groupCount = 1;
            foreach (var group in groupedLeaks)
            {
                _writer.WriteLine($"\n[{groupCount++}] {(group.IsStatic ? "[STATIC] " : "[INSTANCE] ")}{group.PublisherType}.{group.EventFieldName} (Severity: {group.SeverityScore})");
                _writer.WriteLine($"  Instance Count: {group.InstanceCount}");
                _writer.WriteLine($"  Total Subscribers: {group.TotalSubscribers}");
                _writer.WriteLine($"  Severity Score: {group.SeverityScore}");
                _writer.WriteLine($"  Average Subscribers: {group.AverageSubscribers:F2}");
                _writer.WriteLine($"  Min/Max Subscribers: {group.MinSubscribers}/{group.MaxSubscribers}");

                // Optimized subscriber type grouping
                var subscriberTypeCounts = GetTopSubscriberTypes(group.Instances, topCount: TopSubscriberTypesToShow);

                if (subscriberTypeCounts.Count > 0)
                {
                    _writer.WriteLine($"  Top Subscriber Types:");
                    foreach (var (type, count) in subscriberTypeCounts)
                    {
                        _writer.WriteLine($"    - {type} ({count} instance(s))");
                    }
                }
            }
        }

        private List<(string Type, int Count)> GetTopSubscriberTypes(List<EventLeakInfo> instances, int topCount)
        {
            var typeCounts = new Dictionary<string, int>();

            foreach (var instance in instances)
            {
                foreach (var subscriber in instance.Subscribers)
                {
                    if (!typeCounts.TryGetValue(subscriber.Type, out int count))
                    {
                        typeCounts[subscriber.Type] = 1;
                    }
                    else
                    {
                        typeCounts[subscriber.Type] = count + 1;
                    }
                }
            }

            // Convert to list and sort
            var result = new List<(string Type, int Count)>(typeCounts.Count);
            foreach (var kvp in typeCounts)
            {
                result.Add((kvp.Key, kvp.Value));
            }

            result.Sort((a, b) => b.Count.CompareTo(a.Count));

            return result.Count > topCount ? result.GetRange(0, topCount) : result;
        }

        private void PrintDetailedInstances(List<EventGroupInfo> groupedLeaks)
        {
            _writer.WriteLine($"\n{StringConstants.Equals80}");
            _writer.WriteLine("\nDETAILED INSTANCES:");
            _writer.WriteLine(StringConstants.Equals80);

            int detailCount = 1;
            foreach (var group in groupedLeaks)
            {
                _writer.WriteLine($"\n[{(group.IsStatic ? "STATIC" : "INSTANCE")}] {group.PublisherType}.{group.EventFieldName} - {group.InstanceCount} instance(s)");
                _writer.WriteSeparator();

                // Sort instances and take top 5
                var topInstances = group.Instances
                    .OrderByDescending(l => l.SubscriberCount)
                    .Take(TopDetailedInstancesPerGroup)
                    .ToList();

                foreach (var leak in topInstances)
                {
                    _writer.WriteLine($"  Instance #{detailCount++} (Severity: {leak.SeverityScore})");
                    _writer.WriteLine(leak.IsStatic
                        ? "    Address: (static)"
                        : $"    Address: 0x{leak.PublisherAddress:X}");
                    _writer.WriteLine($"    Severity Score: {leak.SeverityScore}");
                    if (!string.IsNullOrEmpty(leak.RootHint))
                        _writer.WriteLine($"    Root Hint: {leak.RootHint}");
                    _writer.WriteLine($"    Subscribers: {leak.SubscriberCount}");

                    if (leak.Subscribers.Count > 0)
                    {
                        _writer.WriteLine($"    Subscriber Types:");

                        int displayCount = Math.Min(TopSubscriberTypesToShow, leak.Subscribers.Count);
                        for (int i = 0; i < displayCount; i++)
                        {
                            var subscriber = leak.Subscribers[i];
                            _writer.WriteLine($"      - {subscriber.Type} (0x{subscriber.Address:X})");
                        }

                        if (leak.Subscribers.Count > TopSubscriberTypesToShow)
                        {
                            _writer.WriteLine($"      ... and {leak.Subscribers.Count - TopSubscriberTypesToShow} more");
                        }
                    }
                    _writer.WriteLine(string.Empty);
                }

                if (group.InstanceCount > TopDetailedInstancesPerGroup)
                {
                    _writer.WriteLine($"  ... and {group.InstanceCount - TopDetailedInstancesPerGroup} more instance(s)");
                    _writer.WriteLine(string.Empty);
                }
            }
        }

        private static List<SubscriberInfo> GetEventSubscribers(ClrObject obj, ClrInstanceField eventField)
        {
            try
            {
                ClrObject eventDelegate = eventField.ReadObject(obj, interior: false);
                return ExtractSubscribersFromDelegate(eventDelegate);
            }
            catch
            {
                return new List<SubscriberInfo>();
            }
        }

        private static List<SubscriberInfo> GetStaticEventSubscribers(ClrStaticField field, IReadOnlyList<ClrAppDomain> appDomains, HashSet<ulong>? processedStaticDelegates = null)
        {
            var subscribers = new List<SubscriberInfo>();
            var seen = new HashSet<(ulong Address, string Type)>();

            foreach (var appDomain in appDomains)
            {
                try
                {
                    ClrObject eventDelegate = field.ReadObject(appDomain);
                    if (eventDelegate.IsValid)
                        processedStaticDelegates?.Add(eventDelegate.Address);

                    var domainSubscribers = ExtractSubscribersFromDelegate(eventDelegate);

                    foreach (var subscriber in domainSubscribers)
                    {
                        if (seen.Add((subscriber.Address, subscriber.Type)))
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

        private static Dictionary<ulong, string> BuildRootHintMap(ClrHeap heap)
        {
            var map = new Dictionary<ulong, string>();

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
            HashSet<ulong> processedStaticDelegates,
            Dictionary<ulong, string> rootHints,
            int minSubscribers,
            List<EventLeakInfo> leaks)
        {
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                if (!root.RootKind.ToString().Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                ClrObject rootObj = root.Object;
                if (!rootObj.IsValid || rootObj.Type == null || !TypeFilterHelper.IsDelegateType(rootObj.Type))
                    continue;

                if (!processedStaticDelegates.Add(rootObj.Address))
                    continue;

                string rootDescription = root.ToString() ?? StringConstants.UnknownType;
                ParseRootPublisher(rootDescription, out string publisherType, out string eventFieldName);

                if (TypeFilterHelper.IsCompilerGenerated(publisherType)
                    || TypeFilterHelper.IsCompilerGenerated(eventFieldName))
                {
                    continue;
                }

                var subscribers = ExtractSubscribersFromDelegate(rootObj);
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

        private static bool IsLikelyEventField(ClrType ownerType, string? fieldName)
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

        private static HashSet<string> GetEventNames(ClrType type)
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
        private static List<SubscriberInfo> ExtractSubscribersFromDelegate(ClrObject eventDelegate)
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
