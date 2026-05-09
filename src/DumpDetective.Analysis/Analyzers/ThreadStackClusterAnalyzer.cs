using Microsoft.Diagnostics.Runtime;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    public class ThreadStackClusterAnalyzer : IAnalyzer
    {
        public string Name => "Thread Stack Signature Clustering";
        public string Category => "Threads";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThreadStackClusterAnalysisOptions options = context.GetOption<ThreadStackClusterAnalysisOptions>();
            return ValueTask.FromResult(Analyze(context.Runtime, context.Progress, options).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime)
        {
            return Analyze(runtime, progress: null, new ThreadStackClusterAnalysisOptions());
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, IProgress<AnalyzerProgressReport>? progress, ThreadStackClusterAnalysisOptions options)
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
                string signature = BuildSignature(thread, options.MaxFramesPerSignature);

                if (!clusters.TryGetValue(signature, out StackCluster? cluster))
                {
                    cluster = new StackCluster(signature);
                    clusters[signature] = cluster;
                }

                cluster.Count++;
                if (cluster.SampleThreadAddresses.Count < options.MaxThreadIdsPerCluster && thread.Address != 0)
                    cluster.SampleThreadAddresses.Add(thread.Address);
            }

            scanCounter.Complete();

            if (clusters.Count == 0)
            {
                return new ThreadStackClusterDomainResult(aliveThreads, 0, 0, 0, Array.Empty<string>(), null);
            }

            var topClusters = clusters.Values
                .OrderByDescending(c => c.Count)
                .ThenBy(c => c.Signature, StringComparer.Ordinal)
                .ToList();

            double diversity = aliveThreads == 0 ? 0 : clusters.Count * 100.0 / aliveThreads;
            int singletonSignatures = topClusters.Count(c => c.Count == 1);
            var topSignatures = topClusters.Take(options.TopSignaturesToShow).Select(c => c.Signature).ToList();

            // Apply MinClusterSize and MaxClusters before snapshot/export
            var filteredClusters = topClusters.Where(c => c.Count >= Math.Max(1, options.MinClusterSize)).ToList();
            if (filteredClusters.Count > options.MaxClusters)
                filteredClusters = filteredClusters.Take(options.MaxClusters).ToList();

            var topClusterSnapshots = filteredClusters
                .Take(options.TopClustersToShow)
                .Select(c => new ThreadClusterSnapshot(c.Count, ProjectSampleOsThreadIds(c.SampleThreadAddresses, osThreadIdByAddress), c.Signature))
                .ToList();

            IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? rawExports = null;
            if (options.ProduceClusterExports)
            {
                try
                {
                    var artifacts = new List<DumpDetective.Core.Models.ReportArtifact>();
                    // Produce a user-friendly pretty JSON export (inline content)
                    try
                    {
                        var summary = filteredClusters.Select(c => new
                        {
                            count = c.Count,
                            signature = c.Signature,
                            sampleOsThreadIds = ProjectSampleOsThreadIds(c.SampleThreadAddresses, osThreadIdByAddress)
                        }).ToList();

                        var prettyJsonOpts = new JsonSerializerOptions { WriteIndented = true };
                        string prettyJson = JsonSerializer.Serialize(summary, prettyJsonOpts);
                        artifacts.Add(new DumpDetective.Core.Models.ReportArtifact("Thread Stack Cluster", "thread-clusters.json", prettyJson, "application/json"));
                    }
                    catch { }

                    // Produce NDJSON gz export for the filtered clusters (machine-friendly, streaming)
                    string tmp = Path.Combine(Path.GetTempPath(), $"dumpdetective-thread-clusters-{Guid.NewGuid():N}.ndjson.gz");
                    try
                    {
                        using (var fs = File.Create(tmp))
                        using (var gz = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: false))
                        {
                            var jsOpts = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
                            foreach (var c in filteredClusters)
                            {
                                var lineObj = new
                                {
                                    count = c.Count,
                                    signature = c.Signature,
                                    sampleThreadAddresses = c.SampleThreadAddresses,
                                    sampleOsThreadIds = ProjectSampleOsThreadIds(c.SampleThreadAddresses, osThreadIdByAddress)
                                };
                                JsonSerializer.Serialize(gz, lineObj, jsOpts);
                                gz.WriteByte((byte)'\n');
                            }
                        }

                        artifacts.Add(new DumpDetective.Core.Models.ReportArtifact("Thread Stack Cluster", "thread-clusters.ndjson.gz", null, "application/gzip", tmp));
                    }
                    catch
                    {
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    }

                    rawExports = artifacts;
                }
                catch
                {
                    rawExports = null;
                }
            }

            return new ThreadStackClusterDomainResult(aliveThreads, clusters.Count, singletonSignatures, diversity, topSignatures, topClusterSnapshots, rawExports);
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

        private static string BuildSignature(ClrThread thread, int maxFramesPerSignature)
        {
            var parts = new List<string>(maxFramesPerSignature);

            foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
            {
                string? name = frame.Method?.Signature;
                if (string.IsNullOrWhiteSpace(name))
                    name = frame.FrameName;

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                parts.Add(name.Trim());
                if (parts.Count >= maxFramesPerSignature)
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
            public List<ulong> SampleThreadAddresses { get; } = new();

            public StackCluster(string signature)
            {
                Signature = signature;
            }
        }

        public void Dispose() { }
    }
}


