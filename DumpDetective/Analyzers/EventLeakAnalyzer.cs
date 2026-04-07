using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class EventLeakAnalyzer
    {
        private readonly OutputWriter _writer;

        public EventLeakAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrHeap heap, int minSubscribers = 0)
        {
            var eventLeaks = FindEventLeaks(heap, minSubscribers);

            if (eventLeaks.Count == 0)
            {
                _writer.WriteLine("No event leaks detected!");
                return;
            }

            var groupedLeaks = GroupEventLeaks(eventLeaks);
            PrintSummary(eventLeaks, groupedLeaks);
            PrintDetailedInstances(groupedLeaks);
        }

        private List<EventLeakInfo> FindEventLeaks(ClrHeap heap, int minSubscribers)
        {
            var leaks = new List<EventLeakInfo>();
            var processedObjects = new HashSet<ulong>();

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                // Use Add's return value - returns false if already exists
                if (!obj.IsValid || !processedObjects.Add(obj.Address))
                    continue;

                if (obj.Type == null || TypeFilterHelper.IsSystemType(obj.Type.Name))
                    continue;

                // Cache fields enumeration
                var fields = obj.Type.Fields;
                bool hasEventFields = false;

                // Quick check if type has any event-like fields before processing
                foreach (var field in fields)
                {
                    if (field.Type?.Name != null && TypeFilterHelper.IsEventField(field.Type.Name))
                    {
                        hasEventFields = true;
                        break;
                    }
                }

                if (!hasEventFields)
                    continue;

                // Now process event fields
                foreach (ClrInstanceField field in fields)
                {
                    if (field.Type?.Name != null && TypeFilterHelper.IsEventField(field.Type.Name))
                    {
                        var subscribers = GetEventSubscribers(obj, field);

                        if (subscribers.Count > 0 && subscribers.Count >= minSubscribers)
                        {
                            leaks.Add(new EventLeakInfo
                            {
                                PublisherAddress = obj.Address,
                                PublisherType = obj.Type.Name ?? StringConstants.UnknownType,
                                EventFieldName = field.Name ?? StringConstants.UnknownType,
                                SubscriberCount = subscribers.Count,
                                Subscribers = subscribers
                            });
                        }
                    }
                }
            }

            return leaks;
        }

        private List<EventGroupInfo> GroupEventLeaks(List<EventLeakInfo> eventLeaks)
        {
            // Pre-allocate with expected capacity
            var groups = new Dictionary<(string PublisherType, string EventFieldName), List<EventLeakInfo>>();

            // Manual grouping to avoid LINQ overhead
            foreach (var leak in eventLeaks)
            {
                var key = (leak.PublisherType, leak.EventFieldName);

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

                foreach (var instance in instances)
                {
                    int count = instance.SubscriberCount;
                    totalSubs += count;
                    if (count < minSubs) minSubs = count;
                    if (count > maxSubs) maxSubs = count;
                }

                result.Add(new EventGroupInfo
                {
                    PublisherType = kvp.Key.PublisherType,
                    EventFieldName = kvp.Key.EventFieldName,
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
                _writer.WriteLine($"\n[{groupCount++}] {group.PublisherType}.{group.EventFieldName}");
                _writer.WriteLine($"  Instance Count: {group.InstanceCount}");
                _writer.WriteLine($"  Total Subscribers: {group.TotalSubscribers}");
                _writer.WriteLine($"  Average Subscribers: {group.AverageSubscribers:F2}");
                _writer.WriteLine($"  Min/Max Subscribers: {group.MinSubscribers}/{group.MaxSubscribers}");

                // Optimized subscriber type grouping
                var subscriberTypeCounts = GetTopSubscriberTypes(group.Instances, topCount: 5);

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
            _writer.WriteLine($"\n{new string('=', 80)}");
            _writer.WriteLine("\nDETAILED INSTANCES:");
            _writer.WriteLine(new string('=', 80));

            int detailCount = 1;
            foreach (var group in groupedLeaks)
            {
                _writer.WriteLine($"\n[{group.PublisherType}.{group.EventFieldName}] - {group.InstanceCount} instance(s)");
                _writer.WriteSeparator();

                // Sort instances and take top 5
                var topInstances = group.Instances
                    .OrderByDescending(l => l.SubscriberCount)
                    .Take(5)
                    .ToList();

                foreach (var leak in topInstances)
                {
                    _writer.WriteLine($"  Instance #{detailCount++}");
                    _writer.WriteLine($"    Address: 0x{leak.PublisherAddress:X}");
                    _writer.WriteLine($"    Subscribers: {leak.SubscriberCount}");

                    if (leak.Subscribers.Count > 0)
                    {
                        _writer.WriteLine($"    Subscriber Types:");

                        int displayCount = Math.Min(5, leak.Subscribers.Count);
                        for (int i = 0; i < displayCount; i++)
                        {
                            var subscriber = leak.Subscribers[i];
                            _writer.WriteLine($"      - {subscriber.Type} (0x{subscriber.Address:X})");
                        }

                        if (leak.Subscribers.Count > 5)
                        {
                            _writer.WriteLine($"      ... and {leak.Subscribers.Count - 5} more");
                        }
                    }
                    _writer.WriteLine(string.Empty);
                }

                if (group.InstanceCount > 5)
                {
                    _writer.WriteLine($"  ... and {group.InstanceCount - 5} more instance(s)");
                    _writer.WriteLine(string.Empty);
                }
            }
        }

        private static List<SubscriberInfo> GetEventSubscribers(ClrObject obj, ClrInstanceField eventField)
        {
            var subscribers = new List<SubscriberInfo>();

            try
            {
                ClrObject eventDelegate = eventField.ReadObject(obj, interior: false);

                if (!eventDelegate.IsValid || eventDelegate.Type == null)
                    return subscribers;

                // Check if it's a multicast delegate
                if (eventDelegate.Type.Name?.Contains(StringConstants.MulticastDelegateName, StringComparison.Ordinal) == true)
                {
                    ExtractMulticastSubscribers(eventDelegate, subscribers);
                }
                else
                {
                    ExtractSingleSubscriber(eventDelegate, subscribers);
                }
            }
            catch
            {
                // Silently handle errors in delegate inspection
            }

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
