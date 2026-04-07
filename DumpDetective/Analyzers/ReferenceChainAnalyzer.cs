using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class ReferenceChainAnalyzer
    {
        private readonly OutputWriter _writer;
        private const int MaxPathsToShow = 5;
        private const int MaxDepth = 50;

        public ReferenceChainAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void AnalyzeTopTypes(ClrHeap heap, int topCount = 10)
        {
            _writer.WriteHeader("REFERENCE CHAIN ANALYSIS:");
            _writer.WriteLine("Finding why top memory-consuming objects are still alive...\n");

            // Get top types by total memory
            var typeStats = new Dictionary<string, (int Count, ulong TotalSize)>();

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? "Unknown";
                if (!typeStats.ContainsKey(typeName))
                {
                    typeStats[typeName] = (0, 0);
                }
                var stats = typeStats[typeName];
                typeStats[typeName] = (stats.Count + 1, stats.TotalSize + obj.Size);
            }

            var topTypes = typeStats
                .OrderByDescending(kvp => kvp.Value.TotalSize)
                .Take(topCount)
                .ToList();

            int typeNum = 1;
            foreach (var typeKvp in topTypes)
            {
                string typeName = typeKvp.Key;
                
                // Skip system types
                if (IsSystemType(typeName))
                    continue;

                _writer.WriteLine($"\n[{typeNum++}] Type: {typeName}");
                _writer.WriteLine($"    Count: {typeKvp.Value.Count:N0}");
                _writer.WriteLine($"    Total Size: {FormatHelper.FormatBytes(typeKvp.Value.TotalSize)}");
                _writer.WriteSeparator();

                // Find sample instance
                ClrObject? sampleObj = null;
                foreach (ClrObject obj in heap.EnumerateObjects())
                {
                    if (obj.IsValid && obj.Type?.Name == typeName)
                    {
                        sampleObj = obj;
                        break;
                    }
                }

                if (sampleObj.HasValue && sampleObj.Value.IsValid)
                {
                    AnalyzeObject(heap, sampleObj.Value.Address);
                }
                else
                {
                    _writer.WriteLine("    No valid sample instance found.");
                }
            }

            _writer.WriteLine($"\n{new string('=', 80)}");
        }

        public void AnalyzeObject(ClrHeap heap, ulong objectAddress)
        {
            ClrObject obj = heap.GetObject(objectAddress);
            
            if (!obj.IsValid)
            {
                _writer.WriteLine($"    Object at 0x{objectAddress:X} is not valid.");
                return;
            }

            _writer.WriteLine($"    Sample Instance: 0x{objectAddress:X}");
            _writer.WriteLine($"    Type: {obj.Type?.Name ?? "Unknown"}");
            _writer.WriteLine($"    Size: {FormatHelper.FormatBytes(obj.Size)}");
            
            var paths = FindPathsToRoot(heap, objectAddress);

            if (paths.Count == 0)
            {
                _writer.WriteLine("    Status: No GC root found (may be eligible for collection)");
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
            }
        }

        private List<List<ReferenceNode>> FindPathsToRoot(ClrHeap heap, ulong targetAddress)
        {
            var paths = new List<List<ReferenceNode>>();
            var visited = new HashSet<ulong>();
            var currentPath = new List<ReferenceNode>();

            // Build reverse reference map for faster lookup
            var referrersMap = BuildReferrersMap(heap, targetAddress);

            // Find all roots that can reach this object
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
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

            return paths;
        }

        private Dictionary<ulong, List<ulong>> BuildReferrersMap(ClrHeap heap, ulong targetAddress)
        {
            var referrersMap = new Dictionary<ulong, List<ulong>>();
            var targetRegion = new HashSet<ulong>();
            
            // Collect target and nearby objects to limit search space
            var queue = new Queue<ulong>();
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
                    
                    if (!referrersMap.ContainsKey(reference.Address))
                    {
                        referrersMap[reference.Address] = new List<ulong>();
                    }
                    referrersMap[reference.Address].Add(current);

                    if (!targetRegion.Contains(reference.Address))
                    {
                        targetRegion.Add(reference.Address);
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

            var visited = new HashSet<ulong> { startAddress };
            var queue = new Queue<ulong>();
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

                    if (!visited.Contains(reference.Address))
                    {
                        visited.Add(reference.Address);
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
            if (!obj.IsValid || visited.Contains(obj.Address))
                return false;

            visited.Add(obj.Address);

            foreach (var reference in obj.EnumerateReferences(carefully: true))
            {
                if (!reference.IsValid) continue;

                string fieldName = GetFieldName(obj, reference.Address);
                
                var node = new ReferenceNode
                {
                    Address = reference.Address,
                    TypeName = reference.Type?.Name ?? "Unknown",
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
    }

    internal class ReferenceNode
    {
        public ulong Address { get; set; }
        public string TypeName { get; set; } = string.Empty;
        public string FieldName { get; set; } = string.Empty;
        public bool IsRoot { get; set; }
    }
}
