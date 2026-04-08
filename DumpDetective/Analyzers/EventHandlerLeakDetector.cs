using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class EventHandlerLeakDetector
    {
        private readonly OutputWriter _writer;

        public EventHandlerLeakDetector(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            _writer.WriteHeader("EVENT HANDLER LEAK DETECTION:");
            _writer.WriteLine("Identifying event subscriptions that may be causing memory leaks...\n");

            var leaks = DetectEventHandlerLeaks(heap, cache);

            if (leaks.Count == 0)
            {
                _writer.WriteLine("No event handler leaks detected (good!).");
                _writer.WriteLine($"\n{StringConstants.Equals80}");
                return;
            }

            _writer.WriteLine($"Found {leaks.Count} potential event handler leak(s):\n");

            // Manual sorting - no LINQ allocations
            leaks.Sort((a, b) => b.SubscriberCount.CompareTo(a.SubscriberCount));

            int leakNum = 1;
            int leakCount = 0;
            foreach (var leak in leaks)
            {
                if (leakCount >= 20) break;

                _writer.WriteLine($"[{leakNum++}] EVENT HANDLER LEAK");
                _writer.WriteSeparator();
                _writer.WriteLine($"  Publisher: {leak.PublisherType}");
                _writer.WriteLine($"  Event: {leak.EventName}");
                _writer.WriteLine($"  Publisher Address: 0x{leak.PublisherAddress:X}");
                _writer.WriteLine($"  Subscriber Count: {leak.SubscriberCount}");
                _writer.WriteLine($"  Total Retained Memory: {FormatHelper.FormatBytes(leak.TotalRetainedMemory)}");

                if (leak.SubscriberDetails.Any())
                {
                    _writer.WriteLine($"\n  Subscribers (keeping objects alive):");

                    // Manual grouping - no LINQ allocations
                    var groupedSubscribers = new Dictionary<string, List<EventSubscriberDetail>>();
                    foreach (var sub in leak.SubscriberDetails)
                    {
                        if (!groupedSubscribers.TryGetValue(sub.SubscriberType, out var list))
                        {
                            list = new List<EventSubscriberDetail>();
                            groupedSubscribers[sub.SubscriberType] = list;
                        }
                        list.Add(sub);
                    }

                    // Manual sorting by count
                    var sortedGroups = new List<KeyValuePair<string, List<EventSubscriberDetail>>>(groupedSubscribers);
                    sortedGroups.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));

                    int groupCount = 0;
                    foreach (var group in sortedGroups)
                    {
                        if (groupCount >= 5) break;

                        long totalSize = 0;
                        foreach (var s in group.Value)
                        {
                            totalSize += (long)s.Size;
                        }
                        _writer.WriteLine($"    - {group.Key}: {group.Value.Count} instance(s), {FormatHelper.FormatBytes((ulong)totalSize)}");                        _writer.WriteLine($"    - {group.Key}: {group.Value.Count} instance(s), {FormatHelper.FormatBytes((ulong)totalSize)}");

                        // Show first instance details
                        var first = group.Value[0];
                        _writer.WriteLine($"      Example: 0x{first.SubscriberAddress:X}");

                        if (first.IsStaticRooted)
                        {
                            _writer.WriteLine($"      ⚠️  Rooted by: {first.RootDescription}");
                        }
                        groupCount++;
                    }
                }

                _writer.WriteLine($"\n  💡 LEAK PATTERN:");
                if (leak.IsStaticPublisher)
                {
                    _writer.WriteLine($"     ⚠️  CRITICAL: Static object publishing events!");
                    _writer.WriteLine($"     Problem: Subscribers will NEVER be garbage collected.");
                    _writer.WriteLine($"     Fix: Unsubscribe in Dispose() or use WeakEventManager.");
                }
                else if (leak.HasLongLivedSubscribers)
                {
                    _writer.WriteLine($"     ⚠️  WARNING: Long-lived subscribers detected.");
                    _writer.WriteLine($"     Problem: Publisher cannot be collected while subscribers exist.");
                    _writer.WriteLine($"     Fix: Ensure subscribers unsubscribe when done.");
                }
                else
                {
                    _writer.WriteLine($"     ⚠️  Potential leak: Many subscribers or large objects retained.");
                    _writer.WriteLine($"     Fix: Ensure proper event unsubscription in Dispose/cleanup.");
                }

                _writer.WriteLine($"\n  📝 CODE PATTERN TO FIX:");
                _writer.WriteLine($"     public void Dispose() {{");
                _writer.WriteLine($"         publisher.{leak.EventName} -= OnEventHandler;");
                _writer.WriteLine($"     }}}}");
                _writer.WriteLine(string.Empty);
                leakCount++;
            }

            _writer.WriteLine(StringConstants.Equals80);
        }

        private List<EventHandlerLeak> DetectEventHandlerLeaks(ClrHeap heap, HeapAnalysisCache cache)
        {
            var leaks = new List<EventHandlerLeak>();
            var staticRoots = cache.GetStaticRootedAddresses(heap);

            // Use cached event publishers instead of heap enumeration
            var eventPublishers = cache.GetEventPublishers();

            foreach (var publisherInfo in eventPublishers)
            {
                ClrObject obj = heap.GetObject(publisherInfo.Address);
                if (!obj.IsValid || obj.Type == null)
                    continue;

                bool isStaticRooted = staticRoots.Contains(obj.Address);

                foreach (var field in obj.Type.Fields)
                {
                    if (field.Type?.Name != null && TypeFilterHelper.IsEventField(field.Type.Name))
                    {
                        var subscriberDetails = GetEventSubscriberDetails(heap, obj, field, staticRoots);

                        // Only report if significant subscriber count or memory impact
                        if (subscriberDetails.Count >= 3 || 
                            subscriberDetails.Sum(s => (long)s.Size) > 100 * 1024)
                        {
                            ulong totalRetained = (ulong)subscriberDetails.Sum(s => (long)s.Size);
                            bool hasLongLived = subscriberDetails.Any(s => s.IsStaticRooted);

                            leaks.Add(new EventHandlerLeak
                            {
                                PublisherAddress = obj.Address,
                                PublisherType = obj.Type.Name ?? StringConstants.UnknownType,
                                EventName = field.Name ?? StringConstants.UnknownType,
                                SubscriberCount = subscriberDetails.Count,
                                SubscriberDetails = subscriberDetails,
                                TotalRetainedMemory = totalRetained,
                                IsStaticPublisher = isStaticRooted,
                                HasLongLivedSubscribers = hasLongLived
                            });
                        }
                    }
                }
            }

            return leaks;
        }

        private HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap)
        {
            var staticRooted = new HashSet<ulong>();
            
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                if (root.RootKind.ToString().Contains("Static", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.Object.IsValid)
                    {
                        staticRooted.Add(root.Object.Address);
                    }
                }
            }

            return staticRooted;
        }

        private List<EventSubscriberDetail> GetEventSubscriberDetails(ClrHeap heap, ClrObject obj, 
            ClrInstanceField eventField, HashSet<ulong> staticRoots)
        {
            var subscribers = new List<EventSubscriberDetail>();

            try
            {
                ClrObject eventDelegate = eventField.ReadObject(obj, interior: false);
                if (!eventDelegate.IsValid)
                    return subscribers;

                if (eventDelegate.Type?.Name?.Contains(StringConstants.MulticastDelegateName, StringComparison.Ordinal) == true)
                {
                    var invocationListField = DelegateHelper.GetCachedField(eventDelegate.Type, StringConstants.DelegateInvocationListField);
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
                                    var targetField = DelegateHelper.GetCachedField(delegateObj.Type, StringConstants.DelegateTargetField);
                                    if (targetField != null)
                                    {
                                        ClrObject target = targetField.ReadObject(delegateObj, interior: false);
                                        if (target.IsValid && target.Type != null)
                                        {
                                            bool isStatic = staticRoots.Contains(target.Address);
                                            string rootDesc = isStatic ? FindRootDescription(heap, target.Address) : "";

                                            subscribers.Add(new EventSubscriberDetail
                                            {
                                                SubscriberAddress = target.Address,
                                                SubscriberType = target.Type.Name ?? StringConstants.UnknownType,
                                                Size = target.Size,
                                                IsStaticRooted = isStatic,
                                                RootDescription = rootDesc
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
                    var targetField = DelegateHelper.GetCachedField(eventDelegate.Type, StringConstants.DelegateTargetField);
                    if (targetField != null)
                    {
                        ClrObject target = targetField.ReadObject(eventDelegate, interior: false);
                        if (target.IsValid && target.Type != null)
                        {
                            bool isStatic = staticRoots.Contains(target.Address);
                            string rootDesc = isStatic ? FindRootDescription(heap, target.Address) : "";

                            subscribers.Add(new EventSubscriberDetail
                            {
                                SubscriberAddress = target.Address,
                                SubscriberType = target.Type.Name ?? StringConstants.UnknownType,
                                Size = target.Size,
                                IsStaticRooted = isStatic,
                                RootDescription = rootDesc
                            });
                        }
                    }
                }
            }
            catch { }

            return subscribers;
        }

        private string FindRootDescription(ClrHeap heap, ulong address)
        {
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                if (root.Object.Address == address && 
                    root.RootKind.ToString().Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase))
                {
                    return root.ToString() ?? "Static Root";
                }
            }
            return "Static Root";
        }
    }

    internal class EventHandlerLeak
    {
        public ulong PublisherAddress { get; set; }
        public string PublisherType { get; set; } = string.Empty;
        public string EventName { get; set; } = string.Empty;
        public int SubscriberCount { get; set; }
        public List<EventSubscriberDetail> SubscriberDetails { get; set; } = new();
        public ulong TotalRetainedMemory { get; set; }
        public bool IsStaticPublisher { get; set; }
        public bool HasLongLivedSubscribers { get; set; }
    }

    internal class EventSubscriberDetail
    {
        public ulong SubscriberAddress { get; set; }
        public string SubscriberType { get; set; } = string.Empty;
        public ulong Size { get; set; }
        public bool IsStaticRooted { get; set; }
        public string RootDescription { get; set; } = string.Empty;
    }
}
