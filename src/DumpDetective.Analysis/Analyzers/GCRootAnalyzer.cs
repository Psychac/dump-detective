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
    public sealed class GCRootAnalyzer : IAnalyzer, IRequiresReachableGraphIndex, IRequiresDominatorTreeIndex
    {
        // Internal traversal bounds, not user-configurable (matches BoundedGraphWalk.AbsoluteMaxDepth's
        // precedent) — used only for the forward path-type-name walk and the retained-size fallback walk,
        // both of which still run when the exact dominator tree can't answer a query (see docs/refactor/
        // analysis-profile-removal-plan.md §9.16 implementation notes for why these two walks remain).
        private const int PathWalkMaxNodes = 500;
        private const int PathWalkMaxDepth = 20;

        // Bounds the per-thread stack-frame-owner attribution enrichment (FieldDescription), not the
        // returned finding set itself — that lookup is measured-expensive per root and purely cosmetic
        // (unenriched rows still carry correct kind/type/bytes/severity, just no "in Type.Method()" text).
        private const int StackOwnerAttributionLimit = 20;

        public string Name => "GC Root Analysis";
        public string Category => "Memory";
        public IReadOnlyCollection<string> Tags => ["gc", "roots", "retention"];
        public int Order => 120;

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrHeap heap,
            IHeapAnalysisCache cache,
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

            // §12.1 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): null when
            // Stage B wasn't built for this run — every consumer below degrades to today's
            // shallow-size/BFS-heuristic behavior in that case.
            IDominatorTreeProvider? treeProvider = cache.TryGetDominatorTreeProvider();

            // ── Step 2: Group by kind, estimate retained bytes, score severity ─
            GCRootAnalysisProjectionResult projection = GCRootAnalysisProjection.Build(roots, heap, cache, aggregates, treeProvider);

            List<RootFinding> findings = projection.FindingsBySeverityDescending;

            // Mechanism B (see docs/analysis/root-field-name-index-plan.md): owning-method
            // attribution for Stack-kind roots, scoped to the severity-ranked top-N only — the
            // per-thread frame walk this triggers on first use is too costly to run for every
            // Stack root in the dump, and this enrichment is purely cosmetic (a missing
            // FieldDescription loses no data — kind/type/bytes/severity are unaffected).
            int ownerAttributionCount = Math.Min(findings.Count, StackOwnerAttributionLimit);
            for (int i = 0; i < ownerAttributionCount; i++)
            {
                RootFinding f = findings[i];
                if (f.RootKind == "Stack" && cache.TryResolveStackFrameOwner(heap, f.RootAddress, out string ownerType, out string methodName))
                    findings[i] = f with { FieldDescription = $"in {ownerType}.{methodName}()" };
            }

            IReadOnlyList<RootFinding> topFindings = findings;

            // ── Step 3: BFS path tracing + retained-size estimate for every finding ──
            // §9.16 (docs/refactor/analysis-profile-removal-plan.md): PathSearchTopN deleted —
            // M4 measured this as affordable (568ms uncapped vs. 874ms capped-to-25 on a real
            // dump with 1,404 findings).
            int subgraphWalkCappedCount = 0;
            int pathN = findings.Count;

            // §12.1: a target the dominator tree can answer exactly for needs no BFS at all — skip
            // it as a walk candidate entirely rather than spending one of RetainedSizeCandidateSelector's
            // maxCandidatesToWalk slots on it.
            var exactRetainedByAddress = treeProvider is not null ? new Dictionary<ulong, ulong>(pathN) : null;
            var walkCandidates = new List<(ulong Address, ulong MethodTable, ulong ShallowSize)>(pathN);
            for (int i = 0; i < pathN; i++)
            {
                RootFinding f = findings[i];
                if (exactRetainedByAddress is not null && treeProvider!.TryGetRetainedBytes(f.TargetAddress, out ulong exactRetainedBytes))
                {
                    exactRetainedByAddress[f.TargetAddress] = exactRetainedBytes;
                    continue;
                }

                if (cache.TryGetObjectMetadata(heap, f.TargetAddress, out ulong methodTable, out ulong size))
                    walkCandidates.Add((f.TargetAddress, methodTable, size));
            }

            var retainedVisited = new HashSet<ulong>(capacity: Math.Min(pathN * 64, 4096));
            IReadOnlyList<RetainedSizeResult> retainedResults = RetainedSizeCandidateSelector.SelectAndCompute(
                walkCandidates, heap, cache, retainedVisited, maxCandidatesToWalk: walkCandidates.Count, PathWalkMaxNodes, PathWalkMaxDepth, cancellationToken);

            var retainedByAddress = new Dictionary<ulong, RetainedSizeResult>(retainedResults.Count);
            foreach (RetainedSizeResult r in retainedResults)
                retainedByAddress[r.Address] = r;

            var subgraphFindings = new List<RootOwnedSubgraphFinding>(pathN);

            for (int i = 0; i < pathN; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RootFinding f = findings[i];

                var subgraphTypeNames = BoundedGraphWalk.CollectForwardTypeNames(heap, f.TargetAddress, PathWalkMaxNodes, PathWalkMaxDepth, out bool wasCapped, cancellationToken);

                if (wasCapped)
                    subgraphWalkCappedCount++;

                ulong retainedBytes;
                bool retainedSizeWasWalked;
                bool retainedSizeIsExact;
                if (exactRetainedByAddress is not null && exactRetainedByAddress.TryGetValue(f.TargetAddress, out ulong exactRetainedBytes))
                {
                    retainedBytes = exactRetainedBytes;
                    retainedSizeWasWalked = false;
                    retainedSizeIsExact = true;
                }
                else
                {
                    retainedByAddress.TryGetValue(f.TargetAddress, out RetainedSizeResult retained);
                    retainedBytes = retained.RetainedSize;
                    retainedSizeWasWalked = retained.WasWalked;
                    retainedSizeIsExact = false;
                }

                subgraphFindings.Add(new RootOwnedSubgraphFinding(
                    TargetAddress: f.TargetAddress,
                    TargetTypeName: f.TargetTypeName,
                    RootKind: f.RootKind,
                    SubgraphTypeNames: subgraphTypeNames,
                    SubgraphNodeCount: subgraphTypeNames.Count,
                    WasCapped: wasCapped,
                    EstimatedRetainedBytes: retainedBytes,
                    RetainedSizeWasWalked: retainedSizeWasWalked,
                    RetainedSizeIsExact: retainedSizeIsExact));
            }

            return new GCRootDomainResult(
                TotalRoots: roots.Count,
                ByKind: projection.ByKind,
                TopRootsBySeverity: topFindings,
                RootOwnedSubgraphs: subgraphFindings,
                SubgraphWalkCapped: subgraphWalkCappedCount > 0,
                SubgraphWalkCappedCount: subgraphWalkCappedCount,
                DroppedZeroEstimateRootCount: projection.DroppedZeroEstimateRootCount);
        }

        private static GCRootDomainResult EmptyResult() =>
            new(TotalRoots: 0,
                ByKind: Array.Empty<RootKindSummary>(),
                TopRootsBySeverity: Array.Empty<RootFinding>(),
                RootOwnedSubgraphs: Array.Empty<RootOwnedSubgraphFinding>(),
                SubgraphWalkCapped: false,
                SubgraphWalkCappedCount: 0);
    }
}
