using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class MemoryLeakAnalyzer
    {
        private readonly OutputWriter _writer;
        private const int HighReferenceThreshold = 50;
        private const int MaxStringLength = 500;
        private const int MinDuplicateCount = 10;

        public MemoryLeakAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrHeap heap, ClrRuntime runtime)
        {
            _writer.WriteHeader("MEMORY LEAK ANALYSIS:");

            AnalyzeFinalizerQueue(heap);
            AnalyzeStaticReferences(heap);
            AnalyzeDuplicateStrings(heap);
            AnalyzeHighlyReferencedObjects(heap);
            AnalyzeRootedObjects(heap);

            _writer.WriteLine(StringConstants.Equals80);
            _writer.WriteHeader("EVENT LEAK DETECTION:");
            _writer.WriteLine(StringConstants.Equals80);
        }

        private void AnalyzeFinalizerQueue(ClrHeap heap)
        {
            var finalizerQueue = heap.EnumerateFinalizableObjects().ToList();
            if (finalizerQueue.Count > 0)
            {
                _writer.WriteLine("\nFINALIZER QUEUE:");
                _writer.WriteSeparator();
                _writer.WriteLine($"Objects waiting for finalization: {finalizerQueue.Count:N0}");

                // Manual grouping for better performance
                var finalizerTypes = new Dictionary<string, int>();
                foreach (var obj in finalizerQueue)
                {
                    string typeName = obj.Type?.Name ?? StringConstants.UnknownType;
                    finalizerTypes.TryGetValue(typeName, out int count);
                    finalizerTypes[typeName] = count + 1;
                }

                _writer.WriteLine("\nTop types in finalizer queue:");
                foreach (var kvp in finalizerTypes.OrderByDescending(x => x.Value).Take(10))
                {
                    _writer.WriteLine($"  {kvp.Key}: {kvp.Value:N0} object(s)");
                }
            }
            else
            {
                _writer.WriteLine("\nFINALIZER QUEUE: Empty (good!)");
            }
        }

        private void AnalyzeStaticReferences(ClrHeap heap)
        {
            _writer.WriteLine("\n\nSTATIC FIELD REFERENCES:");
            _writer.WriteSeparator();

            var staticRoots = new Dictionary<string, List<RootInfo>>();

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                bool isStaticRoot = root.RootKind.ToString().Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase);

                if (isStaticRoot)
                {
                    ClrObject obj = root.Object;
                    if (obj.IsValid && obj.Type != null)
                    {
                        string typeName = obj.Type.Name ?? StringConstants.UnknownType;
                        string rootName = root.ToString() ?? "Unknown Root";

                        if (!staticRoots.TryGetValue(typeName, out var list))
                        {
                            list = new List<RootInfo>();
                            staticRoots[typeName] = list;
                        }

                        list.Add(new RootInfo
                        {
                            Address = obj.Address,
                            Size = obj.Size,
                            RootName = rootName
                        });
                    }
                }
            }

            if (staticRoots.Count > 0)
            {
                _writer.WriteLine("Objects held by static fields (potential leak sources):");
                _writer.WriteLine($"Total static-rooted object types: {staticRoots.Count:N0}");
                _writer.WriteLine("\nTop types by count:");

                foreach (var kvp in staticRoots.OrderByDescending(x => x.Value.Count).Take(15))
                {
                    ulong totalSize = (ulong)kvp.Value.Sum(r => (long)r.Size);
                    _writer.WriteLine($"  {FormatHelper.TruncateString(kvp.Key, 50),-50} {kvp.Value.Count,8:N0} instances  {FormatHelper.FormatBytes(totalSize),12}");

                    int displayCount = Math.Min(2, kvp.Value.Count);
                    for (int i = 0; i < displayCount; i++)
                    {
                        _writer.WriteLine($"    └─ {FormatHelper.TruncateString(kvp.Value[i].RootName, 70)}");
                    }
                    if (kvp.Value.Count > 2)
                    {
                        _writer.WriteLine($"    └─ ... and {kvp.Value.Count - 2} more");
                    }
                }
            }
            else
            {
                _writer.WriteLine("No static field references found (or unable to enumerate)");
            }
        }

        private void AnalyzeDuplicateStrings(ClrHeap heap)
        {
            _writer.WriteLine("\n\nDUPLICATE STRING ANALYSIS:");
            _writer.WriteSeparator();

            var stringStats = new Dictionary<string, StringLeakInfo>(capacity: 1024);
            int totalStrings = 0;
            ulong totalStringMemory = 0;

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (obj.IsValid && obj.Type?.Name == "System.String")
                {
                    totalStrings++;
                    totalStringMemory += obj.Size;

                    string? value = obj.AsString();
                    if (value != null && value.Length > 0 && value.Length < MaxStringLength)
                    {
                        if (!stringStats.TryGetValue(value, out var info))
                        {
                            info = new StringLeakInfo { Value = value };
                            stringStats[value] = info;
                        }
                        info.Count++;
                        info.TotalSize += obj.Size;
                    }
                }
            }

            _writer.WriteLine($"Total strings: {totalStrings:N0}");
            _writer.WriteLine($"Total string memory: {FormatHelper.FormatBytes(totalStringMemory)}");
            _writer.WriteLine($"Unique strings: {stringStats.Count:N0}");

            var duplicates = stringStats.Values
                .Where(s => s.Count > MinDuplicateCount)
                .OrderByDescending(s => s.TotalSize)
                .Take(20);

            if (duplicates.Any())
            {
                _writer.WriteLine("\nMost duplicated strings (potential string pooling opportunities):");
                _writer.WriteLine($"{"String Preview",-50} {"Count",12} {"Wasted Memory",15}");
                _writer.WriteSeparator();

                foreach (var dup in duplicates)
                {
                    string preview = dup.Value.Length > 47 ? dup.Value.Substring(0, 47) + "..." : dup.Value;
                    preview = preview.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                    ulong wastedMemory = dup.TotalSize - (dup.TotalSize / (ulong)dup.Count);
                    _writer.WriteLine($"{preview,-50} {dup.Count,12:N0} {FormatHelper.FormatBytes(wastedMemory),15}");
                }
            }
        }

        private void AnalyzeHighlyReferencedObjects(ClrHeap heap)
        {
            _writer.WriteLine("\n\nHIGHLY REFERENCED OBJECTS:");
            _writer.WriteSeparator();
            _writer.WriteLine("Objects with many incoming references (may indicate leaks):\n");

            var referenceCount = new Dictionary<ulong, int>(capacity: 1024);

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid) continue;

                foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
                {
                    if (reference.IsValid)
                    {
                        referenceCount.TryGetValue(reference.Address, out int count);
                        referenceCount[reference.Address] = count + 1;
                    }
                }
            }

            var highlyReferenced = referenceCount
                .Where(kvp => kvp.Value > HighReferenceThreshold)
                .OrderByDescending(kvp => kvp.Value)
                .Take(15);

            foreach (var kvp in highlyReferenced)
            {
                ClrObject obj = heap.GetObject(kvp.Key);
                if (obj.IsValid && obj.Type != null)
                {
                    _writer.WriteLine($"  {obj.Type.Name ?? StringConstants.UnknownType}");
                    _writer.WriteLine($"    Address: 0x{obj.Address:X}");
                    _writer.WriteLine($"    Size: {FormatHelper.FormatBytes(obj.Size)}");
                    _writer.WriteLine($"    Incoming references: {kvp.Value:N0}");
                    _writer.WriteLine(string.Empty);
                }
            }
        }

        private void AnalyzeRootedObjects(ClrHeap heap)
        {
            _writer.WriteLine("\n\nROOTED OBJECTS ANALYSIS:");
            _writer.WriteSeparator();
            _writer.WriteLine("Objects kept alive by GC roots:\n");

            var rootedObjectsByType = new Dictionary<string, RootedTypeInfo>(capacity: 512);

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                ClrObject obj = root.Object;
                if (obj.IsValid && obj.Type != null)
                {
                    string typeName = obj.Type.Name ?? StringConstants.UnknownType;

                    if (!rootedObjectsByType.TryGetValue(typeName, out var info))
                    {
                        info = new RootedTypeInfo { TypeName = typeName };
                        rootedObjectsByType[typeName] = info;
                    }

                    info.Count++;
                    info.TotalSize += obj.Size;

                    string rootKind = root.RootKind.ToString();
                    info.RootKinds.TryGetValue(rootKind, out int kindCount);
                    info.RootKinds[rootKind] = kindCount + 1;
                }
            }

            _writer.WriteLine($"Total rooted object types: {rootedObjectsByType.Count:N0}");
            _writer.WriteLine("\nTop rooted types by count (these won't be garbage collected):");
            _writer.WriteLine($"{"Type",-50} {"Count",10} {"Size",12} {"Primary Root Kind",-20}");
            _writer.WriteSeparator();

            foreach (var kvp in rootedObjectsByType.Values.OrderByDescending(r => r.Count).Take(20))
            {
                var primaryRootKind = kvp.RootKinds.OrderByDescending(rk => rk.Value).First();
                _writer.WriteLine($"{FormatHelper.TruncateString(kvp.TypeName, 50),-50} {kvp.Count,10:N0} {FormatHelper.FormatBytes(kvp.TotalSize),12} {primaryRootKind.Key,-20}");
            }

            _writer.WriteLine($"\n{StringConstants.Equals80}");
        }
    }
}
