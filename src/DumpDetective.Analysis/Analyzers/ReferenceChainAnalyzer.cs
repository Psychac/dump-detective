using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    public class ReferenceChainAnalyzer : IAnalyzer
    {
        private readonly record struct ObjectMetadata(bool IsValid, string? TypeName, ulong Size);

        public string Name => "Reference Chain Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReferenceChainOptions options = context.AnalysisOptions.ReferenceChain;
            ExecutionPolicy policy = context.AnalysisOptions.ExecutionPolicy;

            return ValueTask.FromResult(AnalyzeTopTypes(context.Heap, context.Cache, options, policy, context.Progress, cancellationToken).Stamp(this));
        }

        internal AnalyzerDomainResult AnalyzeTopTypes(ClrHeap heap, IHeapAnalysisCache cache, ReferenceChainOptions options)
        {
            return AnalyzeTopTypes(heap, cache, options, ExecutionPolicy.Default, progress: null, CancellationToken.None);
        }

        private AnalyzerDomainResult AnalyzeTopTypes(ClrHeap heap, IHeapAnalysisCache cache, ReferenceChainOptions options, ExecutionPolicy policy, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            int topCount = options.TopCount > 0 ? options.TopCount : options.FallbackTopCount;
            bool skipArrays = options.SkipArrays;
            int largeFanoutThreshold = options.LargeFanoutThreshold > 0 ? options.LargeFanoutThreshold : 100;
            var knownLeakPatterns = options.KnownLeakTypePatterns ?? Array.Empty<string>();

            // Use cached type statistics instead of re-enumerating
            progress?.Report(new(0, "building type index"));
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            var topTypes = typeStats
                .OrderByDescending(kvp => kvp.Value.TotalSize)
                .Take(topCount)
                .ToArray();

            int retainedSamples = 0;
            int analyzedSamples = 0;
            int traversalLimitedSamples = 0;
            var retainedTypeNames = new HashSet<string>(StringComparer.Ordinal);
            var sampleReferenceChains = new List<string>(capacity: 5);
            var topTypeSampleTraces = new List<ReferenceTypeSampleSnapshot>(capacity: topTypes.Length);
            progress?.Report(new(0, "loading root list"));
            IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
            // Sort retaining roots by likelihood of early hit (Stack first) and drop weak/dependent roots
            // that can never prevent GC collection. Sorted once, reused for all top-N type samples.
            List<(string RootKind, ulong Address)> prioritizedRoots = SortAndFilterRoots(roots);

            var telemetry = new TelemetryCounters();
            int typeIndex = 0;

            // Create ReferenceGraph once, shared across all top-N type iterations.
            // This preserves the edge cache across iterations, reducing redundant ClrMD calls
            // for objects referenced by multiple types.
            var provider = new ReferenceGraph(heap);

            foreach (var typeKvp in topTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                typeIndex++;
                progress?.Report(new(analyzedSamples, "tracing reference chains", $"{typeIndex}/{topTypes.Length} types"));
                string typeName = typeKvp.Key;
                var stats = typeKvp.Value;

                // Use cached sample instance instead of full heap walk
                ulong? sampleAddress = cache.GetSampleInstanceAddress(typeName);
                string? sampleType = null;
                ulong sampleSize = 0;
                bool hasGcRoot = false;
                string? rootKind = null;
                string? path = null;
                IReadOnlyList<string>? pathHops = null;
                bool searchTruncated = false;

                if (sampleAddress.HasValue)
                {
                    ObjectMetadata sampleMetadata = GetObjectMetadata(heap, sampleAddress.Value);
                    if (sampleMetadata.IsValid)
                    {
                        analyzedSamples++;
                        sampleType = sampleMetadata.TypeName ?? StringConstants.UnknownType;
                        sampleSize = sampleMetadata.Size;

                        hasGcRoot = TryFindAnyRootPath(heap, provider, prioritizedRoots, sampleAddress.Value, options, policy, telemetry, cache.TryGetReverseIndexProvider(), cancellationToken, out rootKind, out path, out pathHops, out searchTruncated);
                        if (hasGcRoot)
                        {
                            retainedSamples++;
                            retainedTypeNames.Add(typeName);

                            if (!string.IsNullOrWhiteSpace(path) && sampleReferenceChains.Count < 5)
                                sampleReferenceChains.Add($"{typeName}: {path}");
                        }
                        else if (searchTruncated)
                        {
                            traversalLimitedSamples++;
                        }
                    }
                }

                topTypeSampleTraces.Add(new ReferenceTypeSampleSnapshot(
                    typeName,
                    stats.Count,
                    stats.TotalSize,
                    sampleAddress,
                    sampleType,
                    sampleSize,
                    hasGcRoot,
                    rootKind,
                    path,
                    pathHops,
                    searchTruncated));
            }

            double retainedPct = analyzedSamples == 0 ? 0 : retainedSamples * 100.0 / analyzedSamples;
            var retainedTypeList = retainedTypeNames
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            return new ReferenceChainDomainResult(
                analyzedSamples,
                retainedSamples,
                retainedPct,
                retainedTypeList,
                sampleReferenceChains,
                topTypeSampleTraces,
                traversalLimitedSamples);
        }

        internal bool AnalyzeObject(ClrHeap heap, IHeapAnalysisCache cache, ulong objectAddress)
        {
            IReadOnlyList<(string RootKind, ulong Address)> roots = cache.GetOrBuildValidRoots(heap);
            List<(string RootKind, ulong Address)> prioritizedRoots = SortAndFilterRoots(roots);
            var provider = new ReferenceGraph(heap);
            var options = new ReferenceChainOptions();
            var telemetry = new TelemetryCounters();
            return TryFindAnyRootPath(heap, provider, prioritizedRoots, objectAddress, options, ExecutionPolicy.Default, telemetry, cache.TryGetReverseIndexProvider(), CancellationToken.None, out _, out _, out _, out _);
        }

        private bool TryFindAnyRootPath(
            ClrHeap heap,
            ReferenceGraph provider,
            IReadOnlyList<(string RootKind, ulong Address)> roots,
            ulong objectAddress,
            ReferenceChainOptions options,
            ExecutionPolicy policy,
            TelemetryCounters telemetry,
            IBackwardReferenceProvider? reverseIndexProvider,
            CancellationToken cancellationToken,
            out string? rootKind,
            out string? path,
            out IReadOnlyList<string>? pathHops,
            out bool searchTruncated)
        {
            rootKind = null;
            path = null;
            pathHops = null;
            searchTruncated = false;

            if (!TryGetValidObject(heap, objectAddress, out _))
                return false;

            // All modes route through the bounded bidirectional search — Fast mode differs only
            // in its (smaller) resolved candidate-set/depth limits, set via ReferenceChainOptions.
            // A separate unbounded per-root BFS used to back Fast mode; removed because it scaled
            // with GC root count instead of a shared bounded budget (see
            // docs/analysis/root-path-search-blast-radius.md).
            return TryFindAnyRootPath_Bidirectional(heap, provider, roots, objectAddress, options, policy, telemetry, reverseIndexProvider, cancellationToken, out rootKind, out path, out pathHops, out searchTruncated);
        }

        // ── Bidirectional bounded search (all modes) ────────────────────────────
        private bool TryFindAnyRootPath_Bidirectional(
            ClrHeap heap,
            ReferenceGraph provider,
            IReadOnlyList<(string RootKind, ulong Address)> roots,
            ulong objectAddress,
            ReferenceChainOptions options,
            ExecutionPolicy policy,
            TelemetryCounters telemetry,
            IBackwardReferenceProvider? reverseIndexProvider,
            CancellationToken cancellationToken,
            out string? rootKind,
            out string? path,
            out IReadOnlyList<string>? pathHops,
            out bool searchTruncated)
        {
            rootKind = null;
            path = null;
            pathHops = null;
            searchTruncated = false;

            // Use shared ReferenceGraph as the reference provider — it caches edges across
            // all types, reducing redundant ClrMD calls for objects referenced by multiple types.
            var limits = new RootPathSearchLimits
            {
                MaxCandidateNodes = options.ResolvedMaxCandidateNodes,
                MaxCandidateDepth = options.ResolvedMaxCandidateDepth,
                MaxRootExpansionDepth = options.ResolvedMaxRootExpansionDepth,
                LargeFanoutThreshold = options.LargeFanoutThreshold,
            };

            var finder = new RootPathFinder(
                heap,
                provider,
                limits,
                telemetry.AsProxy(),
                type => IsNoisyType(type, options.SkipArrays),
                type => IsKnownLeakType(type, options.KnownLeakTypePatterns),
                reverseIndexProvider);

            bool found = finder.TryFindAnyRootPath(
                objectAddress,
                roots,
                out string? foundRootKind,
                out List<ulong>? addresses,
                out searchTruncated,
                out int candidateSetSize,
                out int reverseIndexEntryCount,
                cancellationToken);

            telemetry.TotalCandidateSetSize += candidateSetSize;
            telemetry.ReverseIndexEntries += reverseIndexEntryCount;

            if (found)
            {
                rootKind = foundRootKind;
                path = FormatPath(heap, foundRootKind!, addresses, out pathHops);
                return true;
            }

            return false;
        }

        private static string FormatPath(ClrHeap heap, string rootKind, IReadOnlyList<ulong>? addresses, out IReadOnlyList<string>? pathHops)
        {
            pathHops = null;

            if (addresses is null || addresses.Count == 0)
                return $"{rootKind}: <no path>";

            var parts = new List<string>(addresses.Count);
            for (int i = 0; i < addresses.Count; i++)
            {
                parts.Add(FormatNodeByAddress(heap, addresses[i]));
            }
            pathHops = parts;
            string chain = string.Join(" -> ", parts);
            return $"{rootKind}: {chain}";
        }

        private static ObjectMetadata GetObjectMetadata(ClrHeap heap, ulong address)
        {
            if (!TryGetValidObject(heap, address, out ClrObject obj))
                return new ObjectMetadata(false, null, 0);

            return new ObjectMetadata(true, obj.Type?.Name, obj.Size);
        }

        internal static bool IsNoisyType(ClrType? type, bool skipArrays)
        {
            if (type is null)
                return false;

            string? name = type.Name;
            if (string.IsNullOrEmpty(name))
                return false;

            // Skip System.String and System.Object
            if (name == "System.String" || name == "System.Object")
                return true;

            // Optionally skip arrays
            if (skipArrays && type.IsArray)
                return true;

            return false;
        }

        internal static bool IsKnownLeakType(ClrType? type, IReadOnlyList<string> knownLeakPatterns)
        {
            if (type is null)
                return false;

            string? name = type.Name;
            if (string.IsNullOrEmpty(name))
                return false;

            foreach (var pattern in knownLeakPatterns)
            {
                if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string FormatNodeByAddress(ClrHeap heap, ulong address)
        {
            ObjectMetadata metadata = GetObjectMetadata(heap, address);
            string typeName = metadata.IsValid ? (metadata.TypeName ?? StringConstants.UnknownType) : "<invalid>";
            return $"{typeName}@0x{address:X}";
        }

        // Simple telemetry counters used during a single AnalyzeTopTypes invocation
        internal sealed class TelemetryCounters
        {
            public long PrunedNodes { get; set; }
            public long LargeFanoutNodesSkipped { get; set; }
            public long TotalCandidateSetSize { get; set; }
            public long ReverseIndexEntries { get; set; }

            // Returns a lightweight proxy that the helper classes (outside this class) can write through.
            public TelemetryProxy AsProxy() => new(this);
        }

        /// <summary>
        /// Lightweight ref-struct-like proxy so nested helper classes (declared outside
        /// <see cref="ReferenceChainAnalyzer"/>) can update telemetry without exposing the full counter object.
        /// </summary>
        internal sealed class TelemetryProxy(TelemetryCounters inner) : IPathSearchTelemetry
        {
            public void IncrementPruned() => inner.PrunedNodes++;
            public void IncrementLargeFanout() => inner.LargeFanoutNodesSkipped++;
        }

        private static List<(string RootKind, ulong Address)> SortAndFilterRoots(
            IReadOnlyList<(string RootKind, ulong Address)> roots)
        {
            var result = new List<(string RootKind, ulong Address)>(roots.Count);
            foreach ((string rootKind, ulong address) in roots)
            {
                // Weak and dependent roots never prevent collection — skip them entirely.
                if (rootKind.Contains("Weak", StringComparison.OrdinalIgnoreCase) ||
                    rootKind.Contains("Dependent", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add((rootKind, address));
            }

            result.Sort(static (a, b) => GetRootSearchPriority(a.RootKind).CompareTo(GetRootSearchPriority(b.RootKind)));
            return result;
        }

        private static int GetRootSearchPriority(string rootKind) => rootKind switch
        {
            // Stack locals are the most direct retainers and resolve fastest.
            "Stack" => 0,
            // Thread-static and static variables are the next most likely long-lived retainers.
            "ThreadStaticVar" => 1,
            "StaticVar" => 2,
            // Strong and pinned GC handles are explicit long-term roots.
            "Strong" => 3,
            "Pinned" => 4,
            "AsyncPinnedHandle" => 4,
            "RefCountedHandle" => 5,
            // Finalizer roots are already reported by FinalizableObjectAnalyzer — deprioritize.
            "Finalizer" => 10,
            // Unknown kinds go last.
            _ => 6
        };

        private static bool TryGetValidObject(ClrHeap heap, ulong address, out ClrObject obj)
        {
            obj = default;

            if (address == 0)
                return false;

            obj = heap.GetObject(address);
            return obj.IsValid;
        }
        public void Dispose() { }

    }

}
