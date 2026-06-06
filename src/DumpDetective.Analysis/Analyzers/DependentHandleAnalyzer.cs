using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using System.Reflection;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;
using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Analyzers
{
    public sealed class DependentHandleAnalyzer : IAnalyzer
    {
        public string Name => "Dependent Handle Analysis";
        public string Category => "Handles";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DependentHandleAnalysisOptions options = context.AnalysisOptions.DependentHandleAnalysis;
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, options, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap? heap = null)
        {
            return Analyze(runtime, heap, new DependentHandleAnalysisOptions(), progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap? heap, DependentHandleAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            heap ??= runtime.Heap;

            var scanCounter = new ObjectScanCounter("scanning dependent handles", progress, reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            int dependentHandleCount = 0;
            int resolvedEdgeCount = 0;
            int unresolvedTargetCount = 0;

            var sourceTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var targetTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sourceTargetPairCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            // TODO: Prefer consuming a shared handle snapshot provider (HeapIndexBuildResult.InMemoryHandleSnapshot
            // or IHandleSnapshotReader) when available to avoid repeated calls to runtime.EnumerateHandles().
            foreach (ClrHandle handle in runtime.EnumerateHandles())
            {
                scanCounter.Tick();
                string kind = handle.HandleKind.ToString();
                if (!kind.Contains("Dependent", StringComparison.OrdinalIgnoreCase))
                    continue;

                dependentHandleCount++;

                if (!TryGetHandleAddress(handle.Object, out ulong sourceAddress)
                    || !TryResolveTypeName(heap, sourceAddress, out string sourceType))
                {
                    unresolvedTargetCount++;
                    continue;
                }

                Increment(sourceTypeCounts, sourceType);

                if (!TryGetDependentTargetAddress(handle, out ulong targetAddress)
                    || !TryResolveTypeName(heap, targetAddress, out string targetType))
                {
                    unresolvedTargetCount++;
                    continue;
                }

                resolvedEdgeCount++;
                Increment(targetTypeCounts, targetType);
                Increment(sourceTargetPairCounts, $"{sourceType} -> {targetType}");
            }

            scanCounter.Complete();

            double unresolvedPct = dependentHandleCount == 0 ? 0
                : unresolvedTargetCount * 100.0 / dependentHandleCount;

            if (dependentHandleCount == 0)
            {
                return new DependentHandleDomainResult(0, 0, 0, 0);
            }

            static List<NameCountEntry> ToTopEntries(Dictionary<string, int> source, int take)
            {
                var list = new List<NameCountEntry>(Math.Min(source.Count, take));
                foreach (var kvp in source.OrderByDescending(k => k.Value).Take(take))
                    list.Add(new NameCountEntry(kvp.Key, kvp.Value));
                return list;
            }

            return new DependentHandleDomainResult(
                    dependentHandleCount,
                    resolvedEdgeCount,
                    unresolvedTargetCount,
                    unresolvedPct,
                    ToTopEntries(sourceTypeCounts, options.TopCount),
                    ToTopEntries(targetTypeCounts, options.TopCount),
                    ToTopEntries(sourceTargetPairCounts, options.TopCount));
        }

        private static InsightFinding CreateFinding(int dependentHandleCount, int resolvedEdgeCount, int unresolvedTargetCount)
        {
            double unresolvedPct = dependentHandleCount == 0
                ? 0
                : unresolvedTargetCount * 100.0 / dependentHandleCount;

            FindingSeverity severity = unresolvedPct >= 50
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(DependentHandleAnalyzer),
                Category: "Retention",
                Severity: severity,
                Title: "Dependent-handle retention summary",
                Evidence: $"Dependent handles: {dependentHandleCount:N0}; resolved source->target edges: {resolvedEdgeCount:N0}; unresolved targets: {unresolvedTargetCount:N0} ({unresolvedPct:F1}%).",
                Recommendation: "Inspect dominant dependent-handle source/target pairs to identify hidden retention relationships.",
                Tags: ["dependent-handle", "retention", "conditionalweaktable"],
                MetricValue: unresolvedPct,
                MetricUnit: "% unresolved-targets");
        }

        private static bool TryGetDependentTargetAddress(ClrHandle handle, out ulong targetAddress)
        {
            targetAddress = 0;

            string[] propertyCandidates =
            [
                "DependentTarget",
                "Target",
                "Secondary",
                "DependentObject",
                "Dependent"
            ];

            Type handleType = handle.GetType();
            foreach (string propertyName in propertyCandidates)
            {
                PropertyInfo? property = handleType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (property == null)
                    continue;

                object? value = property.GetValue(handle);
                if (value == null)
                    continue;

                if (TryGetHandleAddress(value, out targetAddress))
                    return true;
            }

            return false;
        }

        private static bool TryGetHandleAddress(object value, out ulong address)
        {
            address = 0;

            if (value is ClrObject clrObject)
            {
                if (!clrObject.IsValid)
                    return false;

                address = clrObject.Address;
                return true;
            }

            if (value is ulong targetAddress && targetAddress != 0)
            {
                address = targetAddress;
                return true;
            }

            return false;
        }

        private static bool TryResolveTypeName(ClrHeap heap, ulong address, out string typeName)
        {
            typeName = StringConstants.UnknownType;

            if (address == 0)
                return false;

            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type == null)
                return false;

            typeName = obj.Type.Name ?? StringConstants.UnknownType;
            return true;
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            if (counts.TryGetValue(key, out int count))
                counts[key] = count + 1;
            else
                counts[key] = 1;
        }

        public void Dispose() { }
    }
}


