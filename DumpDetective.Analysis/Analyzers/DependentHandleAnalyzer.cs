using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using System.Reflection;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Analyzers
{
    internal class DependentHandleAnalyzer : IAnalyzer
    {
        private const int TopCount = 15;

        public string Name => "Dependent Handle Analysis";

        public AnalyzerExecutionResult Execute(AnalysisContext context) => Analyze(context.Runtime);

        public AnalyzerExecutionResult Analyze(ClrRuntime runtime)
        {
            var scanCounter = new ObjectScanCounter("Dependent handle scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            int dependentHandleCount = 0;
            int resolvedEdgeCount = 0;
            int unresolvedTargetCount = 0;

            var sourceTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var targetTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sourceTargetPairCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (ClrHandle handle in runtime.EnumerateHandles())
            {
                scanCounter.Tick();
                string kind = handle.HandleKind.ToString();
                if (!kind.Contains("Dependent", StringComparison.OrdinalIgnoreCase))
                    continue;

                dependentHandleCount++;

                if (!TryGetHandleObject(handle.Object, runtime.Heap, out ClrObject sourceObj))
                {
                    unresolvedTargetCount++;
                    continue;
                }

                string sourceType = sourceObj.Type?.Name ?? StringConstants.UnknownType;
                Increment(sourceTypeCounts, sourceType);

                if (!TryGetDependentTargetObject(handle, runtime.Heap, out ClrObject targetObj))
                {
                    unresolvedTargetCount++;
                    continue;
                }

                resolvedEdgeCount++;
                string targetType = targetObj.Type?.Name ?? StringConstants.UnknownType;
                Increment(targetTypeCounts, targetType);
                Increment(sourceTargetPairCounts, $"{sourceType} -> {targetType}");
            }

            scanCounter.Complete();

            double unresolvedPct = dependentHandleCount == 0 ? 0
                : unresolvedTargetCount * 100.0 / dependentHandleCount;

            if (dependentHandleCount == 0)
            {
                return new AnalyzerExecutionResult(
                    [],
                    new DependentHandleDomainResult(0, 0, 0, 0));
            }

            static List<NameCountEntry> ToTopEntries(Dictionary<string, int> source, int take)
            {
                var list = new List<NameCountEntry>(Math.Min(source.Count, take));
                foreach (var kvp in source.OrderByDescending(k => k.Value).Take(take))
                    list.Add(new NameCountEntry(kvp.Key, kvp.Value));
                return list;
            }

            return new AnalyzerExecutionResult(
                [CreateFinding(dependentHandleCount, resolvedEdgeCount, unresolvedTargetCount)],
                new DependentHandleDomainResult(
                    dependentHandleCount,
                    resolvedEdgeCount,
                    unresolvedTargetCount,
                    unresolvedPct,
                    ToTopEntries(sourceTypeCounts, TopCount),
                    ToTopEntries(targetTypeCounts, TopCount),
                    ToTopEntries(sourceTargetPairCounts, TopCount)));
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

        private static bool TryGetDependentTargetObject(ClrHandle handle, ClrHeap heap, out ClrObject targetObj)
        {
            targetObj = default;

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

                if (TryGetHandleObject(value, heap, out targetObj))
                    return true;
            }

            return false;
        }

        private static bool TryGetHandleObject(object value, ClrHeap heap, out ClrObject obj)
        {
            obj = default;

            if (value is ClrObject clrObject)
            {
                if (!clrObject.IsValid || clrObject.Type == null)
                    return false;

                obj = clrObject;
                return true;
            }

            if (value is ulong address && address != 0)
            {
                ClrObject fromAddress = heap.GetObject(address);
                if (fromAddress.IsValid && fromAddress.Type != null)
                {
                    obj = fromAddress;
                    return true;
                }
            }

            return false;
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            if (counts.TryGetValue(key, out int count))
                counts[key] = count + 1;
            else
                counts[key] = 1;
        }
    }
}


