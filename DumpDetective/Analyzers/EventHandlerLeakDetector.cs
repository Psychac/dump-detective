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

        public void Analyze(ClrHeap heap)
        {
            _writer.WriteHeader("EVENT HANDLER LEAK DETECTION:");
            _writer.WriteLine("Identifying event subscriptions that may be causing memory leaks...\n");

            var leaks = DetectEventHandlerLeaks(heap);

            if (leaks.Count == 0)
            {
                _writer.WriteLine("No event handler leaks detected (good!).");
                _writer.WriteLine($"\n{new string('=', 80)}");
                return;
            }

            _writer.WriteLine($"Found {leaks.Count} potential event handler leak(s):\n");

            int leakNum = 1;
            foreach (var leak in leaks.OrderByDescending(l => l.SubscriberCount).Take(20))
            {
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
                    
                    var grouped = leak.SubscriberDetails
                        .GroupBy(s => s.SubscriberType)
                        .OrderByDescending(g => g.Count())
                        .Take(5);

                    foreach (var group in grouped)
                    {
                        var totalSize = group.Sum(s => (long)s.Size);
                        _writer.WriteLine($"    - {group.Key}: {group.Count()} instance(s), {FormatHelper.FormatBytes((ulong)totalSize)}");
                        
                        // Show first instance details
                        var first = group.First();
                        _writer.WriteLine($"      Example: 0x{first.SubscriberAddress:X}");
                        
                        if (first.IsStaticRooted)
                        {
                            _writer.WriteLine($"      ⚠️  Rooted by: {first.RootDescription}");
                        }
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
                _writer.WriteLine($"     }}");
                _writer.WriteLine(string.Empty);
            }

            _writer.WriteLine($"{new string('=', 80)}");
        }

        private List<EventHandlerLeak> DetectEventHandlerLeaks(ClrHeap heap)
        {
            var leaks = new List<EventHandlerLeak>();
            var processedObjects = new HashSet<ulong>();
            var staticRoots = GetStaticRootedAddresses(heap);

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || processedObjects.Contains(obj.Address) || obj.Type == null)
                    continue;

                processedObjects.Add(obj.Address);

                // Skip system types
                if (IsSystemType(obj.Type.Name))
                    continue;

                bool isStaticRooted = staticRoots.Contains(obj.Address);

                foreach (var field in obj.Type.Fields)
                {
                    if (IsEventField(field))
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
                                PublisherType = obj.Type.Name ?? "Unknown",
                                EventName = field.Name ?? "Unknown",
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
                                            bool isStatic = staticRoots.Contains(target.Address);
                                            string rootDesc = isStatic ? FindRootDescription(heap, target.Address) : "";

                                            subscribers.Add(new EventSubscriberDetail
                                            {
                                                SubscriberAddress = target.Address,
                                                SubscriberType = target.Type.Name ?? "Unknown",
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
                    var targetField = eventDelegate.Type?.GetFieldByName("_target");
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
                                SubscriberType = target.Type.Name ?? "Unknown",
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
                    root.RootKind.ToString().Contains("Static", StringComparison.OrdinalIgnoreCase))
                {
                    return root.ToString() ?? "Static Root";
                }
            }
            return "Static Root";
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
