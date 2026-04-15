using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class GCHandleAnalyzer : IAnalyzer
    {
        private const int TopTypeCount = 15;

        public string Name => "GC Handle Analysis";

        public AnalyzerExecutionResult Execute(AnalysisContext context) => Analyze(context.Runtime);

        public AnalyzerExecutionResult Analyze(ClrRuntime runtime)
        {
            var scanCounter = new ObjectScanCounter("GC handle scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var pinnedTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var allTargetTypes = new Dictionary<string, int>(StringComparer.Ordinal);

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

                string? typeName = TryGetTargetTypeName(handle);
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

            return new AnalyzerExecutionResult(
                [CreateFinding(totalHandles, pinnedTypes)],
                new GCHandleDomainResult(
                    totalHandles,
                    strongLikeHandles,
                    weakLikeHandles,
                    pinnedHandleTargets,
                    ToTopEntries(byKind, TopTypeCount),
                    ToTopEntries(allTargetTypes, TopTypeCount),
                    ToTopEntries(pinnedTypes, TopTypeCount)));
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

        private static string? TryGetTargetTypeName(ClrHandle handle)
        {
            object boxedTarget = handle.Object;

            if (boxedTarget is ClrObject clrObject)
            {
                if (!clrObject.IsValid || clrObject.Type == null)
                    return null;

                return clrObject.Type.Name ?? StringConstants.UnknownType;
            }

            if (boxedTarget is ulong address)
            {
                return address == 0 ? null : $"Object@0x{address:X}";
            }

            return null;
        }
    }
}
