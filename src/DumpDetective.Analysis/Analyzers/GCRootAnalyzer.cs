using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase-2 analyzer: unified GC root intelligence covering all root kinds
    /// (Stack, Static/StrongHandle, Finalizer, Pinned, etc.), retention estimates,
    /// and bounded BFS path tracing for top suspects.
    ///
    /// Root data is sourced entirely from Phase 1:
    /// Memory mode — <see cref="HeapIndexBuildResult.InMemoryRootCandidates"/>
    /// Disk mode — reads <c>RootIndex.bin</c> from the dump index directory
    ///
    /// No direct <c>heap.EnumerateRoots()</c> call is made in Phase 2.
    /// </summary>
    public sealed class GCRootAnalyzer : IAnalyzer
    {
        public string Name => "GC Root Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GCRootAnalysisOptions options = context.AnalysisOptions.GCRootAnalysis;
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrHeap heap,
            IHeapAnalysisCache cache,
            GCRootAnalysisOptions options,
            CancellationToken cancellationToken)
        {
            if (cache is not HeapAnalysisCache heapCache
                || !heapCache.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            {
                return EmptyResult();
            }

            // ── Step 1: Read all roots from the Phase 1 index ──────────────────
            var roots = ReadRoots(idx, cancellationToken);
            if (roots.Count == 0)
                return EmptyResult();

            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates = idx.TypeAggregates;

            // ── Step 2: Group by kind, estimate retained bytes, score severity ─
            GCRootAnalysisProjectionResult projection = GCRootAnalysisProjection.Build(roots, heap, aggregates);

            List<RootFinding> findings = projection.FindingsBySeverityDescending;
            int topCount = Math.Min(findings.Count, options.TopSeverityLimit);
            IReadOnlyList<RootFinding> topFindings = findings.Count <= options.TopSeverityLimit
                ? findings
                : findings.GetRange(0, topCount);

            // ── Step 3: BFS path tracing for top-N roots ────────────────────────
            int pathCappedCount = 0;
            int pathN = Math.Min(findings.Count, options.PathSearchTopN);
            var pathFindings = new List<RootPathFinding>(pathN);

            for (int i = 0; i < pathN; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RootFinding f = findings[i];

                var pathTypes = HeapTypePathTraversal.CollectForwardTypeNames(heap, f.TargetAddress, options.MaxBfsNodes, options.MaxBfsDepth, out bool wasCapped);

                if (wasCapped)
                    pathCappedCount++;

                pathFindings.Add(new RootPathFinding(
                    TargetAddress: f.TargetAddress,
                    TargetTypeName: f.TargetTypeName,
                    RootKind: f.RootKind,
                    PathTypeNames: pathTypes,
                    PathLength: pathTypes.Count,
                    WasCapped: wasCapped));
            }

            return new GCRootDomainResult(
                TotalRoots: roots.Count,
                ByKind: projection.ByKind,
                TopRootsBySeverity: topFindings,
                RootPaths: pathFindings,
                PathSearchCapped: pathCappedCount > 0,
                PathSearchCappedCount: pathCappedCount);
        }

        // ── Root reading ─────────────────────────────────────────────────────

        private static List<(ulong TargetAddr, ulong RootAddr, byte Kind)> ReadRoots(
            HeapIndexBuildResult idx,
            CancellationToken cancellationToken)
        {
            return RootIndexReader.ReadRootCandidates(idx, cancellationToken);
        }

        private static GCRootDomainResult EmptyResult() =>
            new(TotalRoots: 0,
                ByKind: Array.Empty<RootKindSummary>(),
                TopRootsBySeverity: Array.Empty<RootFinding>(),
                RootPaths: Array.Empty<RootPathFinding>(),
                PathSearchCapped: false,
                PathSearchCappedCount: 0);
    }
}
