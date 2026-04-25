using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    public class GCHandleAnalyzer : IAnalyzer
    {
        private const int TopTypeCount = 15;

        public string Name => "GC Handle Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Cache).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap? heap = null, IHeapAnalysisCache? cache = null)
        {
            var scanCounter = new ObjectScanCounter("GC handle scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var pinnedTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var allTargetTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            // OPT-#9: Cache method-table -> type-name to avoid one heap.GetObject call per handle
            // for handles whose target type has already been resolved. Collapses N handles of the
            // same type to a single heap dereference — same pattern as stringMethodTables in MemoryLeakAnalyzer.
            var methodTableNameCache = new Dictionary<ulong, string>(capacity: 128);
            // use passed-in cache when available

            int totalHandles = 0;
            int strongLikeHandles = 0;
            int weakLikeHandles = 0;

            foreach (ClrHandle handle in runtime.EnumerateHandles())
            {
                scanCounter.Tick();
                totalHandles++;

                string kind = handle.HandleKind.ToString();
                Increment(byKind, kind);

                if (IsWeakLike(kind))
                    weakLikeHandles++;
                else
                    strongLikeHandles++;

                ulong targetAddress = GetTargetAddress(handle);
                string? typeName;
                if (heap is not null && cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out var build))
                {
                    // Fast-path: resolve type name from index's TypeAggregates by method-table if possible
                    ClrObject targetObject = heap.GetObject(targetAddress);
                    if (targetObject.IsValid)
                    {
                        ulong mt = targetObject.Type?.MethodTable ?? 0;
                        if (mt != 0 && build.TypeAggregates.TryGetValue(mt, out var agg))
                        {
                            // Resolve sample-based type name from heap when available
                            if (agg.SampleAddress != 0)
                            {
                                ClrObject sample = heap.GetObject(agg.SampleAddress);
                                typeName = sample.IsValid && sample.Type != null ? sample.Type.Name : ResolveTargetTypeName(heap, targetAddress, methodTableNameCache);
                            }
                            else
                            {
                                typeName = ResolveTargetTypeName(heap, targetAddress, methodTableNameCache);
                            }
                        }
                        else
                        {
                            typeName = ResolveTargetTypeName(heap, targetAddress, methodTableNameCache);
                        }
                    }
                    else
                    {
                        typeName = ResolveTargetTypeName(heap, targetAddress, methodTableNameCache);
                    }
                }
                else
                {
                    typeName = ResolveTargetTypeName(heap, targetAddress, methodTableNameCache);
                }
                if (typeName == null)
                    continue;

                Increment(allTargetTypes, typeName);

                if (kind.Contains("Pinned", StringComparison.OrdinalIgnoreCase))
                    Increment(pinnedTypes, typeName);
            }

            scanCounter.Complete();

            int pinnedHandleTargets = pinnedTypes.Values.Sum();
            static List<NameCountEntry> ToTopEntries(Dictionary<string, int> source, int take)
            {
                var list = new List<NameCountEntry>(Math.Min(source.Count, take));
                foreach (var kvp in source.OrderByDescending(k => k.Value).Take(take))
                    list.Add(new NameCountEntry(kvp.Key, kvp.Value));
                return list;
            }

            return new GCHandleDomainResult(
                    totalHandles,
                    strongLikeHandles,
                    weakLikeHandles,
                    pinnedHandleTargets,
                    ToTopEntries(byKind, TopTypeCount),
                    ToTopEntries(allTargetTypes, TopTypeCount),
                    ToTopEntries(pinnedTypes, TopTypeCount));
        }

        private static InsightFinding CreateFinding(int totalHandles, Dictionary<string, int> pinnedTypes)
        {
            int pinnedHandleTargets = 0;
            foreach (var kv in pinnedTypes)
            {
                pinnedHandleTargets += kv.Value;
            }

            FindingSeverity severity = pinnedHandleTargets >= 1000 || totalHandles >= 10000
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(GCHandleAnalyzer),
                Category: "GC",
                Severity: severity,
                Title: "GC handle pressure summary",
                Evidence: $"Total handles: {totalHandles:N0}; pinned-handle target count: {pinnedHandleTargets:N0}; pinned target types: {pinnedTypes.Count:N0}.",
                Recommendation: severity == FindingSeverity.Warning
                    ? "Inspect pinned-handle-heavy types and reduce long-lived pinning where possible."
                    : "Handle distribution appears within expected bounds for this snapshot.",
                Tags: ["gc-handle", "pinning", "retention"],
                MetricValue: totalHandles,
                MetricUnit: "total-handles");
        }

        private static bool IsWeakLike(string kind)
        {
            return kind.Contains("Weak", StringComparison.OrdinalIgnoreCase)
                || kind.Contains("Dependent", StringComparison.OrdinalIgnoreCase);
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            if (counts.TryGetValue(key, out int value))
                counts[key] = value + 1;
            else
                counts[key] = 1;
        }

        private static ulong GetTargetAddress(ClrHandle handle)
        {
            object boxedTarget = handle.Object;

            if (boxedTarget is ClrObject clrObject)
            {
                return clrObject.IsValid ? clrObject.Address : 0;
            }

            if (boxedTarget is ulong address)
            {
                return address;
            }

            return 0;
        }

        private static string? ResolveTargetTypeName(ClrHeap? heap, ulong targetAddress, Dictionary<ulong, string> methodTableNameCache)
        {
            if (targetAddress == 0)
                return null;

            if (heap == null)
                return $"Object@0x{targetAddress:X}";

            ClrObject targetObject = heap.GetObject(targetAddress);
            if (!targetObject.IsValid)
                return $"Object@0x{targetAddress:X}";

            ulong methodTable = targetObject.Type?.MethodTable ?? 0;
            if (methodTable != 0 && methodTableNameCache.TryGetValue(methodTable, out string? cached))
                return cached;

            string name = targetObject.Type?.Name ?? StringConstants.UnknownType;
            if (methodTable != 0)
                methodTableNameCache[methodTable] = name;

            return name;
        }
    }
}


