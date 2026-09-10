using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase 1 retyping batch, thread-domain quartet item 2
    /// (docs/refactor/modularity/phase-1-thread-quartet-plan.md): retyped onto SDK's
    /// capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing threads/stack frames through
    /// <see cref="IRuntimeThreadQuery"/> (<c>runtime.threads</c>). Runs through the existing pipeline
    /// via <see cref="ThreadStackClusterAnalyzerLegacyAdapter"/>, which also carries the
    /// <c>IThreadStackScanParticipant</c> implementation — this analyzer's stack-frame reads rely on
    /// the pipeline sharing a single walk across the whole thread-domain quartet; see the adapter's
    /// own remarks and the plan doc's § 3 for why that lives there, not here.
    /// </summary>
    /// <remarks>
    /// Re-verified during this retyping (not just grepped, per this project's own convention): this
    /// analyzer reads no live object field values anywhere — cluster signatures are built purely from
    /// frame method signatures/frame names and thread state flags, all coarse/structural.
    /// </remarks>
    public sealed class ThreadStackClusterAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
    {
        public string Name => "Thread Stack Signature Clustering";
        public string Category => "Threads";

        public AnalyzerDomainResult? LastResult { get; private set; }

        // P2-4: how many entries the frame-level hotspot histogram surfaces in the domain result.
        private const int TopFrameHotspotsToReport = 10;

        // P3-2: shared-prefix cluster tree render-width limits — the trie itself is built from the
        // complete filtered-cluster set, only the bound nodes it reports render.
        private const int MaxTreeChildrenPerNode = 8;
        private const int MaxTreeNodes = 400;

        // Safety bound (not a display truncation) on rendered TreeNode nesting depth, for the same
        // reasoning as RetentionOptions.MaxDominatorChainDepth: a real call stack's depth is
        // bounded only by the deepest recursion in the target app (e.g. an unbounded/stack-overflow
        // recursive call), and each branch point in the trie adds one nested TreeNode.Children
        // level in the JSON report — an unbounded depth previously tripped
        // System.Text.Json's MaxDepth guard ("possible object cycle detected") on such stacks.
        private const int MaxTreeDepth = 64;

        // P3-1: well-known wait/idle signatures, pipe-joined into a single Contains scan per frame.
        // Ordered so the first, most specific application-level match wins.
        private static readonly WaitPattern[] FrameworkPatterns =
        [
            new("Threadpool-idle", "ThreadPoolWorkQueue", "CLR thread pool worker waiting for work"),
            new("Threadpool-idle", "PortableThreadPool", "CLR thread pool worker waiting for work"),
            new("GC", "<No managed frames> (GC)", "Garbage collector thread"),
            new("Finalizer", "<No managed frames> (Finalizer)", "Finalizer thread waiting on finalization queue"),
            new("IOCP-idle", "<No managed frames> (IOCP)", "I/O completion port thread waiting for completions"),
            new("Threadpool-idle", "<No managed frames> (Threadpool)", "CLR thread pool worker waiting for work"),
        ];

        public ValueTask AnalyzeAsync(Sdk.Analysis.AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IRuntimeThreadQuery threadQuery = context.RuntimeThreads
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeThreads}' capability.");

            ThreadStackClusterAnalysisOptions options = context.AnalyzerOptions as ThreadStackClusterAnalysisOptions ?? new ThreadStackClusterAnalysisOptions();

            LastResult = Analyze(threadQuery, options, cancellationToken);
            return ValueTask.CompletedTask;
        }

        private static ThreadStackClusterDomainResult Analyze(
            IRuntimeThreadQuery threadQuery,
            ThreadStackClusterAnalysisOptions options,
            CancellationToken cancellationToken)
        {
            var clusters = new Dictionary<string, StackCluster>(StringComparer.Ordinal);
            var frameHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            int aliveThreads = 0;

            foreach (RuntimeThreadRef thread in threadQuery.EnumerateThreads())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!thread.IsAlive)
                    continue;

                aliveThreads++;
                string signature = BuildSignature(threadQuery.EnumerateStackFrames(thread), thread, frameHistogram);
                AccumulateCluster(clusters, signature, thread);
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
                    c.SampleOsThreadIds,
                    c.Signature,
                    c.ThreadpoolWorkerCount,
                    c.GcCount,
                    c.FinalizerCount,
                    c.SampleManagedThreadIds,
                    ClassifyFrameworkPattern(c.Signature)))
                .ToArray();

            IReadOnlyList<ThreadClusterTreeNode> clusterTreeRoots = BuildClusterTree(filteredClusters);

            return new ThreadStackClusterDomainResult(aliveThreads, clusters.Count, singletonSignatures, diversity, topSignatures, topClusterSnapshots, null, topFrameHotspots, clusterTreeRoots);
        }

        // P3-2: builds a shared-prefix trie over cluster signatures, innermost frame first (index 0
        // of BuildSignature's " | "-joined parts is the currently-executing frame), so branches
        // converge on threads' shared blocking point even when reached via different call sites —
        // information a flat per-cluster signature list can't surface on its own. See
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
                    node.Count += cluster.Count;
                    node = GetOrAddChild(node, frame);
                }
                node.Count += cluster.Count;
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

            // Collapse straight-line runs of single-child ancestors (no cluster terminates along the
            // way) into one chain node instead of one node per frame — real stacks commonly run
            // 50+ frames deep with most of that depth unbranched.
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

        // P2-4: alive-thread-weighted frame-level hotspot histogram (every resolvable frame counted
        // once per cluster member, not once per-cluster) — surfaces frames that dominate across many
        // distinct call paths (e.g. a lock-acquire helper reached from several callers) that a
        // per-cluster signature view alone can't.
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

        internal static void AccumulateCluster(Dictionary<string, StackCluster> clusters, string signature, RuntimeThreadRef thread)
        {
            if (!clusters.TryGetValue(signature, out StackCluster? cluster))
            {
                cluster = new StackCluster(signature);
                clusters[signature] = cluster;
            }

            cluster.Count++;
            if (thread.IsThreadpoolWorker)
                cluster.ThreadpoolWorkerCount++;
            if (thread.IsGc)
                cluster.GcCount++;
            if (thread.IsFinalizer)
                cluster.FinalizerCount++;

            // Every thread's ID is recorded — the display-width cap on how many sample IDs to show
            // per cluster is a render-layer concern (§9.24 D5), not an accumulation cap.
            cluster.SampleOsThreadIds.Add(thread.Thread.OsThreadId);
            cluster.SampleManagedThreadIds.Add(thread.Thread.ManagedThreadId ?? 0);
        }

        // Cluster identity is a thread's whole captured stack — no artificial frame-count cap
        // (§9.24). A signature match now means two threads share their entire call stack, not just
        // the top N frames, so distinct threads whose stacks diverge below frame N are no longer
        // merged into the same cluster.
        internal static string BuildSignature(IEnumerable<ThreadStackFrameRef> frames, RuntimeThreadRef? thread = null, Dictionary<string, int>? frameHistogram = null)
        {
            var parts = new List<string>();
            foreach (ThreadStackFrameRef frame in frames)
            {
                string? name = frame.RawMethodSignature;
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
                if (thread is { } t)
                {
                    if (t.IsGc)
                        return "<No managed frames> (GC)";
                    if (t.IsFinalizer)
                        return "<No managed frames> (Finalizer)";
                    if (t.IsCompletionPortThread)
                        return "<No managed frames> (IOCP)";
                    if (t.IsThreadpoolWorker)
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
            public List<uint> SampleOsThreadIds { get; } = new();
            public List<int> SampleManagedThreadIds { get; } = new();

            public StackCluster(string signature)
            {
                Signature = signature;
            }
        }

        public void Dispose() { }
    }
}
