using Microsoft.Diagnostics.Runtime;
using DumpDetective.Configuration;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class MemoryLeakAnalyzer
    {
        private readonly OutputWriter _writer;
        private readonly int _highReferenceThreshold;
        private readonly int _maxStringLength;
        private readonly int _minDuplicateCount;
        private readonly int _maxReferenceAddressesToTrack;

        public MemoryLeakAnalyzer(OutputWriter writer, AnalysisConfiguration config)
        {
            _writer = writer;
            _highReferenceThreshold = config.HighReferenceThreshold;
            _maxStringLength = config.MaxDuplicateStringLength;
            _minDuplicateCount = config.MinDuplicateStringCount;
            _maxReferenceAddressesToTrack = config.MaxReferenceAddressesToTrack;
        }

        public void Analyze(ClrHeap heap, ClrRuntime runtime)
        {
            _writer.WriteHeader("MEMORY LEAK ANALYSIS:");

            AnalyzeFinalizerQueue(heap);
            AnalyzeRootsPass(heap);     // static refs + rooted objects in one pass
            AnalyzeObjectsPass(heap);   // string dups + reference counts in one pass

            _writer.WriteLine(StringConstants.Equals80);
        }

        private void AnalyzeFinalizerQueue(ClrHeap heap)
        {
            // Single pass — no intermediate list allocation
            var finalizerTypes = new Dictionary<string, int>();
            int finalizerCount = 0;

            foreach (var obj in heap.EnumerateFinalizableObjects())
            {
                finalizerCount++;
                string typeName = obj.Type?.Name ?? StringConstants.UnknownType;
                finalizerTypes.TryGetValue(typeName, out int typeCount);
                finalizerTypes[typeName] = typeCount + 1;
            }

            if (finalizerCount > 0)
            {
                _writer.WriteLine("\nFINALIZER QUEUE:");
                _writer.WriteSeparator();
                _writer.WriteLine($"Objects waiting for finalization: {finalizerCount:N0}");

                _writer.WriteLine("\nTop types in finalizer queue:");

                // Manual sorting - no LINQ allocations
                var sortedTypes = new List<KeyValuePair<string, int>>(finalizerTypes);
                sortedTypes.Sort((a, b) => b.Value.CompareTo(a.Value));

                int count = 0;
                foreach (var kvp in sortedTypes)
                {
                    if (count >= 10) break;
                    _writer.WriteLine($"  {kvp.Key}: {kvp.Value:N0} object(s)");
                    count++;
                }
            }
            else
            {
                _writer.WriteLine("\nFINALIZER QUEUE: Empty (good!)");
            }
        }

        private void AnalyzeRootsPass(ClrHeap heap)
        {
            // Single pass over roots — collects data for both static references and rooted objects
            var staticRoots = new Dictionary<string, StaticRootTypeInfo>();
            var rootedObjectsByType = new Dictionary<string, RootedTypeInfo>(capacity: 512);

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                ClrObject obj = root.Object;
                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? StringConstants.UnknownType;
                string rootKind = root.RootKind.ToString(); // computed once, used for both checks below

                if (rootKind.Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase))
                {
                    string rootName = root.ToString() ?? "Unknown Root";

                    if (!staticRoots.TryGetValue(typeName, out var staticRootInfo))
                    {
                        staticRootInfo = new StaticRootTypeInfo();
                        staticRoots[typeName] = staticRootInfo;
                    }

                    staticRootInfo.Count++;
                    staticRootInfo.TotalSize += obj.Size;
                    if (staticRootInfo.SampleRootNames.Count < 2)
                    {
                        staticRootInfo.SampleRootNames.Add(rootName);
                    }
                }

                if (!rootedObjectsByType.TryGetValue(typeName, out var typeInfo))
                {
                    typeInfo = new RootedTypeInfo { TypeName = typeName };
                    rootedObjectsByType[typeName] = typeInfo;
                }

                typeInfo.Count++;
                typeInfo.TotalSize += obj.Size;
                typeInfo.RootKinds.TryGetValue(rootKind, out int kindCount);
                typeInfo.RootKinds[rootKind] = kindCount + 1;
            }

            PrintStaticReferences(staticRoots);
            PrintRootedObjects(rootedObjectsByType);
        }

        private void PrintStaticReferences(Dictionary<string, StaticRootTypeInfo> staticRoots)
        {
            _writer.WriteLine("\n\nSTATIC FIELD REFERENCES:");
            _writer.WriteSeparator();

            if (staticRoots.Count > 0)
            {
                _writer.WriteLine("Objects held by static fields (potential leak sources):");
                _writer.WriteLine($"Total static-rooted object types: {staticRoots.Count:N0}");
                _writer.WriteLine("\nTop types by count:");

                // Manual sorting - no LINQ allocations
                var sortedRoots = new List<KeyValuePair<string, StaticRootTypeInfo>>(staticRoots);
                sortedRoots.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));

                int count = 0;
                foreach (var kvp in sortedRoots)
                {
                    if (count >= 15) break;
                    _writer.WriteLine($"  {FormatHelper.TruncateString(kvp.Key, 50),-50} {kvp.Value.Count,8:N0} instances  {FormatHelper.FormatBytes(kvp.Value.TotalSize),12}");

                    int displayCount = kvp.Value.SampleRootNames.Count;
                    for (int i = 0; i < displayCount; i++)
                    {
                        _writer.WriteLine($"    └─ {FormatHelper.TruncateString(kvp.Value.SampleRootNames[i], 70)}");
                    }
                    if (kvp.Value.Count > displayCount)
                    {
                        _writer.WriteLine($"    └─ ... and {kvp.Value.Count - displayCount} more");
                    }
                    count++;
                }
            }
            else
            {
                _writer.WriteLine("No static field references found (or unable to enumerate)");
            }
        }

        private void AnalyzeObjectsPass(ClrHeap heap)
        {
            // Single pass over heap objects — collects data for both string analysis and reference counting
            var stringStats = new Dictionary<StringFingerprint, StringLeakInfo>(capacity: 1024);
            int totalStrings = 0;
            ulong totalStringMemory = 0;
            var referenceCount = new Dictionary<ulong, int>(capacity: 4096);
            long skippedReferenceAddresses = 0;

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid) continue;

                if (obj.Type?.Name == "System.String")
                {
                    totalStrings++;
                    totalStringMemory += obj.Size;

                    string? value = obj.AsString();
                    if (value != null && value.Length > 0 && value.Length < _maxStringLength)
                    {
                        var fingerprint = CreateStringFingerprint(value);

                        if (!stringStats.TryGetValue(fingerprint, out var info))
                        {
                            info = new StringLeakInfo { Preview = CreateStringPreview(value) };
                            stringStats[fingerprint] = info;
                        }
                        info.Count++;
                        info.TotalSize += obj.Size;
                    }
                }

                foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
                {
                    if (reference.IsValid)
                    {
                        if (referenceCount.TryGetValue(reference.Address, out int count))
                        {
                            referenceCount[reference.Address] = count + 1;
                        }
                        else if (referenceCount.Count < _maxReferenceAddressesToTrack)
                        {
                            referenceCount[reference.Address] = 1;
                        }
                        else
                        {
                            skippedReferenceAddresses++;
                        }
                    }
                }
            }

            PrintDuplicateStrings(stringStats, totalStrings, totalStringMemory);
            PrintHighlyReferencedObjects(heap, referenceCount, skippedReferenceAddresses);
        }

        private void PrintDuplicateStrings(Dictionary<StringFingerprint, StringLeakInfo> stringStats, int totalStrings, ulong totalStringMemory)
        {
            _writer.WriteLine("\n\nDUPLICATE STRING ANALYSIS:");
            _writer.WriteSeparator();

            _writer.WriteLine($"Total strings: {totalStrings:N0}");
            _writer.WriteLine($"Total string memory: {FormatHelper.FormatBytes(totalStringMemory)}");
            _writer.WriteLine($"Unique strings: {stringStats.Count:N0}");

            var duplicates = stringStats.Values
                .Where(s => s.Count > _minDuplicateCount)
                .OrderByDescending(s => s.TotalSize)
                .Take(20)
                .ToList();

            if (duplicates.Count > 0)
            {
                _writer.WriteLine("\nMost duplicated strings (potential string pooling opportunities):");
                _writer.WriteLine($"{"String Preview",-50} {"Count",12} {"Wasted Memory",15}");
                _writer.WriteSeparator();

                foreach (var dup in duplicates)
                {
                    ulong wastedMemory = dup.TotalSize - (dup.TotalSize / (ulong)dup.Count);
                    _writer.WriteLine($"{dup.Preview,-50} {dup.Count,12:N0} {FormatHelper.FormatBytes(wastedMemory),15}");
                }
            }
        }

        private void PrintHighlyReferencedObjects(ClrHeap heap, Dictionary<ulong, int> referenceCount, long skippedReferenceAddresses)
        {
            _writer.WriteLine("\n\nHIGHLY REFERENCED OBJECTS:");
            _writer.WriteSeparator();
            _writer.WriteLine("Objects with many incoming references (may indicate leaks):\n");

            if (skippedReferenceAddresses > 0)
            {
                _writer.WriteLine($"⚠️  Reference tracking capped at {_maxReferenceAddressesToTrack:N0} unique addresses.");
                _writer.WriteLine($"    Skipped {skippedReferenceAddresses:N0} additional references to new addresses. Results may be incomplete.\n");
            }

            var highlyReferenced = referenceCount
                .Where(kvp => kvp.Value > _highReferenceThreshold)
                .OrderByDescending(kvp => kvp.Value)
                .Take(15);

            bool foundHighlyReferenced = false;
            foreach (var kvp in highlyReferenced)
            {
                ClrObject obj = heap.GetObject(kvp.Key);
                if (obj.IsValid && obj.Type != null)
                {
                    foundHighlyReferenced = true;
                    _writer.WriteLine($"  {obj.Type.Name ?? StringConstants.UnknownType}");
                    _writer.WriteLine($"    Address: 0x{obj.Address:X}");
                    _writer.WriteLine($"    Size: {FormatHelper.FormatBytes(obj.Size)}");
                    _writer.WriteLine($"    Incoming references: {kvp.Value:N0}");
                    _writer.WriteLine(string.Empty);
                }
            }

            if (!foundHighlyReferenced)
            {
                _writer.WriteLine($"  ✅ No objects with more than {_highReferenceThreshold} incoming references found.");
            }
        }

        private void PrintRootedObjects(Dictionary<string, RootedTypeInfo> rootedObjectsByType)
        {
            _writer.WriteLine("\n\nROOTED OBJECTS ANALYSIS:");
            _writer.WriteSeparator();
            _writer.WriteLine("Objects kept alive by GC roots:\n");
            _writer.WriteLine($"Total rooted object types: {rootedObjectsByType.Count:N0}");
            _writer.WriteLine("\nTop rooted types by count (these won't be garbage collected):");
            _writer.WriteLine($"{"Type",-50} {"Count",10} {"Size",12} {"Primary Root Kind",-20}");
            _writer.WriteSeparator();

            // Manual sorting - no LINQ allocations
            var sortedRooted = new List<RootedTypeInfo>(rootedObjectsByType.Values);
            sortedRooted.Sort((a, b) => b.Count.CompareTo(a.Count));

            int count = 0;
            foreach (var typeInfo in sortedRooted)
            {
                if (count >= 20) break;

                // Manual max-find — no LINQ allocations
                string primaryRootKind = "";
                int primaryRootKindCount = 0;
                foreach (var rk in typeInfo.RootKinds)
                {
                    if (rk.Value > primaryRootKindCount)
                    {
                        primaryRootKindCount = rk.Value;
                        primaryRootKind = rk.Key;
                    }
                }

                _writer.WriteLine($"{FormatHelper.TruncateString(typeInfo.TypeName, 50),-50} {typeInfo.Count,10:N0} {FormatHelper.FormatBytes(typeInfo.TotalSize),12} {primaryRootKind,-20}");
                count++;
            }

            _writer.WriteLine($"\n{StringConstants.Equals80}");
        }

        private static StringFingerprint CreateStringFingerprint(string value)
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;

            ulong hash = fnvOffset;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= fnvPrime;
            }

            return new StringFingerprint(hash, value.Length, value[0], value[^1]);
        }

        private static string CreateStringPreview(string value)
        {
            string preview = value.Length > 47 ? value.Substring(0, 47) + "..." : value;
            return preview.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private readonly record struct StringFingerprint(ulong Hash, int Length, char FirstChar, char LastChar);

        private sealed class StaticRootTypeInfo
        {
            public int Count { get; set; }
            public ulong TotalSize { get; set; }
            public List<string> SampleRootNames { get; } = new(capacity: 2);
        }
    }
}
