using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class MemoryLeakAnalyzer
    {
        private readonly OutputWriter _writer;

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

            _writer.WriteLine($"{new string('=', 80)}");
            _writer.WriteHeader("EVENT LEAK DETECTION:");
            _writer.WriteLine($"{new string('=', 80)}");
        }

        private void AnalyzeFinalizerQueue(ClrHeap heap)
        {
            var finalizerQueue = heap.EnumerateFinalizableObjects().ToList();
            if (finalizerQueue.Any())
            {
                _writer.WriteLine("\nFINALIZER QUEUE:");
                _writer.WriteSeparator();
                _writer.WriteLine($"Objects waiting for finalization: {finalizerQueue.Count:N0}");

                var finalizerTypes = finalizerQueue
                    .Select(obj => obj.Type?.Name ?? "Unknown")
                    .GroupBy(name => name)
                    .Select(g => new { Type = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(10);

                _writer.WriteLine("\nTop types in finalizer queue:");
                foreach (var type in finalizerTypes)
                {
                    _writer.WriteLine($"  {type.Type}: {type.Count:N0} object(s)");
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
                bool isStaticRoot = root.RootKind.ToString().Contains("Static", StringComparison.OrdinalIgnoreCase);

                if (isStaticRoot)
                {
                    ClrObject obj = root.Object;
                    if (obj.IsValid && obj.Type != null)
                    {
                        string typeName = obj.Type.Name ?? "Unknown";
                        string rootName = root.ToString() ?? "Unknown Root";

                        if (!staticRoots.ContainsKey(typeName))
                        {
                            staticRoots[typeName] = new List<RootInfo>();
                        }

                        staticRoots[typeName].Add(new RootInfo
                        {
                            Address = obj.Address,
                            Size = obj.Size,
                            RootName = rootName
                        });
                    }
                }
            }

            if (staticRoots.Any())
            {
                _writer.WriteLine("Objects held by static fields (potential leak sources):");
                _writer.WriteLine($"Total static-rooted object types: {staticRoots.Count:N0}");
                _writer.WriteLine("\nTop types by count:");

                foreach (var kvp in staticRoots.OrderByDescending(x => x.Value.Count).Take(15))
                {
                    ulong totalSize = (ulong)kvp.Value.Sum(r => (long)r.Size);
                    _writer.WriteLine($"  {FormatHelper.TruncateString(kvp.Key, 50),-50} {kvp.Value.Count,8:N0} instances  {FormatHelper.FormatBytes(totalSize),12}");

                    foreach (var root in kvp.Value.Take(2))
                    {
                        _writer.WriteLine($"    └─ {FormatHelper.TruncateString(root.RootName, 70)}");
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

            var stringStats = new Dictionary<string, StringLeakInfo>();
            int totalStrings = 0;
            ulong totalStringMemory = 0;

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (obj.IsValid && obj.Type?.Name == "System.String")
                {
                    totalStrings++;
                    totalStringMemory += obj.Size;

                    string? value = obj.AsString();
                    if (value != null && value.Length > 0 && value.Length < 500)
                    {
                        if (!stringStats.ContainsKey(value))
                        {
                            stringStats[value] = new StringLeakInfo { Value = value };
                        }
                        stringStats[value].Count++;
                        stringStats[value].TotalSize += obj.Size;
                    }
                }
            }

            _writer.WriteLine($"Total strings: {totalStrings:N0}");
            _writer.WriteLine($"Total string memory: {FormatHelper.FormatBytes(totalStringMemory)}");
            _writer.WriteLine($"Unique strings: {stringStats.Count:N0}");

            var duplicates = stringStats.Values
                .Where(s => s.Count > 10)
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

            var referenceCount = new Dictionary<ulong, int>();

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid) continue;

                foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
                {
                    if (reference.IsValid)
                    {
                        if (!referenceCount.ContainsKey(reference.Address))
                        {
                            referenceCount[reference.Address] = 0;
                        }
                        referenceCount[reference.Address]++;
                    }
                }
            }

            var highlyReferenced = referenceCount
                .Where(kvp => kvp.Value > 50)
                .OrderByDescending(kvp => kvp.Value)
                .Take(15);

            foreach (var kvp in highlyReferenced)
            {
                ClrObject obj = heap.GetObject(kvp.Key);
                if (obj.IsValid && obj.Type != null)
                {
                    _writer.WriteLine($"  {obj.Type.Name ?? "Unknown"}");
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

            var rootedObjectsByType = new Dictionary<string, RootedTypeInfo>();

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                ClrObject obj = root.Object;
                if (obj.IsValid && obj.Type != null)
                {
                    string typeName = obj.Type.Name ?? "Unknown";

                    if (!rootedObjectsByType.ContainsKey(typeName))
                    {
                        rootedObjectsByType[typeName] = new RootedTypeInfo { TypeName = typeName };
                    }

                    rootedObjectsByType[typeName].Count++;
                    rootedObjectsByType[typeName].TotalSize += obj.Size;

                    string rootKind = root.RootKind.ToString();
                    if (!rootedObjectsByType[typeName].RootKinds.ContainsKey(rootKind))
                    {
                        rootedObjectsByType[typeName].RootKinds[rootKind] = 0;
                    }
                    rootedObjectsByType[typeName].RootKinds[rootKind]++;
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

            _writer.WriteLine($"\n{new string('=', 80)}");
        }
    }
}
