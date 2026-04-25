using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    public class ThreadStackClusterAnalyzer : IAnalyzer
    {
        private const int MaxFramesPerSignature = 6;
        private const int MaxThreadIdsPerCluster = 8;

        public string Name => "Thread Stack Signature Clustering";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Runtime, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime)
        {
            return Analyze(runtime, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, IProgress<AnalyzerProgressReport>? progress)
        {
            var threads = runtime.Threads.ToList();
            var osThreadIdByAddress = new Dictionary<ulong, uint>(capacity: threads.Count);
            foreach (ClrThread thread in threads)
            {
                if (thread.Address != 0)
                    osThreadIdByAddress[thread.Address] = thread.OSThreadId;
            }

            var clusters = new Dictionary<string, StackCluster>(StringComparer.Ordinal);
            int aliveThreads = 0;
            var scanCounter = new ObjectScanCounter("clustering thread stacks", progress, reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (ClrThread thread in threads)
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
                if (cluster.SampleThreadAddresses.Count < MaxThreadIdsPerCluster && thread.Address != 0)
                    cluster.SampleThreadAddresses.Add(thread.Address);
            }

            scanCounter.Complete();

            if (clusters.Count == 0)
            {
                return new ThreadStackClusterDomainResult(aliveThreads, 0, 0, 0, []);
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
                .Select(c => new ThreadClusterSnapshot(c.Count, ProjectSampleOsThreadIds(c.SampleThreadAddresses, osThreadIdByAddress), c.Signature))
                .ToList();
            return new ThreadStackClusterDomainResult(aliveThreads, clusters.Count, singletonSignatures, diversity, topSignatures, topClusterSnapshots);
        }

        private static IReadOnlyList<uint> ProjectSampleOsThreadIds(IReadOnlyList<ulong> sampleThreadAddresses, IReadOnlyDictionary<ulong, uint> osThreadIdByAddress)
        {
            var sampleIds = new List<uint>(sampleThreadAddresses.Count);
            foreach (ulong threadAddress in sampleThreadAddresses)
            {
                if (osThreadIdByAddress.TryGetValue(threadAddress, out uint osThreadId))
                    sampleIds.Add(osThreadId);
            }

            return sampleIds;
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
            public List<ulong> SampleThreadAddresses { get; } = new(capacity: MaxThreadIdsPerCluster);

            public StackCluster(string signature)
            {
                Signature = signature;
            }
        }
    }
}


