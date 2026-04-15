using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class StaticRootLeakDetector : IAnalyzer
    {
        private const int MaxRootsToReport = 15;
        private const int TopRetainedTypesToReport = 5;
        private const int SampleRetainedObjectsToInspect = 100;
        private const ulong SignificantMemoryThresholdBytes = 1024 * 1024;
        private const int SignificantObjectCountThreshold = 100;
        private const int MaxRetainedObjectsToScan = 10000;

        public string Name => "Static Root Leak Detection";

        public AnalyzerExecutionResult Execute(AnalysisContext context) => Analyze(context.Heap, context.Cache);

        public AnalyzerExecutionResult Analyze(ClrHeap heap, HeapAnalysisCache cache)
        {
            var staticRootAnalysis = AnalyzeStaticRoots(heap, cache);

            if (staticRootAnalysis.Count == 0)
            {
                return new AnalyzerExecutionResult(
                    [new InsightFinding(
                        Analyzer: nameof(StaticRootLeakDetector),
                        Category: "Leak",
                        Severity: FindingSeverity.Info,
                        Title: "No high-impact static roots",
                        Evidence: "Static-root scan found no roots exceeding significant memory/object thresholds.",
                        Recommendation: "No immediate static-root retention remediation required.",
                        Tags: ["static-root", "leak", "retention"],
                        MetricValue: 0,
                        MetricUnit: "retained-bytes")],
                    new StaticRootDomainResult(0, 0));
            }

            ulong totalImpact = 0;
            foreach (var item in staticRootAnalysis)
                totalImpact += item.TotalMemoryImpact;

            var topRoots = staticRootAnalysis
                .OrderByDescending(r => r.TotalMemoryImpact)
                .Take(MaxRootsToReport)
                .Select(r => new NameBytesEntry(FormatHelper.TruncateString(r.RootDescription, 90), r.TotalMemoryImpact))
                .ToList();

            return new AnalyzerExecutionResult(
                [CreateFinding(staticRootAnalysis)],
                new StaticRootDomainResult(staticRootAnalysis.Count, totalImpact, topRoots));
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
