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
                if (!obj.IsValid || processedObjects.Contains(obj.Address))
                    continue;

                processedObjects.Add(obj.Address);

                if (obj.Type == null || IsSystemType(obj.Type.Name))
                    continue;

                foreach (ClrInstanceField field in obj.Type.Fields)
                {
                    if (IsEventField(field))
                    {
                        var subscribers = GetEventSubscribers(obj, field, heap);

                        if (subscribers.Count > 0 && subscribers.Count >= minSubscribers)
                        {
                            leaks.Add(new EventLeakInfo
                            {
                                PublisherAddress = obj.Address,
                                PublisherType = obj.Type.Name ?? "Unknown",
                                EventFieldName = field.Name ?? "Unknown",
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
            return eventLeaks
                .GroupBy(e => new { e.PublisherType, e.EventFieldName })
                .Select(g => new EventGroupInfo
                {
                    PublisherType = g.Key.PublisherType,
                    EventFieldName = g.Key.EventFieldName,
                    InstanceCount = g.Count(),
                    TotalSubscribers = g.Sum(x => x.SubscriberCount),
                    AverageSubscribers = g.Average(x => x.SubscriberCount),
                    MaxSubscribers = g.Max(x => x.SubscriberCount),
                    MinSubscribers = g.Min(x => x.SubscriberCount),
                    Instances = g.ToList()
                })
                .OrderByDescending(g => g.TotalSubscribers)
                .ToList();
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

                var subscriberTypes = group.Instances
                    .SelectMany(i => i.Subscribers)
                    .GroupBy(s => s.Type)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .ToList();

                if (subscriberTypes.Any())
                {
                    _writer.WriteLine($"  Top Subscriber Types:");
                    foreach (var subType in subscriberTypes)
                    {
                        _writer.WriteLine($"    - {subType.Key} ({subType.Count()} instance(s))");
                    }
                }
            }
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

                foreach (var leak in group.Instances.OrderByDescending(l => l.SubscriberCount).Take(5))
                {
                    _writer.WriteLine($"  Instance #{detailCount++}");
                    _writer.WriteLine($"    Address: 0x{leak.PublisherAddress:X}");
                    _writer.WriteLine($"    Subscribers: {leak.SubscriberCount}");

                    if (leak.Subscribers.Count > 0)
                    {
                        _writer.WriteLine($"    Subscriber Types:");
                        foreach (var subscriber in leak.Subscribers.Take(5))
                        {
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

        private static bool IsSystemType(string? typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return false;

            string[] systemNamespaces =
            {
                "System.",
                "Microsoft.",
                "MS.",
                "Internal.",
                "Windows.",
                "Interop.",
                "FxResources.",
                "System_Private_CoreLib"
            };

            return systemNamespaces.Any(ns => typeName.StartsWith(ns, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsEventField(ClrInstanceField field)
        {
            if (field.Type == null)
                return false;

            string? typeName = field.Type.Name;

            return typeName != null &&
                   (typeName.Contains("EventHandler") ||
                    typeName.Contains("Action") ||
                    typeName.Contains("Func") ||
                    typeName.Contains("Delegate"));
        }

        private static List<SubscriberInfo> GetEventSubscribers(ClrObject obj, ClrInstanceField eventField, ClrHeap heap)
        {
            var subscribers = new List<SubscriberInfo>();

            try
            {
                ClrObject eventDelegate = eventField.ReadObject(obj, interior: false);

                if (!eventDelegate.IsValid)
                    return subscribers;

                if (eventDelegate.Type?.Name?.Contains("MulticastDelegate") == true)
                {
                    var invocationListField = eventDelegate.Type.GetFieldByName("_invocationList");
                    if (invocationListField != null)
                    {
                        ClrObject invocationList = invocationListField.ReadObject(eventDelegate, interior: false);

                        if (invocationList.IsValid && invocationList.IsArray)
                        {
                            for (int i = 0; i < invocationList.AsArray().Length; i++)
                            {
                                ClrObject delegateObj = invocationList.AsArray().GetObjectValue(i);
                                if (delegateObj.IsValid)
                                {
                                    var targetField = delegateObj.Type?.GetFieldByName("_target");
                                    if (targetField != null)
                                    {
                                        ClrObject target = targetField.ReadObject(delegateObj, interior: false);
                                        if (target.IsValid && target.Type != null)
                                        {
                                            subscribers.Add(new SubscriberInfo
                                            {
                                                Address = target.Address,
                                                Type = target.Type.Name ?? "Unknown"
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    var targetField = eventDelegate.Type?.GetFieldByName("_target");
                    if (targetField != null)
                    {
                        ClrObject target = targetField.ReadObject(eventDelegate, interior: false);
                        if (target.IsValid && target.Type != null)
                        {
                            subscribers.Add(new SubscriberInfo
                            {
                                Address = target.Address,
                                Type = target.Type.Name ?? "Unknown"
                            });
                        }
                    }
                }
            }
            catch
            {
            }

            return subscribers;
        }
    }
}
