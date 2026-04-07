using Microsoft.Diagnostics.Runtime;

namespace DumpDetective
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: DumpDetective <dump-file-path> [output-file-path]");
                Console.WriteLine("Example: DumpDetective C:\\dumps\\myapp.dmp C:\\reports\\analysis.txt");
                return;
            }

            string dumpPath = args[0];
            //string dumpPath = Path.GetFullPath("D:\\DUmps\\01-02\\Time_03_07_47PM__ProcessList\\w3wp.exe__BALLOADTEST__PID__772__Date__04_01_2026__Time_03_07_47PM__418__Manual Dump.dmp");
            string? outputPath = args.Length > 1 ? args[1] : null;

            if (!File.Exists(dumpPath))
            {
                Console.WriteLine($"Error: Dump file not found at '{dumpPath}'");
                return;
            }

            Console.WriteLine($"Analyzing dump: {dumpPath}");
            if (outputPath != null)
            {
                Console.WriteLine($"Output will be written to: {outputPath}");
            }
            Console.WriteLine();

            try
            {
                AnalyzeDump(dumpPath, outputPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error analyzing dump: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        static void AnalyzeDump(string dumpPath, string? outputPath)
        {
            StreamWriter? fileWriter = null;
            try
            {
                if (outputPath != null)
                {
                    fileWriter = new StreamWriter(outputPath, false);
                }

                using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);

                WriteLine($"Dump file: {dumpPath}", fileWriter);
                WriteLine(string.Empty, fileWriter);

                ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
                ClrHeap heap = runtime.Heap;

                if (!heap.CanWalkHeap)
                {
                    WriteLine("Cannot walk the heap!", fileWriter);
                    return;
                }

                WriteLine("Searching for event leaks...", fileWriter);
                WriteLine(new string('-', 80), fileWriter);

                var eventLeaks = FindEventLeaks(heap, 0);

                if (eventLeaks.Count == 0)
                {
                    WriteLine("No event leaks detected!", fileWriter);
                }
                else
                {
                    var groupedLeaks = eventLeaks
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

                    WriteLine($"\nFound {eventLeaks.Count} event instance(s) across {groupedLeaks.Count} event type(s):\n", fileWriter);
                    WriteLine("SUMMARY BY EVENT TYPE:", fileWriter);
                    WriteLine(new string('=', 80), fileWriter);

                    int groupCount = 1;
                    foreach (var group in groupedLeaks)
                    {
                        WriteLine($"\n[{groupCount++}] {group.PublisherType}.{group.EventFieldName}", fileWriter);
                        WriteLine($"  Instance Count: {group.InstanceCount}", fileWriter);
                        WriteLine($"  Total Subscribers: {group.TotalSubscribers}", fileWriter);
                        WriteLine($"  Average Subscribers: {group.AverageSubscribers:F2}", fileWriter);
                        WriteLine($"  Min/Max Subscribers: {group.MinSubscribers}/{group.MaxSubscribers}", fileWriter);

                        var subscriberTypes = group.Instances
                            .SelectMany(i => i.Subscribers)
                            .GroupBy(s => s.Type)
                            .OrderByDescending(g => g.Count())
                            .Take(5)
                            .ToList();

                        if (subscriberTypes.Any())
                        {
                            WriteLine($"  Top Subscriber Types:", fileWriter);
                            foreach (var subType in subscriberTypes)
                            {
                                WriteLine($"    - {subType.Key} ({subType.Count()} instance(s))", fileWriter);
                            }
                        }
                    }

                    WriteLine($"\n{new string('=', 80)}", fileWriter);
                    WriteLine("\nDETAILED INSTANCES:", fileWriter);
                    WriteLine(new string('=', 80), fileWriter);

                    int detailCount = 1;
                    foreach (var group in groupedLeaks)
                    {
                        WriteLine($"\n[{group.PublisherType}.{group.EventFieldName}] - {group.InstanceCount} instance(s)", fileWriter);
                        WriteLine(new string('-', 80), fileWriter);

                        foreach (var leak in group.Instances.OrderByDescending(l => l.SubscriberCount).Take(5))
                        {
                            WriteLine($"  Instance #{detailCount++}", fileWriter);
                            WriteLine($"    Address: 0x{leak.PublisherAddress:X}", fileWriter);
                            WriteLine($"    Subscribers: {leak.SubscriberCount}", fileWriter);

                            if (leak.Subscribers.Count > 0)
                            {
                                WriteLine($"    Subscriber Types:", fileWriter);
                                foreach (var subscriber in leak.Subscribers.Take(5))
                                {
                                    WriteLine($"      - {subscriber.Type} (0x{subscriber.Address:X})", fileWriter);
                                }
                                if (leak.Subscribers.Count > 5)
                                {
                                    WriteLine($"      ... and {leak.Subscribers.Count - 5} more", fileWriter);
                                }
                            }
                            WriteLine(string.Empty, fileWriter);
                        }

                        if (group.InstanceCount > 5)
                        {
                            WriteLine($"  ... and {group.InstanceCount - 5} more instance(s)", fileWriter);
                            WriteLine(string.Empty, fileWriter);
                        }
                    }
                }

                WriteLine(new string('-', 80), fileWriter);
                WriteLine($"Analysis complete. Total objects analyzed", fileWriter);

                if (outputPath != null)
                {
                    Console.WriteLine($"\nReport written to: {outputPath}");
                }
            }
            finally
            {
                fileWriter?.Dispose();
            }
        }

        static void WriteLine(string message, StreamWriter? fileWriter)
        {
            Console.WriteLine(message);
            fileWriter?.WriteLine(message);
        }

        static List<EventLeakInfo> FindEventLeaks(ClrHeap heap, int minSubscribers = 5)
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

                if (IsSystemType(obj.Type.Name))
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

        static bool IsSystemType(string? typeName)
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
