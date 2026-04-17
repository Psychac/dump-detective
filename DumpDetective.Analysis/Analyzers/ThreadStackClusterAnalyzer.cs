using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    internal class ThreadStackClusterAnalyzer : IAnalyzer
    {
        private const int MaxFramesPerSignature = 6;
        private const int MaxThreadIdsPerCluster = 8;

        public string Name => "Thread Stack Signature Clustering";

        public AnalyzerExecutionResult Execute(AnalysisContext context) => Analyze(context.Runtime);

        public AnalyzerExecutionResult Analyze(ClrRuntime runtime)
        {
            var clusters = new Dictionary<string, StackCluster>(StringComparer.Ordinal);
            int aliveThreads = 0;
            var scanCounter = new ObjectScanCounter("Thread clustering scan", reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (ClrThread thread in runtime.Threads)
            {
                scanCounter.Tick();

                if (!thread.IsAlive)
                    continue;

                aliveThreads++;
                string signature = BuildSignature(thread);

                if (!clusters.TryGetValue(signature, out StackCluster? cluster))
                {
                    cluster = new StackCluster(signature);
                    clusters[signature] = cluster;
                }

                cluster.Count++;
                if (cluster.SampleThreadIds.Count < MaxThreadIdsPerCluster)
                    cluster.SampleThreadIds.Add(thread.OSThreadId);
            }

            scanCounter.Complete();

            if (clusters.Count == 0)
            {
                return new AnalyzerExecutionResult(
                    [new InsightFinding(
                        Analyzer: nameof(ThreadStackClusterAnalyzer),
                        Category: "Threading",
                        Severity: FindingSeverity.Info,
                        Title: "No thread clusters available",
                        Evidence: "No alive managed threads were available for stack-signature clustering.",
                        Recommendation: "Capture a dump with active managed threads for clustering insights.",
                        Tags: ["thread-cluster", "threads", "diagnostics"],
                        MetricValue: 0,
                        MetricUnit: "% signature-diversity")],
                    new ThreadStackClusterDomainResult(aliveThreads, 0, 0, 0, []));
            }

            var topClusters = clusters.Values
                .OrderByDescending(c => c.Count)
                .ThenBy(c => c.Signature, StringComparer.Ordinal)
                .ToList();

            double diversity = aliveThreads == 0 ? 0 : clusters.Count * 100.0 / aliveThreads;
            int singletonSignatures = topClusters.Count(c => c.Count == 1);
            var topSignatures = topClusters.Take(5).Select(c => c.Signature).ToList();
            var topClusterSnapshots = topClusters
                .Take(12)
                .Select(c => new ThreadClusterSnapshot(c.Count, c.SampleThreadIds.ToList(), c.Signature))
                .ToList();
            return new AnalyzerExecutionResult(
                [CreateFinding(aliveThreads, clusters.Count)],
                new ThreadStackClusterDomainResult(aliveThreads, clusters.Count, singletonSignatures, diversity, topSignatures, topClusterSnapshots));
        }

        private static InsightFinding CreateFinding(int aliveThreads, int uniqueClusters)
        {
            double diversity = aliveThreads == 0 ? 0 : uniqueClusters * 100.0 / aliveThreads;
            FindingSeverity severity = diversity <= 25 ? FindingSeverity.Warning : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(ThreadStackClusterAnalyzer),
                Category: "Threading",
                Severity: severity,
                Title: "Thread stack-signature diversity",
                Evidence: $"{uniqueClusters:N0} unique signatures across {aliveThreads:N0} alive threads ({diversity:F1}% diversity).",
                Recommendation: severity == FindingSeverity.Warning
                    ? "Low diversity suggests hotspot wait/execution patterns; inspect top clusters for bottlenecks."
                    : "Thread stack diversity appears healthy for this snapshot.",
                Tags: ["thread-cluster", "hotspot", "contention"],
                MetricValue: diversity,
                MetricUnit: "% signature-diversity");
        }

        private static string BuildSignature(ClrThread thread)
        {
            var parts = new List<string>(MaxFramesPerSignature);

            foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
            {
                string? name = frame.Method?.Signature;
                if (string.IsNullOrWhiteSpace(name))
                    name = frame.FrameName;

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                parts.Add(name.Trim());
                if (parts.Count >= MaxFramesPerSignature)
                    break;
            }

            if (parts.Count == 0)
                return "<No managed frames>";

            return string.Join(" | ", parts);
        }

        private sealed class StackCluster
        {
            public string Signature { get; }
            public int Count { get; set; }
            public List<uint> SampleThreadIds { get; } = new(capacity: MaxThreadIdsPerCluster);

            public StackCluster(string signature)
            {
                Signature = signature;
            }
        }
    }
}


