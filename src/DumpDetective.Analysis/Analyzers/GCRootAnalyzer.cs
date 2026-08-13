using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase-2 analyzer: unified GC root intelligence covering all root kinds
    /// (Stack, Static/StrongHandle, Finalizer, Pinned, etc.), retention estimates,
    /// and bounded BFS path tracing for top suspects.
    ///
    /// Root data is sourced from <see cref="RootSetCache"/>: the Phase-1 disk index
    /// (<c>RootIndex.bin</c>) when available, falling back to a live
    /// <c>heap.EnumerateRoots()</c> walk otherwise.
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

            cancellationToken.ThrowIfCancellationRequested();

            // ── Step 1: Read all roots via the shared root-set cache ───────────
            IReadOnlyList<RootRecord> rootRecords = heapCache.GetOrBuildRoots(heap, cancellationToken);
            if (rootRecords.Count == 0)
                return EmptyResult();

            var roots = new List<(ulong TargetAddr, ulong RootAddr, byte Kind)>(rootRecords.Count);
            foreach (RootRecord record in rootRecords)
                roots.Add((record.TargetAddr, record.RootAddr, record.Kind));

            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates = idx.TypeAggregates;

            // ── Step 2: Group by kind, estimate retained bytes, score severity ─
            GCRootAnalysisProjectionResult projection = GCRootAnalysisProjection.Build(roots, heap, cache, aggregates);

            List<RootFinding> findings = projection.FindingsBySeverityDescending;
            int topCount = Math.Min(findings.Count, options.TopSeverityLimit);

            // Mechanism B (see docs/analysis/root-field-name-index-plan.md): owning-method
            // attribution for Stack-kind roots, scoped to the severity-ranked top-N only — the
            // per-thread frame walk this triggers on first use is too costly to run for every
            // Stack root in the dump, and the report never shows more than TopSeverityLimit rows.
            for (int i = 0; i < topCount; i++)
            {
                RootFinding f = findings[i];
                if (f.RootKind == "Stack" && cache.TryResolveStackFrameOwner(heap, f.RootAddress, out string ownerType, out string methodName))
                    findings[i] = f with { FieldDescription = $"in {ownerType}.{methodName}()" };
            }

            IReadOnlyList<RootFinding> topFindings = findings.Count <= options.TopSeverityLimit
                ? findings
                : findings.GetRange(0, topCount);

            // ── Step 3: BFS path tracing + retained-size estimate for top-N roots ──
            int pathCappedCount = 0;
            int pathN = Math.Min(findings.Count, options.PathSearchTopN);

            var walkCandidates = new List<(ulong Address, ulong MethodTable, ulong ShallowSize)>(pathN);
            for (int i = 0; i < pathN; i++)
            {
                RootFinding f = findings[i];
                if (cache.TryGetObjectMetadata(heap, f.TargetAddress, out ulong methodTable, out ulong size))
                    walkCandidates.Add((f.TargetAddress, methodTable, size));
            }

            var retainedVisited = new HashSet<ulong>(capacity: Math.Min(pathN * 64, 4096));
            IReadOnlyList<RetainedSizeResult> retainedResults = RetainedSizeCandidateSelector.SelectAndCompute(
                walkCandidates, heap, cache, retainedVisited, maxCandidatesToWalk: pathN, options.MaxBfsNodes, options.MaxBfsDepth, cancellationToken);

            var retainedByAddress = new Dictionary<ulong, RetainedSizeResult>(retainedResults.Count);
            foreach (RetainedSizeResult r in retainedResults)
                retainedByAddress[r.Address] = r;

            var pathFindings = new List<RootPathFinding>(pathN);

            for (int i = 0; i < pathN; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RootFinding f = findings[i];

                var pathTypes = BoundedGraphWalk.CollectForwardTypeNames(heap, f.TargetAddress, options.MaxBfsNodes, options.MaxBfsDepth, out bool wasCapped, cancellationToken);

                if (wasCapped)
                    pathCappedCount++;

                retainedByAddress.TryGetValue(f.TargetAddress, out RetainedSizeResult retained);

                pathFindings.Add(new RootPathFinding(
                    TargetAddress: f.TargetAddress,
                    TargetTypeName: f.TargetTypeName,
                    RootKind: f.RootKind,
                    PathTypeNames: pathTypes,
                    PathLength: pathTypes.Count,
                    WasCapped: wasCapped,
                    EstimatedRetainedBytes: retained.RetainedSize,
                    RetainedSizeWasWalked: retained.WasWalked));
            }

            return new GCRootDomainResult(
                TotalRoots: roots.Count,
                ByKind: projection.ByKind,
                TopRootsBySeverity: topFindings,
                RootPaths: pathFindings,
                PathSearchCapped: pathCappedCount > 0,
                PathSearchCappedCount: pathCappedCount);
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
