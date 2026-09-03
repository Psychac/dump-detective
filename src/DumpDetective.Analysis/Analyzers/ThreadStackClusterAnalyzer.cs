using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

using System.IO.Compression;
using System.Text.Json;

namespace DumpDetective.Analysis.Analyzers
{
    public class ThreadStackClusterAnalyzer : IAnalyzer, IThreadStackScanParticipant
    {
        public string Name => "Thread Stack Signature Clustering";
        public string Category => "Threads";

        // Instance accumulator state for the IThreadStackScanParticipant path — shares
        // ThreadStackScanDispatcher's single EnumerateStackTrace() pass with ThreadAnalyzer/
        // HangAnalyzer/LockGraphAnalyzer instead of independently walking runtime.Threads.
        private Dictionary<ulong, uint>? _participantOsThreadIdByAddress;
        private Dictionary<string, StackCluster>? _participantClusters;
        private Dictionary<string, int>? _participantFrameHistogram;
        private int _participantAliveThreads;
        private ObjectScanCounter? _participantScanCounter;
        private bool _participantScanSucceeded;

        // P2-4: how many entries the frame-level hotspot histogram surfaces in the domain result.
        private const int TopFrameHotspotsToReport = 10;

        // P3-2: shared-prefix cluster tree render-width limits — the trie itself is built from the
        // complete filtered-cluster set, these only bound how many nodes the report renders.
        private const int MaxTreeChildrenPerNode = 8;
        private const int MaxTreeNodes = 400;

        // Safety bound (not a display truncation) on rendered TreeNode nesting depth, same
        // reasoning as RetentionOptions.MaxDominatorChainDepth: a real call stack's depth is
        // bounded only by the deepest recursion in the target app (e.g. an unbounded/stack-overflow
        // recursive call), and each branch point in the trie adds one nested TreeNode.Children
        // level in the JSON report — unbounded depth there previously tripped
        // System.Text.Json's MaxDepth guard ("possible object cycle detected") on real dumps with
        // deep, branchy stacks. Unbranched runs already collapse into one chain node below, so this
        // only bounds how many distinct branch points get their own nesting level.
        private const int MaxTreeDepth = 64;

        // P3-1: well-known framework wait/idle frames matched against a cluster's whole pipe-joined
        // signature via ThreadWaitClassifier — these represent expected framework activity rather
        // than application-level contention, so findings can avoid treating them as hotspots.
        private static readonly WaitPattern[] FrameworkPatterns =
        {
            new("Threadpool-idle", "ThreadPoolWorkQueue", "CLR thread pool worker waiting for work"),
            new("Threadpool-idle", "PortableThreadPool", "CLR thread pool worker waiting for work"),
            new("GC", "<No managed frames> (GC)", "Garbage collector thread"),
            new("Finalizer", "<No managed frames> (Finalizer)", "Finalizer thread waiting on finalization queue"),
            new("IOCP-idle", "<No managed frames> (IOCP)", "I/O completion port thread waiting for completions"),
            new("Threadpool-idle", "<No managed frames> (Threadpool)", "CLR thread pool worker waiting for work"),
        };

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThreadStackClusterAnalysisOptions options = context.AnalysisOptions.ThreadStackClusterAnalysis;
            return ValueTask.FromResult(Analyze(context.Runtime, context.Progress, options).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime)
        {
            return Analyze(runtime, progress: null, new ThreadStackClusterAnalysisOptions());
        }

        public int GetRequiredFrameCount(AnalysisContext context) => ThreadAnalyzer.UnboundedFrameCount;

        public void BeforeThreadStackScan(AnalysisContext context)
        {
            _participantOsThreadIdByAddress = new Dictionary<ulong, uint>();
            _participantClusters = new Dictionary<string, StackCluster>(StringComparer.Ordinal);
            _participantFrameHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            _participantAliveThreads = 0;
            _participantScanCounter = new ObjectScanCounter("clustering thread stacks", context.Progress, reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));
            _participantScanSucceeded = false;
        }

        void IThreadStackScanParticipant.OnThreadStack(in ThreadStackSnapshot snapshot) => OnThreadStack(in snapshot);

        private void OnThreadStack(in ThreadStackSnapshot snapshot)
        {
            _participantScanCounter!.Tick();

            ClrThread thread = snapshot.Thread;
            if (thread.Address != 0)
                _participantOsThreadIdByAddress![thread.Address] = thread.OSThreadId;

            if (!thread.IsAlive)
                return;

            _participantAliveThreads++;
            string signature = BuildSignature(snapshot.TopFrames, thread, _participantFrameHistogram);
            AccumulateCluster(_participantClusters!, signature, thread);
        }

        public void OnThreadStackScanCompleted(bool succeeded)
        {
            _participantScanSucceeded = succeeded;
            if (succeeded)
                _participantScanCounter?.Complete();
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, IProgress<AnalyzerProgressReport>? progress, ThreadStackClusterAnalysisOptions options)
        {
            Dictionary<ulong, uint> osThreadIdByAddress;
            Dictionary<string, StackCluster> clusters;
            Dictionary<string, int> frameHistogram;
            int aliveThreads;

            if (_participantScanSucceeded)
            {
                // BeforeThreadStackScan/OnThreadStack already ran via the pipeline's
                // ThreadStackScanDispatcher — read back the accumulated state instead of a
                // second independent walk of runtime.Threads.
                osThreadIdByAddress = _participantOsThreadIdByAddress!;
                clusters = _participantClusters!;
                frameHistogram = _participantFrameHistogram!;
                aliveThreads = _participantAliveThreads;
            }
            else
            {
                // Fallback (non-participant) path: used when this analyzer is invoked directly
                // (tests, benchmarks) instead of through AnalysisPipeline's dispatcher.
                osThreadIdByAddress = new Dictionary<ulong, uint>();
                foreach (ClrThread thread in runtime.Threads)
                {
                    if (thread.Address != 0)
                        osThreadIdByAddress[thread.Address] = thread.OSThreadId;
                }

                clusters = new Dictionary<string, StackCluster>(StringComparer.Ordinal);
                frameHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
                aliveThreads = 0;
                var scanCounter = new ObjectScanCounter("clustering thread stacks", progress, reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

                foreach (ClrThread thread in runtime.Threads)
                {
                    scanCounter.Tick();

                    if (!thread.IsAlive)
                        continue;

                    aliveThreads++;
                    string signature = BuildSignature(thread.EnumerateStackTrace(), thread, frameHistogram);
                    AccumulateCluster(clusters, signature, thread);
                }

                scanCounter.Complete();
            }

            IReadOnlyList<NameCountEntry> topFrameHotspots = BuildTopFrameHotspots(frameHistogram);

            if (clusters.Count == 0)
            {
                return new ThreadStackClusterDomainResult(aliveThreads, 0, 0, 0, Array.Empty<string>(), null, TopFrameHotspots: topFrameHotspots);
            }

            var topClusters = clusters.Values
                .OrderByDescending(c => c.Count)
                .ThenBy(c => c.Signature, StringComparer.Ordinal)
                .ToArray();

            double diversity = aliveThreads == 0 ? 0 : clusters.Count * 100.0 / aliveThreads;
            int singletonSignatures = topClusters.Count(c => c.Count == 1);
            // Complete ranked signature/cluster lists — no report-width cap here (§9.24 D5); the
            // render layer slices for display.
            var topSignatures = topClusters.Select(c => c.Signature).ToArray();

            var filteredClusters = topClusters.Where(c => c.Count >= Math.Max(1, options.MinClusterSize)).ToArray();

            var topClusterSnapshots = filteredClusters
                .Select(c => new ThreadClusterSnapshot(
                    c.Count,
                    ProjectSampleOsThreadIds(c.SampleThreadAddresses, osThreadIdByAddress),
                    c.Signature,
                    c.ThreadpoolWorkerCount,
                    c.GcCount,
                    c.FinalizerCount,
                    c.SampleManagedThreadIds,
                    ClassifyFrameworkPattern(c.Signature)))
                .ToArray();

            IReadOnlyList<ThreadClusterTreeNode> clusterTreeRoots = BuildClusterTree(filteredClusters);

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
                        }).ToArray();

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

            return new ThreadStackClusterDomainResult(aliveThreads, clusters.Count, singletonSignatures, diversity, topSignatures, topClusterSnapshots, rawExports, topFrameHotspots, clusterTreeRoots);
        }

        // P3-2: builds a shared-prefix trie over cluster signatures, innermost frame first (index 0
        // of BuildSignature's " | "-joined parts is the currently-executing frame), so branches
        // converge on threads' shared blocking point even when reached via different call sites —
        // information the flat per-cluster signature list can't surface on its own. See
        // docs/refactor/collapsible-tree-widget-design.md.
        internal static IReadOnlyList<ThreadClusterTreeNode> BuildClusterTree(IReadOnlyList<StackCluster> filteredClusters)
        {
            if (filteredClusters.Count == 0)
                return Array.Empty<ThreadClusterTreeNode>();

            var root = new TrieBuildNode();
            foreach (StackCluster cluster in filteredClusters)
            {
                string[] frames = cluster.Signature.Split(" | ", StringSplitOptions.None);
                TrieBuildNode node = root;
                foreach (string frame in frames)
                {
                    node = GetOrAddChild(node, frame);
                    node.Count += cluster.Count;
                }
                node.OwnLeafCount += cluster.Count;
            }

            int nodeBudget = MaxTreeNodes;
            var roots = new List<ThreadClusterTreeNode>(root.Children.Count);
            foreach (KeyValuePair<string, TrieBuildNode> child in OrderChildrenByCountDescending(root.Children))
            {
                if (nodeBudget <= 0)
                    break;
                roots.Add(ConvertTrieNode(child.Key, child.Value, ref nodeBudget, depth: 0));
            }
            return roots;
        }

        private static TrieBuildNode GetOrAddChild(TrieBuildNode node, string frame)
        {
            if (!node.Children.TryGetValue(frame, out TrieBuildNode? child))
            {
                child = new TrieBuildNode();
                node.Children[frame] = child;
            }
            return child;
        }

        private static List<KeyValuePair<string, TrieBuildNode>> OrderChildrenByCountDescending(Dictionary<string, TrieBuildNode> children)
        {
            var ordered = new List<KeyValuePair<string, TrieBuildNode>>(children);
            ordered.Sort((a, b) =>
            {
                int byCount = b.Value.Count.CompareTo(a.Value.Count);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });
            return ordered;
        }

        private static ThreadClusterTreeNode ConvertTrieNode(string frameLabel, TrieBuildNode node, ref int nodeBudget, int depth)
        {
            nodeBudget--;

            // Collapse straight-line runs of single-child ancestors (no cluster terminates along
            // the way) into one chain node instead of one node per frame — real stacks are commonly
            // 50+ frames deep and most of that depth is unbranched.
            bool isChain = false;
            while (node.OwnLeafCount == 0 && node.Children.Count == 1)
            {
                isChain = true;
                KeyValuePair<string, TrieBuildNode> only = node.Children.First();
                frameLabel = frameLabel + " → " + only.Key;
                node = only.Value;
            }

            List<KeyValuePair<string, TrieBuildNode>> orderedChildren = OrderChildrenByCountDescending(node.Children);

            if (depth >= MaxTreeDepth)
                return new ThreadClusterTreeNode(frameLabel, node.Count, Array.Empty<ThreadClusterTreeNode>(), isChain, orderedChildren.Count);

            var children = new List<ThreadClusterTreeNode>(Math.Min(orderedChildren.Count, MaxTreeChildrenPerNode));
            int truncatedChildCount = 0;
            for (int i = 0; i < orderedChildren.Count; i++)
            {
                if (children.Count < MaxTreeChildrenPerNode && nodeBudget > 0)
                    children.Add(ConvertTrieNode(orderedChildren[i].Key, orderedChildren[i].Value, ref nodeBudget, depth + 1));
                else
                    truncatedChildCount++;
            }

            return new ThreadClusterTreeNode(frameLabel, node.Count, children, isChain, truncatedChildCount);
        }

        private sealed class TrieBuildNode
        {
            public int Count;
            public int OwnLeafCount;
            public Dictionary<string, TrieBuildNode> Children { get; } = new(StringComparer.Ordinal);
        }

        // P2-4: the most frequently occurring individual frames across the entire alive-thread
        // population (not per-cluster) — surfaces hot call sites shared by threads that otherwise
        // cluster into different signatures (e.g. same lock-acquire frame reached via different
        // call paths), which per-cluster signatures alone can't reveal.
        private static IReadOnlyList<NameCountEntry> BuildTopFrameHotspots(Dictionary<string, int> frameHistogram)
        {
            if (frameHistogram.Count == 0)
                return Array.Empty<NameCountEntry>();

            var top = new List<KeyValuePair<string, int>>(frameHistogram);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));

            int limit = Math.Min(top.Count, TopFrameHotspotsToReport);
            var result = new List<NameCountEntry>(limit);
            for (int i = 0; i < limit; i++)
                result.Add(new NameCountEntry(top[i].Key, top[i].Value));

            return result;
        }

        // P3-1: recognizes well-known framework wait/idle signatures so findings can tell coordinated
        // application blocking apart from expected framework noise (idle pool workers, GC, etc.).
        internal static string? ClassifyFrameworkPattern(string signature) =>
            ThreadWaitClassifier.ClassifySignature(signature, FrameworkPatterns)?.Category;

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

        private static void AccumulateCluster(Dictionary<string, StackCluster> clusters, string signature, ClrThread thread)
        {
            if (!clusters.TryGetValue(signature, out StackCluster? cluster))
            {
                cluster = new StackCluster(signature);
                clusters[signature] = cluster;
            }

            cluster.Count++;
            if (thread.State.HasFlag(ClrThreadState.TS_TPWorkerThread))
                cluster.ThreadpoolWorkerCount++;
            if (thread.IsGc)
                cluster.GcCount++;
            if (thread.IsFinalizer)
                cluster.FinalizerCount++;

            // Every thread's address is recorded — the display-width cap on how many sample IDs
            // to show per cluster is a render-layer concern (§9.24 D5), not an accumulation cap.
            if (thread.Address != 0)
            {
                cluster.SampleThreadAddresses.Add(thread.Address);
                cluster.SampleManagedThreadIds.Add(thread.ManagedThreadId);
            }
        }

        // Cluster identity is the thread's whole captured stack — no artificial frame-count cap
        // (§9.24). A signature match now means two threads share their entire call stack, not just
        // its top N frames, so distinct threads whose stacks diverge below frame N are no longer
        // merged into the same cluster.
        private static string BuildSignature(IEnumerable<ClrStackFrame> frames, ClrThread? thread = null, Dictionary<string, int>? frameHistogram = null)
        {
            var parts = new List<string>();

            foreach (ClrStackFrame frame in frames)
            {
                string? name = frame.Method?.Signature;
                if (string.IsNullOrWhiteSpace(name))
                    name = frame.FrameName;

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string trimmed = name.Trim();
                parts.Add(trimmed);

                if (frameHistogram != null)
                {
                    frameHistogram.TryGetValue(trimmed, out int existing);
                    frameHistogram[trimmed] = existing + 1;
                }
            }

            if (parts.Count == 0)
            {
                if (thread != null)
                {
                    if (thread.IsGc)
                        return "<No managed frames> (GC)";
                    if (thread.IsFinalizer)
                        return "<No managed frames> (Finalizer)";
                    if (thread.State.HasFlag(ClrThreadState.TS_CompletionPortThread))
                        return "<No managed frames> (IOCP)";
                    if (thread.State.HasFlag(ClrThreadState.TS_TPWorkerThread))
                        return "<No managed frames> (Threadpool)";
                }
                return "<No managed frames>";
            }

            return string.Join(" | ", parts);
        }

        internal sealed class StackCluster
        {
            public string Signature { get; }
            public int Count { get; set; }
            public int ThreadpoolWorkerCount { get; set; }
            public int GcCount { get; set; }
            public int FinalizerCount { get; set; }
            public List<ulong> SampleThreadAddresses { get; } = new();
            public List<int> SampleManagedThreadIds { get; } = new();

            public StackCluster(string signature)
            {
                Signature = signature;
            }
        }

        public void Dispose() { }
    }
}


