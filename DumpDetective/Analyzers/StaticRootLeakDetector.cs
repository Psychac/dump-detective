using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class StaticRootLeakDetector
    {
        private const int MaxRootsToReport = 15;
        private const int TopRetainedTypesToReport = 5;
        private const int SampleRetainedObjectsToInspect = 100;
        private const ulong SignificantMemoryThresholdBytes = 1024 * 1024;
        private const int SignificantObjectCountThreshold = 100;
        private const int MaxRetainedObjectsToScan = 10000;

        private readonly OutputWriter _writer;

        public StaticRootLeakDetector(OutputWriter writer)
        {
            _writer = writer;
        }

        public IReadOnlyList<InsightFinding> Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            _writer.WriteHeader("STATIC ROOT LEAK DETECTION:");
            _writer.WriteLine("Identifying static fields that may be causing memory leaks...\n");

            var findings = new List<InsightFinding>(capacity: 1);

            var staticRootAnalysis = AnalyzeStaticRoots(heap, cache);

            if (staticRootAnalysis.Count == 0)
            {
                _writer.WriteLine("No concerning static roots found.");
                findings.Add(new InsightFinding(
                    Analyzer: nameof(StaticRootLeakDetector),
                    Category: "Leak",
                    Severity: FindingSeverity.Info,
                    Title: "No high-impact static roots",
                    Evidence: "Static-root scan found no roots exceeding significant memory/object thresholds.",
                    Recommendation: "No immediate static-root retention remediation required.",
                    Tags: ["static-root", "leak", "retention"],
                    MetricValue: 0,
                    MetricUnit: "retained-bytes"));
                _writer.WriteLine($"\n{StringConstants.Equals80}");
                return findings;
            }

            _writer.WriteLine($"Found {staticRootAnalysis.Count} static root(s) with significant memory impact:\n");

            // Manual sorting - no LINQ allocations
            staticRootAnalysis.Sort((a, b) => b.TotalMemoryImpact.CompareTo(a.TotalMemoryImpact));

            int rootNum = 1;
            int rootCount = 0;
            foreach (var analysis in staticRootAnalysis)
            {
                if (rootCount >= MaxRootsToReport) break;
                _writer.WriteLine($"[{rootNum++}] STATIC ROOT LEAK");
                _writer.WriteSeparator();
                _writer.WriteLine($"  Root: {analysis.RootDescription}");
                _writer.WriteLine($"  Direct Object: {analysis.DirectObjectType} @ 0x{analysis.DirectObjectAddress:X}");
                _writer.WriteLine($"  Direct Size: {FormatHelper.FormatBytes(analysis.DirectObjectSize)}");
                _writer.WriteLine($"  Total Retained Memory: {FormatHelper.FormatBytes(analysis.TotalMemoryImpact)}");
                _writer.WriteLine($"  Objects Kept Alive: {analysis.ObjectsKeptAlive:N0}");

                if (analysis.TopRetainedTypes.Count > 0)
                {
                    _writer.WriteLine($"\n  Top Types Kept Alive:");
                    int typeCount = 0;
                    foreach (var typeInfo in analysis.TopRetainedTypes)
                    {
                        if (typeCount >= TopRetainedTypesToReport) break;
                        _writer.WriteLine($"    - {typeInfo.TypeName}: {typeInfo.Count:N0} instance(s), {FormatHelper.FormatBytes(typeInfo.TotalSize)}");
                        typeCount++;
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
                rootCount++;
            }

            findings.Add(CreateFinding(staticRootAnalysis));

            _writer.WriteLine(StringConstants.Equals80);
            return findings;
        }

        private static InsightFinding CreateFinding(List<StaticRootAnalysis> staticRootAnalysis)
        {
            ulong totalImpact = 0;
            foreach (var item in staticRootAnalysis)
            {
                totalImpact += item.TotalMemoryImpact;
            }

            FindingSeverity severity = staticRootAnalysis.Count >= 10
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            return new InsightFinding(
                Analyzer: nameof(StaticRootLeakDetector),
                Category: "Leak",
                Severity: severity,
                Title: "Static-root retention candidates detected",
                Evidence: $"{staticRootAnalysis.Count:N0} root(s) retain ~{FormatHelper.FormatBytes(totalImpact)} cumulative memory.",
                Recommendation: "Audit static ownership and clear or weaken references for expired object graphs.",
                Tags: ["static-root", "retention", "memory-leak"],
                MetricValue: totalImpact,
                MetricUnit: "retained-bytes");
        }

        private List<StaticRootAnalysis> AnalyzeStaticRoots(ClrHeap heap, HeapAnalysisCache cache)
        {
            var results = new List<StaticRootAnalysis>();
            var processedRoots = new HashSet<ulong>();
            var scanCounter = new ObjectScanCounter("Static root scan");

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                scanCounter.Tick();

                if (!root.RootKind.ToString().Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                ClrObject obj = root.Object;
                if (!obj.IsValid || !processedRoots.Add(obj.Address))
                    continue;

                var retainedObjects = cache.GetRetainedObjects(heap, obj.Address, MaxRetainedObjectsToScan);

                var typeStats = new Dictionary<string, RetainedTypeInfo>();
                ulong totalSize = 0;
                bool containsCollections = false;
                bool containsEventHandlers = false;
                int sampledCount = 0;

                foreach (var address in retainedObjects)
                {
                    var retainedObj = heap.GetObject(address);
                    if (!retainedObj.IsValid || retainedObj.Type == null)
                        continue;

                    totalSize += retainedObj.Size;

                    string typeName = retainedObj.Type.Name ?? StringConstants.UnknownType;
                    if (!typeStats.TryGetValue(typeName, out var info))
                    {
                        info = new RetainedTypeInfo { TypeName = typeName };
                        typeStats[typeName] = info;
                    }

                    info.Count++;
                    info.TotalSize += retainedObj.Size;

                    if (sampledCount < SampleRetainedObjectsToInspect)
                    {
                        if (!containsCollections && TypeFilterHelper.IsCollectionType(typeName))
                        {
                            containsCollections = true;
                        }

                        if (!containsEventHandlers)
                        {
                            foreach (var field in retainedObj.Type.Fields)
                            {
                                if (TypeFilterHelper.IsDelegateType(field.Type))
                                {
                                    containsEventHandlers = true;
                                    break;
                                }
                            }
                        }

                        sampledCount++;
                    }
                }

                // Only report if significant memory impact (> 1MB or > 100 objects)
                if (totalSize > SignificantMemoryThresholdBytes || retainedObjects.Count > SignificantObjectCountThreshold)
                {
                    var analysis = new StaticRootAnalysis
                    {
                        RootDescription = root.ToString() ?? "Unknown Static Root",
                        DirectObjectAddress = obj.Address,
                        DirectObjectType = obj.Type?.Name ?? StringConstants.UnknownType,
                        DirectObjectSize = obj.Size,
                        TotalMemoryImpact = totalSize,
                        ObjectsKeptAlive = retainedObjects.Count,
                        TopRetainedTypes = GetTopRetainedTypes(typeStats),
                        ContainsCollections = containsCollections,
                        ContainsEventHandlers = containsEventHandlers
                    };

                    results.Add(analysis);
                }
            }

            scanCounter.Complete();

            return results;
        }

        private List<RetainedTypeInfo> GetTopRetainedTypes(Dictionary<string, RetainedTypeInfo> typeStats)
        {
            // Manual sorting - no LINQ allocations
            var result = new List<RetainedTypeInfo>(typeStats.Values);
            result.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
            return result;
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
