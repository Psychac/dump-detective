using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class ThreadStackClusterAnalyzer
    {
        private const int MaxFramesPerSignature = 6;
        private const int MaxClustersToDisplay = 12;
        private const int MaxThreadIdsPerCluster = 8;
        private readonly OutputWriter _writer;

        public ThreadStackClusterAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrRuntime runtime)
        {
            _writer.WriteHeader("THREAD STACK SIGNATURE CLUSTERING:");
            _writer.WriteLine("Grouping alive threads by top stack-frame signatures to highlight hot wait/execution patterns...\n");

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
                {
                    cluster.SampleThreadIds.Add(thread.OSThreadId);
                }
            }

            scanCounter.Complete();

            _writer.WriteLine("CLUSTER SUMMARY:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Alive Threads: {aliveThreads:N0}");
            _writer.WriteLine($"Unique Stack Signatures: {clusters.Count:N0}");
            _writer.WriteLine($"Singleton Signatures: {clusters.Values.Count(c => c.Count == 1):N0}");

            if (clusters.Count == 0)
            {
                _writer.WriteLine("\nNo alive managed threads were available for clustering.");
                _writer.WriteLine(StringConstants.Equals80);
                return;
            }

            _writer.WriteLine("\nTOP THREAD CLUSTERS:");
            _writer.WriteSeparator();

            int shown = 0;
            foreach (StackCluster cluster in clusters.Values
                .OrderByDescending(c => c.Count)
                .ThenBy(c => c.Signature, StringComparer.Ordinal))
            {
                if (shown >= MaxClustersToDisplay)
                    break;

                string sampleThreadIds = string.Join(", ", cluster.SampleThreadIds.Select(id => $"0x{id:X}"));
                _writer.WriteLine($"\n[{cluster.Count,4} threads] Sample OSThreadIds: {sampleThreadIds}");
                _writer.WriteLine($"Signature: {FormatHelper.TruncateString(cluster.Signature, 180)}");
                shown++;
            }

            _writer.WriteLine("\nTip: Large clusters with wait-related signatures often indicate contention bottlenecks or hangs.");
            _writer.WriteLine(StringConstants.Equals80);
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
