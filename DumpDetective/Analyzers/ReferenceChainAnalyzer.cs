using Microsoft.Diagnostics.Runtime;
using DumpDetective.Configuration;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class ReferenceChainAnalyzer
    {
        private readonly OutputWriter _writer;
        private readonly AnalysisConfiguration _config;
        private const int MaxPathsToShow = 5;
        private const int DefaultTopTypeCount = 10;
        private const int MaxDepth = 50;

        public ReferenceChainAnalyzer(OutputWriter writer, AnalysisConfiguration config)
        {
            _writer = writer;
            _config = config;
        }

        public AnalyzerOutput AnalyzeTopTypes(ClrHeap heap, HeapAnalysisCache cache)
        {
            _writer.WriteHeader("REFERENCE CHAIN ANALYSIS:");

            int topCount = _config.ReferenceChainTopCount > 0 ? _config.ReferenceChainTopCount : DefaultTopTypeCount;

            // Use cached type statistics instead of re-enumerating
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            var topTypes = typeStats
                .OrderByDescending(kvp => kvp.Value.TotalSize)
                .Take(topCount)
                .ToList();

            int retainedSamples = 0;
            int analyzedSamples = 0;

            int typeNum = 1;
            foreach (var typeKvp in topTypes)
            {
                string typeName = typeKvp.Key;
                var stats = typeKvp.Value;

                // Skip system types using shared utility
                if (TypeFilterHelper.IsSystemType(typeName))
                    continue;

                _writer.WriteLine($"\n[{typeNum++}] Type: {typeName}");
                _writer.WriteLine($"    Count: {stats.Count:N0}");
                _writer.WriteLine($"    Total Size: {FormatHelper.FormatBytes(stats.TotalSize)}");
                _writer.WriteSeparator();

                // Use cached sample instance instead of full heap walk
                ulong? sampleAddress = cache.GetSampleInstanceAddress(typeName);

                if (sampleAddress.HasValue)
                {
                    analyzedSamples++;
                    if (AnalyzeObject(heap, sampleAddress.Value))
                        retainedSamples++;
                }
                else
                {
                    _writer.WriteLine("    No valid sample instance found.");
                }
            }

            double retainedPct = analyzedSamples == 0 ? 0 : retainedSamples * 100.0 / analyzedSamples;
            _writer.WriteLine($"\n{StringConstants.Equals80}");
            return new AnalyzerOutput(
                [CreateFinding(analyzedSamples, retainedSamples)],
                new ReferenceChainDomainResult(analyzedSamples, retainedSamples, retainedPct));
        }

        public bool AnalyzeObject(ClrHeap heap, ulong objectAddress)
        {
            ClrObject obj = heap.GetObject(objectAddress);

            if (!obj.IsValid)
            {
                _writer.WriteLine($"    Object at 0x{objectAddress:X} is not valid.");
                return false;
            }

            _writer.WriteLine($"    Sample Instance: 0x{objectAddress:X}");
            _writer.WriteLine($"    Type: {obj.Type?.Name ?? StringConstants.UnknownType}");
            _writer.WriteLine($"    Size: {FormatHelper.FormatBytes(obj.Size)}");

            var paths = FindPathsToRoot(heap, objectAddress);

            if (paths.Count == 0)
            {
                _writer.WriteLine("    Status: No GC root found (may be eligible for collection)");
                return false;
            }
            else
            {
                _writer.WriteLine($"    Status: Kept alive by {paths.Count} root path(s)");
                _writer.WriteLine($"\n    Reference Chains (showing up to {MaxPathsToShow}):");

                int pathNum = 1;
                foreach (var path in paths.Take(MaxPathsToShow))
                {
                    _writer.WriteLine($"\n    Path #{pathNum++}:");
                    PrintPath(path);
                }

                if (paths.Count > MaxPathsToShow)
                {
                    _writer.WriteLine($"\n    ... and {paths.Count - MaxPathsToShow} more path(s)");
                }

                return true;
            }
        }

        private static InsightFinding CreateFinding(int analyzedSamples, int retainedSamples)
        {
            if (analyzedSamples == 0)
            {
                return new InsightFinding(
                    Analyzer: nameof(ReferenceChainAnalyzer),
                    Category: "Retention",
                    Severity: FindingSeverity.Info,
                    Title: "No sample instances available for reference-chain tracing",
                    Evidence: "Reference-chain analyzer could not obtain valid sample objects for configured top types.",
                    Recommendation: "Review type statistics and dump integrity; re-run with broader type coverage if needed.",
                    Tags: ["reference-chain", "roots", "retention"],
                    MetricValue: 0,
                    MetricUnit: "% retained-samples");
            }

            double retainedPct = retainedSamples * 100.0 / analyzedSamples;
            FindingSeverity severity = retainedPct >= 70 ? FindingSeverity.Warning : FindingSeverity.Info;
            return new InsightFinding(
                Analyzer: nameof(ReferenceChainAnalyzer),
                Category: "Retention",
                Severity: severity,
                Title: "Reference-chain retention coverage",
                Evidence: $"{retainedSamples:N0}/{analyzedSamples:N0} sampled top types had at least one GC-root path ({retainedPct:F1}%).",
                Recommendation: "Focus on root paths for retained top types to identify ownership leaks.",
                Tags: ["reference-chain", "gc-roots", "retention"],
                MetricValue: retainedPct,
                MetricUnit: "% retained-samples");
        }

        private List<List<ReferenceNode>> FindPathsToRoot(ClrHeap heap, ulong targetAddress)
        {
            var paths = new List<List<ReferenceNode>>();
            var visited = new HashSet<ulong>();
            var currentPath = new List<ReferenceNode>();
            var scanCounter = new ObjectScanCounter("Reference chain root scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(2));

            // Build reverse reference map for faster lookup
            var referrersMap = BuildReferrersMap(heap, targetAddress);

            // Find all roots that can reach this object
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                scanCounter.Tick();

                if (CanReachTarget(heap, root.Object.Address, targetAddress, referrersMap))
                {
                    visited.Clear();
                    currentPath.Clear();
                    
                    if (FindPathDFS(heap, root, targetAddress, currentPath, visited, referrersMap))
                    {
                        paths.Add(new List<ReferenceNode>(currentPath));
                        
                        if (paths.Count >= 10) // Limit total paths
                            break;
                    }
                }
            }

            scanCounter.Complete();

            return paths;
        }

        private Dictionary<ulong, List<ulong>> BuildReferrersMap(ClrHeap heap, ulong targetAddress)
        {
            var referrersMap = new Dictionary<ulong, List<ulong>>(capacity: 512);
            var targetRegion = new HashSet<ulong>(capacity: 1024);

            // Collect target and nearby objects to limit search space
            var queue = new Queue<ulong>(capacity: 256);
            queue.Enqueue(targetAddress);
            targetRegion.Add(targetAddress);

            int maxRegionSize = 10000;
            while (queue.Count > 0 && targetRegion.Count < maxRegionSize)
            {
                var current = queue.Dequeue();
                var obj = heap.GetObject(current);

                if (!obj.IsValid) continue;

                foreach (var reference in obj.EnumerateReferences(carefully: true))
                {
                    if (!reference.IsValid) continue;

                    // Use TryGetValue pattern for better performance
                    if (!referrersMap.TryGetValue(reference.Address, out var list))
                    {
                        list = new List<ulong>();
                        referrersMap[reference.Address] = list;
                    }
                    list.Add(current);

                    if (targetRegion.Add(reference.Address))
                    {
                        queue.Enqueue(reference.Address);
                    }
                }
            }

            return referrersMap;
        }

        private bool CanReachTarget(ClrHeap heap, ulong startAddress, ulong targetAddress, 
            Dictionary<ulong, List<ulong>> referrersMap)
        {
            if (startAddress == targetAddress)
                return true;

            var visited = new HashSet<ulong>(capacity: 1024) { startAddress };
            var queue = new Queue<ulong>(capacity: 256);
            queue.Enqueue(startAddress);

            int maxSearch = 5000;
            int searched = 0;

            while (queue.Count > 0 && searched++ < maxSearch)
            {
                var current = queue.Dequeue();
                var obj = heap.GetObject(current);

                if (!obj.IsValid) continue;

                foreach (var reference in obj.EnumerateReferences(carefully: true))
                {
                    if (!reference.IsValid) continue;

                    if (reference.Address == targetAddress)
                        return true;

                    if (visited.Add(reference.Address))
                    {
                        queue.Enqueue(reference.Address);
                    }
                }
            }

            return false;
        }

        private bool FindPathDFS(ClrHeap heap, ClrRoot root, ulong targetAddress,
            List<ReferenceNode> currentPath, HashSet<ulong> visited, 
            Dictionary<ulong, List<ulong>> referrersMap)
        {
            var rootNode = new ReferenceNode
            {
                Address = root.Object.Address,
                TypeName = root.Object.Type?.Name ?? "Unknown",
                FieldName = $"[GC Root: {root.RootKind}]",
                IsRoot = true
            };

            currentPath.Add(rootNode);

            if (root.Object.Address == targetAddress)
                return true;

            if (currentPath.Count > MaxDepth)
            {
                currentPath.RemoveAt(currentPath.Count - 1);
                return false;
            }

            return ExploreReferences(heap, root.Object, targetAddress, currentPath, visited);
        }

        private bool ExploreReferences(ClrHeap heap, ClrObject obj, ulong targetAddress,
            List<ReferenceNode> currentPath, HashSet<ulong> visited)
        {
            if (!obj.IsValid || !visited.Add(obj.Address))
                return false;

            foreach (var reference in obj.EnumerateReferences(carefully: true))
            {
                if (!reference.IsValid) continue;

                string fieldName = GetFieldName(obj, reference.Address);

                var node = new ReferenceNode
                {
                    Address = reference.Address,
                    TypeName = reference.Type?.Name ?? StringConstants.UnknownType,
                    FieldName = fieldName,
                    IsRoot = false
                };

                currentPath.Add(node);

                if (reference.Address == targetAddress)
                    return true;

                if (currentPath.Count <= MaxDepth)
                {
                    if (ExploreReferences(heap, reference, targetAddress, currentPath, visited))
                        return true;
                }

                currentPath.RemoveAt(currentPath.Count - 1);
            }

            return false;
        }

        private string GetFieldName(ClrObject obj, ulong referenceAddress)
        {
            if (obj.Type == null) return "?";

            foreach (var field in obj.Type.Fields)
            {
                try
                {
                    if (field.IsObjectReference)
                    {
                        var fieldObj = field.ReadObject(obj, interior: false);
                        if (fieldObj.IsValid && fieldObj.Address == referenceAddress)
                        {
                            return field.Name ?? "?";
                        }
                    }
                }
                catch { }
            }

            // Check if it's in an array
            if (obj.IsArray)
            {
                try
                {
                    var array = obj.AsArray();
                    for (int i = 0; i < Math.Min(array.Length, 100); i++)
                    {
                        var element = array.GetObjectValue(i);
                        if (element.IsValid && element.Address == referenceAddress)
                        {
                            return $"[{i}]";
                        }
                    }
                }
                catch { }
            }

            return "[reference]";
        }

        private void PrintPath(List<ReferenceNode> path)
        {
            // Pre-allocate space strings to avoid repeated allocations
            for (int i = 0; i < path.Count; i++)
            {
                var node = path[i];
                string indent = new string(' ', 6 + i * 2);

                if (node.IsRoot)
                {
                    _writer.WriteLine($"{indent}↓ {node.FieldName}");
                    _writer.WriteLine($"{indent}  {node.TypeName} @ 0x{node.Address:X}");
                }
                else
                {
                    _writer.WriteLine($"{indent}↓ .{node.FieldName}");
                    _writer.WriteLine($"{indent}  {node.TypeName} @ 0x{node.Address:X}");
                }
            }
        }
    }

    internal class ReferenceNode
    {
        public ulong Address { get; set; }
        public string TypeName { get; set; } = string.Empty;
        public string FieldName { get; set; } = string.Empty;
        public bool IsRoot { get; set; }
    }
}
