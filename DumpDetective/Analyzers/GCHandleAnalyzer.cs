using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class GCHandleAnalyzer
    {
        private const int TopTypeCount = 15;
        private readonly OutputWriter _writer;

        public GCHandleAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public AnalyzerOutput Analyze(ClrRuntime runtime)
        {
            _writer.WriteHeader("GC HANDLE ANALYSIS:");
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

            PrintSummary(totalHandles, strongLikeHandles, weakLikeHandles, byKind);
            PrintTopTypes("TOP TYPES REFERENCED BY HANDLES:", allTargetTypes, TopTypeCount);
            PrintTopTypes("TOP TYPES REFERENCED BY PINNED HANDLES:", pinnedTypes, TopTypeCount);

            int pinnedHandleTargets = pinnedTypes.Values.Sum();
            _writer.WriteLine(StringConstants.Equals80);
            return new AnalyzerOutput(
                [CreateFinding(totalHandles, pinnedTypes)],
                new GCHandleDomainResult(totalHandles, strongLikeHandles, weakLikeHandles, pinnedHandleTargets));
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

        private void PrintSummary(int total, int strongLike, int weakLike, Dictionary<string, int> byKind)
        {
            _writer.WriteLine("HANDLE SUMMARY:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Total Handles: {total:N0}");

            if (total > 0)
            {
                _writer.WriteLine($"Strong-like Handles: {strongLike:N0} ({(strongLike * 100.0 / total):F1}%)");
                _writer.WriteLine($"Weak-like Handles: {weakLike:N0} ({(weakLike * 100.0 / total):F1}%)");
            }

            _writer.WriteLine("\nHANDLES BY KIND:");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"HandleKind",-30} {"Count",12}");
            _writer.WriteSeparator();

            foreach ((string kind, int count) in byKind.OrderByDescending(k => k.Value))
            {
                _writer.WriteLine($"{kind,-30} {count,12:N0}");
            }
        }

        private void PrintTopTypes(string title, Dictionary<string, int> typeCounts, int topCount)
        {
            _writer.WriteLine($"\n{title}");
            _writer.WriteSeparator();

            if (typeCounts.Count == 0)
            {
                _writer.WriteLine("No typed handle targets found.");
                return;
            }

            _writer.WriteLine($"{"Type",-70} {"Count",12}");
            _writer.WriteSeparator();

            int written = 0;
            foreach ((string typeName, int count) in typeCounts.OrderByDescending(t => t.Value))
            {
                if (written >= topCount)
                    break;

                _writer.WriteLine($"{FormatHelper.TruncateString(typeName, 70),-70} {count,12:N0}");
                written++;
            }
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
