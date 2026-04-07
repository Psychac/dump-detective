using Microsoft.Diagnostics.Runtime;

namespace DumpDetective
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: DumpDetective <dump-file-path>");
                Console.WriteLine("Example: DumpDetective C:\\dumps\\myapp.dmp");
                return;
            }

            string dumpPath = args[0];

            if (!File.Exists(dumpPath))
            {
                Console.WriteLine($"Error: Dump file not found at '{dumpPath}'");
                return;
            }

            Console.WriteLine($"Analyzing dump: {dumpPath}");
            Console.WriteLine();

            try
            {
                AnalyzeDump(dumpPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error analyzing dump: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        static void AnalyzeDump(string dumpPath)
        {
            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);

            Console.WriteLine($"Dump file: {dumpPath}");
            Console.WriteLine();

            ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            ClrHeap heap = runtime.Heap;

            if (!heap.CanWalkHeap)
            {
                Console.WriteLine("Cannot walk the heap!");
                return;
            }

            Console.WriteLine("Searching for event leaks...");
            Console.WriteLine(new string('-', 80));

            var eventLeaks = FindEventLeaks(heap);

            if (eventLeaks.Count == 0)
            {
                Console.WriteLine("No event leaks detected!");
            }
            else
            {
                Console.WriteLine($"\nFound {eventLeaks.Count} potential event leak(s):\n");

                int count = 1;
                foreach (var leak in eventLeaks.OrderByDescending(l => l.SubscriberCount))
                {
                    Console.WriteLine($"#{count++}");
                    Console.WriteLine($"  Publisher Type: {leak.PublisherType}");
                    Console.WriteLine($"  Event Field: {leak.EventFieldName}");
                    Console.WriteLine($"  Subscriber Count: {leak.SubscriberCount}");
                    Console.WriteLine($"  Publisher Address: 0x{leak.PublisherAddress:X}");

                    if (leak.Subscribers.Count > 0)
                    {
                        Console.WriteLine("  Subscribers:");
                        foreach (var subscriber in leak.Subscribers.Take(10))
                        {
                            Console.WriteLine($"    - {subscriber.Type} (0x{subscriber.Address:X})");
                        }
                        if (leak.Subscribers.Count > 10)
                        {
                            Console.WriteLine($"    ... and {leak.Subscribers.Count - 10} more");
                        }
                    }
                    Console.WriteLine();
                }
            }

            Console.WriteLine(new string('-', 80));
            Console.WriteLine($"Analysis complete. Total objects analyzed");
        }

        static List<EventLeakInfo> FindEventLeaks(ClrHeap heap)
        {
            var leaks = new List<EventLeakInfo>();
            var processedObjects = new HashSet<ulong>();

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || processedObjects.Contains(obj.Address))
                    continue;

                processedObjects.Add(obj.Address);

                if (obj.Type == null)
                    continue;

                foreach (ClrInstanceField field in obj.Type.Fields)
                {
                    if (IsEventField(field))
                    {
                        var subscribers = GetEventSubscribers(obj, field, heap);

                        if (subscribers.Count > 5)
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

        static bool IsEventField(ClrInstanceField field)
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

        static List<SubscriberInfo> GetEventSubscribers(ClrObject obj, ClrInstanceField eventField, ClrHeap heap)
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

    class EventLeakInfo
    {
        public ulong PublisherAddress { get; set; }
        public string PublisherType { get; set; } = string.Empty;
        public string EventFieldName { get; set; } = string.Empty;
        public int SubscriberCount { get; set; }
        public List<SubscriberInfo> Subscribers { get; set; } = new();
    }

    class SubscriberInfo
    {
        public ulong Address { get; set; }
        public string Type { get; set; } = string.Empty;
    }
}
