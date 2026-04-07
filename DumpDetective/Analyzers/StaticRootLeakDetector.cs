using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class StaticRootLeakDetector
    {
        private readonly OutputWriter _writer;

        public StaticRootLeakDetector(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            _writer.WriteHeader("STATIC ROOT LEAK DETECTION:");
            _writer.WriteLine("Identifying static fields that may be causing memory leaks...\n");

            var staticRootAnalysis = AnalyzeStaticRoots(heap, cache);

            if (staticRootAnalysis.Count == 0)
            {
                _writer.WriteLine("No concerning static roots found.");
                _writer.WriteLine($"\n{StringConstants.Equals80}");
                return;
            }

            _writer.WriteLine($"Found {staticRootAnalysis.Count} static root(s) with significant memory impact:\n");

            int rootNum = 1;
            foreach (var analysis in staticRootAnalysis.OrderByDescending(a => a.TotalMemoryImpact).Take(15))
            {
                _writer.WriteLine($"[{rootNum++}] STATIC ROOT LEAK");
                _writer.WriteSeparator();
                _writer.WriteLine($"  Root: {analysis.RootDescription}");
                _writer.WriteLine($"  Direct Object: {analysis.DirectObjectType} @ 0x{analysis.DirectObjectAddress:X}");
                _writer.WriteLine($"  Direct Size: {FormatHelper.FormatBytes(analysis.DirectObjectSize)}");
                _writer.WriteLine($"  Total Retained Memory: {FormatHelper.FormatBytes(analysis.TotalMemoryImpact)}");
                _writer.WriteLine($"  Objects Kept Alive: {analysis.ObjectsKeptAlive:N0}");

                if (analysis.TopRetainedTypes.Any())
                {
                    _writer.WriteLine($"\n  Top Types Kept Alive:");
                    foreach (var typeInfo in analysis.TopRetainedTypes.Take(5))
                    {
                        _writer.WriteLine($"    - {typeInfo.TypeName}: {typeInfo.Count:N0} instance(s), {FormatHelper.FormatBytes(typeInfo.TotalSize)}");
                    }
                }

                _writer.WriteLine($"\n  💡 RECOMMENDATION:");
                _writer.WriteLine($"     Consider if this static field needs to hold references indefinitely.");
                _writer.WriteLine($"     Options: Use WeakReference, implement IDisposable pattern, or clear collections.");

                if (analysis.ContainsCollections)
                {
                    _writer.WriteLine($"     ⚠️  Contains collections - ensure they're being cleared when done.");
                }

                if (analysis.ContainsEventHandlers)
                {
                    _writer.WriteLine($"     ⚠️  Contains event handlers - ensure proper unsubscription.");
                }

                _writer.WriteLine(string.Empty);
            }

            _writer.WriteLine(StringConstants.Equals80);
        }

        private List<StaticRootAnalysis> AnalyzeStaticRoots(ClrHeap heap, HeapAnalysisCache cache)
        {
            var results = new List<StaticRootAnalysis>();
            var processedRoots = new HashSet<ulong>();

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                if (!root.RootKind.ToString().Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                ClrObject obj = root.Object;
                if (!obj.IsValid || !processedRoots.Add(obj.Address))
                    continue;

                // Calculate retained memory using cache
                var retainedObjects = cache.GetRetainedObjects(heap, obj.Address);

                // Only report if significant memory impact (> 1MB or > 100 objects)
                ulong totalSize = (ulong)retainedObjects.Sum(addr => (long)heap.GetObject(addr).Size);

                if (totalSize > 1024 * 1024 || retainedObjects.Count > 100)
                {
                    var analysis = new StaticRootAnalysis
                    {
                        RootDescription = root.ToString() ?? "Unknown Static Root",
                        DirectObjectAddress = obj.Address,
                        DirectObjectType = obj.Type?.Name ?? StringConstants.UnknownType,
                        DirectObjectSize = obj.Size,
                        TotalMemoryImpact = totalSize,
                        ObjectsKeptAlive = retainedObjects.Count,
                        TopRetainedTypes = GetTopRetainedTypes(heap, retainedObjects),
                        ContainsCollections = ContainsCollectionTypes(heap, retainedObjects),
                        ContainsEventHandlers = ContainsEventHandlers(heap, retainedObjects)
                    };

                    results.Add(analysis);
                }
            }

            return results;
        }

        private List<RetainedTypeInfo> GetTopRetainedTypes(ClrHeap heap, HashSet<ulong> addresses)
        {
            var typeStats = new Dictionary<string, RetainedTypeInfo>();

            foreach (var address in addresses)
            {
                var obj = heap.GetObject(address);
                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? StringConstants.UnknownType;

                if (!typeStats.TryGetValue(typeName, out var info))
                {
                    info = new RetainedTypeInfo { TypeName = typeName };
                    typeStats[typeName] = info;
                }

                info.Count++;
                info.TotalSize += obj.Size;
            }

            return typeStats.Values.OrderByDescending(t => t.TotalSize).ToList();
        }

        private bool ContainsCollectionTypes(ClrHeap heap, HashSet<ulong> addresses)
        {
            foreach (var address in addresses.Take(100))
            {
                var obj = heap.GetObject(address);
                if (!obj.IsValid || obj.Type == null)
                    continue;

                if (TypeFilterHelper.IsCollectionType(obj.Type.Name))
                    return true;
            }
            return false;
        }

        private bool ContainsEventHandlers(ClrHeap heap, HashSet<ulong> addresses)
        {
            foreach (var address in addresses.Take(100))
            {
                var obj = heap.GetObject(address);
                if (!obj.IsValid || obj.Type == null)
                    continue;

                foreach (var field in obj.Type.Fields)
                {
                    if (field.Type?.Name != null && TypeFilterHelper.IsEventField(field.Type.Name))
                        return true;
                }
            }
            return false;
        }
    }

    internal class StaticRootAnalysis
    {
        public string RootDescription { get; set; } = string.Empty;
        public ulong DirectObjectAddress { get; set; }
        public string DirectObjectType { get; set; } = string.Empty;
        public ulong DirectObjectSize { get; set; }
        public ulong TotalMemoryImpact { get; set; }
        public int ObjectsKeptAlive { get; set; }
        public List<RetainedTypeInfo> TopRetainedTypes { get; set; } = new();
        public bool ContainsCollections { get; set; }
        public bool ContainsEventHandlers { get; set; }
    }

    internal class RetainedTypeInfo
    {
        public string TypeName { get; set; } = string.Empty;
        public int Count { get; set; }
        public ulong TotalSize { get; set; }
    }
}
