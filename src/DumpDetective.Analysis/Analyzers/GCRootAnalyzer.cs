using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Phase 1 retyping batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// retyped onto the SDK's capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing roots
/// through <see cref="IHeapRootQuery"/> (<c>heap.roots</c>), retained-byte estimates through
/// <see cref="IHeapDominatorQuery"/> (<c>heap.dominators</c>, optional — null when Stage B wasn't
/// built for this run), object metadata through <see cref="IHeapObjectLookup"/>, forward reference
/// walks through <see cref="IHeapReferenceQuery"/>, per-object generation through
/// <see cref="IHeapSegmentQuery.GetGeneration"/>, and heap-wide totals through
/// <see cref="IHeapTypeStatisticsQuery"/>. Runs through the existing pipeline via
/// <see cref="GCRootAnalyzerLegacyAdapter"/>, which also carries the
/// <c>IRequiresReachableGraphIndex</c>/<c>IRequiresDominatorTreeIndex</c> markers this analyzer's
/// dominator-tree dependency relies on the pipeline pre-building — those markers are gated on the
/// *registered* <c>Core.Abstractions.IAnalyzer</c> instance, so they live on the adapter now, not
/// here.
/// </summary>
/// <remarks>
/// Requires the Phase-1 heap index to run at all — matches this analyzer's pre-retyping behavior
/// exactly (it returned an empty result whenever <c>HeapAnalysisCache.TryGetHeapIndex</c> failed,
/// rather than falling back to a live-heap pass), since its per-kind "% of managed heap" and
/// severity scoring both depend on the index's exact type-aggregate totals.
/// </remarks>
public sealed class GCRootAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    // Matches BoundedGraphWalk.AbsoluteMaxDepth's precedent (see docs/refactor/analysis-profile-
    // removal-plan.md §9.16 for why these bounds exist without being user-configurable) — used
    // for both the forward path-type-name walk and the retained-size fallback walk, either of
    // which still runs when the exact dominator tree can't answer a query.
    private const int PathWalkMaxNodes = 500;
    private const int PathWalkMaxDepth = 20;

    // Bounds the per-thread stack-frame-owner attribution enrichment, not the returned finding set
    // itself — that lookup is measured-expensive per root and purely cosmetic (an unenriched row
    // still carries correct kind/type/bytes/severity, just no "in Type.Method()" text).
    private const int StackOwnerAttributionLimit = 20;

    public string Name => "GC Root Analysis";
    public string Category => "Memory";

    public AnalyzerDomainResult? LastResult { get; private set; }

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapRootQuery rootQuery = context.HeapRoots
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapRoots}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
        IHeapReferenceQuery referenceQuery = context.HeapReferences
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapReferences}' capability.");
        IHeapSegmentQuery segmentQuery = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapTypes}' capability.");

        // context.HeapDominators is deliberately not required — null means Stage B wasn't built
        // for this run, and every consumer below degrades to shallow-size/BFS-heuristic behavior
        // in that case, exactly like the pre-retyping analyzer's own null-treeProvider path.
        LastResult = Analyze(rootQuery, context.HeapDominators, objectLookup, referenceQuery, segmentQuery, typeStatistics, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static AnalyzerDomainResult Analyze(
        IHeapRootQuery rootQuery,
        IHeapDominatorQuery? dominatorQuery,
        IHeapObjectLookup objectLookup,
        IHeapReferenceQuery referenceQuery,
        IHeapSegmentQuery segmentQuery,
        IHeapTypeStatisticsQuery typeStatistics,
        CancellationToken cancellationToken)
    {
        if (!typeStatistics.HasExactGenerationData)
            return EmptyResult();

        cancellationToken.ThrowIfCancellationRequested();

        var roots = new List<HeapRootRef>();
        foreach (HeapRootRef root in rootQuery.EnumerateRoots())
            roots.Add(root);

        if (roots.Count == 0)
            return EmptyResult();

        ulong totalHeapBytes = typeStatistics.GetTotalIndexedBytes();
        bool hasExactDominator = dominatorQuery is not null;

        var kindCounts = new Dictionary<string, int>(8);
        var kindBytes = new Dictionary<string, ulong>(8);
        // Index: 0 = Gen0, 1 = Gen1, 2 = Gen2, 3 = Loh. Poh/Frozen/Unknown targets are counted
        // toward kindCounts but not toward any generation bucket (matches the audit's original
        // Gen0-2/LOH-only scope).
        var kindGenerationCounts = new Dictionary<string, int[]>(8);
        Dictionary<string, List<ulong>>? targetsByKind = hasExactDominator ? new Dictionary<string, List<ulong>>(8) : null;
        var findings = new List<RootFinding>(roots.Count);
        int droppedZeroEstimateRootCount = 0;

        foreach (HeapRootRef root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Dropped silently before P2-5 (pre-retyping): a null target, unresolvable object
            // metadata, or a zero-size object all mean this root contributes no retained-byte
            // estimate at all — surfaced as a single count on GCRootDomainResult rather than left
            // invisible.
            if (root.TargetAddress == 0 || !objectLookup.TryGetObject(root.TargetAddress, out HeapObjectRef targetObj) || targetObj.Size == 0)
            {
                droppedZeroEstimateRootCount++;
                continue;
            }

            string kind = root.Kind.ToString();
            kindCounts[kind] = (kindCounts.TryGetValue(kind, out int count) ? count : 0) + 1;
            kindBytes[kind] = (kindBytes.TryGetValue(kind, out ulong bytes) ? bytes : 0UL) + targetObj.Size;

            if (!kindGenerationCounts.TryGetValue(kind, out int[]? genCounts))
                kindGenerationCounts[kind] = genCounts = new int[4];
            switch (segmentQuery.GetGeneration(root.TargetAddress))
            {
                case HeapGenerationTag.Gen0: genCounts[0]++; break;
                case HeapGenerationTag.Gen1: genCounts[1]++; break;
                case HeapGenerationTag.Gen2: genCounts[2]++; break;
                case HeapGenerationTag.Loh: genCounts[3]++; break;
            }

            if (targetsByKind is not null)
            {
                if (!targetsByKind.TryGetValue(kind, out List<ulong>? targetList))
                    targetsByKind[kind] = targetList = new List<ulong>();
                targetList.Add(root.TargetAddress);
            }

            ulong exactRetainedBytes = 0;
            bool retainedBytesIsExact = hasExactDominator && dominatorQuery!.TryGetRetainedSize(root.TargetAddress, out exactRetainedBytes);
            ulong retainedBytes = retainedBytesIsExact ? exactRetainedBytes : targetObj.Size;

            int severity = ComputeSeverity(retainedBytes, kind);

            string? fieldDescription = null;
            if (kind is "StaticVar" or "ThreadStaticVar" && root.OwnerTypeName is not null && root.FieldName is not null)
            {
                fieldDescription = root.AppDomainId is int appDomainId && appDomainId != 1
                    ? $"{root.OwnerTypeName}.{root.FieldName} [AppDomain#{appDomainId}]"
                    : $"{root.OwnerTypeName}.{root.FieldName}";
            }

            findings.Add(new RootFinding(
                RootKind: kind,
                RootAddress: root.RootAddress,
                FieldDescription: fieldDescription,
                TargetTypeName: targetObj.TypeDisplayName,
                TargetAddress: root.TargetAddress,
                EstimatedRetainedBytes: retainedBytes,
                SeverityScore: severity,
                RetainedBytesIsExact: retainedBytesIsExact));
        }

        var byKind = new List<RootKindSummary>(kindCounts.Count);
        foreach (KeyValuePair<string, int> kv in kindCounts)
        {
            string kind = kv.Key;
            ulong estBytes;
            bool isExact;
            if (hasExactDominator && targetsByKind is not null && targetsByKind.TryGetValue(kind, out List<ulong>? targets))
            {
                estBytes = ComputeExclusiveRetainedByKind(dominatorQuery!, targets);
                isExact = true;
            }
            else
            {
                estBytes = kindBytes.TryGetValue(kind, out ulong kb) ? kb : 0UL;
                isExact = false;
            }

            double pct = totalHeapBytes > 0 ? (double)estBytes / totalHeapBytes * 100.0 : 0.0;

            double gen0Fraction = 0.0, gen1Fraction = 0.0, gen2Fraction = 0.0, lohFraction = 0.0;
            if (kindGenerationCounts.TryGetValue(kind, out int[]? genCounts) && kv.Value > 0)
            {
                gen0Fraction = (double)genCounts[0] / kv.Value;
                gen1Fraction = (double)genCounts[1] / kv.Value;
                gen2Fraction = (double)genCounts[2] / kv.Value;
                lohFraction = (double)genCounts[3] / kv.Value;
            }

            byKind.Add(new RootKindSummary(kind, kv.Value, estBytes, pct, isExact,
                gen0Fraction, gen1Fraction, gen2Fraction, lohFraction));
        }
        byKind.Sort(static (a, b) => b.EstimatedRetainedBytes.CompareTo(a.EstimatedRetainedBytes));

        findings.Sort(static (a, b) => b.SeverityScore.CompareTo(a.SeverityScore));

        // Owner-method attribution for Stack roots, scoped to the severity-ranked top-N only — the
        // per-thread frame walk this triggers on first use is too costly to run for every Stack
        // root in the dump, and this enrichment is purely cosmetic (a missing FieldDescription
        // loses no data — kind/type/bytes/severity are unaffected).
        int ownerAttributionCount = Math.Min(findings.Count, StackOwnerAttributionLimit);
        for (int i = 0; i < ownerAttributionCount; i++)
        {
            RootFinding f = findings[i];
            if (f.RootKind == "Stack" && rootQuery.TryResolveStackFrameOwner(f.RootAddress, out string ownerType, out string methodName))
                findings[i] = f with { FieldDescription = $"in {ownerType}.{methodName}()" };
        }

        IReadOnlyList<RootFinding> topFindings = findings;

        // BFS path tracing + retained-size estimate for every finding — uncapped (measured
        // affordable pre-retyping: 568ms uncapped vs. 874ms capped-to-25 on a real dump with 1,404
        // findings; see docs/refactor/analysis-profile-removal-plan.md §9.16).
        int pathN = findings.Count;
        var exactRetainedByAddress = hasExactDominator ? new Dictionary<ulong, ulong>(pathN) : null;
        var walkCandidates = new List<(ulong Address, ulong ShallowSize)>(pathN);
        for (int i = 0; i < pathN; i++)
        {
            RootFinding f = findings[i];
            if (exactRetainedByAddress is not null && dominatorQuery!.TryGetRetainedSize(f.TargetAddress, out ulong exactRetainedBytes))
            {
                exactRetainedByAddress[f.TargetAddress] = exactRetainedBytes;
                continue;
            }

            if (objectLookup.TryGetObject(f.TargetAddress, out HeapObjectRef targetObj))
                walkCandidates.Add((f.TargetAddress, targetObj.Size));
        }

        var retainedVisited = new HashSet<ulong>(capacity: Math.Min(pathN * 64, 4096));
        var retainedByAddress = new Dictionary<ulong, ulong>(walkCandidates.Count);
        // Stable order by descending shallow size — mirrors the pre-retyping selector's tie-break
        // so which candidate "claims" a shared subgraph first (via the exclusivity of
        // retainedVisited) stays deterministic the same way.
        walkCandidates.Sort(static (a, b) => b.ShallowSize.CompareTo(a.ShallowSize));
        foreach ((ulong address, _) in walkCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            retainedByAddress[address] = CapabilityBoundedGraphWalk.ComputeExclusiveRetained(address, objectLookup, referenceQuery, retainedVisited, PathWalkMaxNodes, PathWalkMaxDepth, cancellationToken);
        }

        var subgraphFindings = new List<RootOwnedSubgraphFinding>(pathN);
        int subgraphWalkCappedCount = 0;

        for (int i = 0; i < pathN; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RootFinding f = findings[i];

            List<string> subgraphTypeNames = CapabilityBoundedGraphWalk.CollectForwardTypeNames(f.TargetAddress, objectLookup, referenceQuery, PathWalkMaxNodes, PathWalkMaxDepth, out bool wasCapped, cancellationToken);

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
                retainedSizeWasWalked = retainedByAddress.TryGetValue(f.TargetAddress, out retainedBytes);
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
            ByKind: byKind,
            TopRootsBySeverity: topFindings,
            RootOwnedSubgraphs: subgraphFindings,
            SubgraphWalkCapped: subgraphWalkCappedCount > 0,
            SubgraphWalkCappedCount: subgraphWalkCappedCount,
            DroppedZeroEstimateRootCount: droppedZeroEstimateRootCount);
    }

    private static GCRootDomainResult EmptyResult() =>
        new(TotalRoots: 0,
            ByKind: Array.Empty<RootKindSummary>(),
            TopRootsBySeverity: Array.Empty<RootFinding>(),
            RootOwnedSubgraphs: Array.Empty<RootOwnedSubgraphFinding>(),
            SubgraphWalkCapped: false,
            SubgraphWalkCappedCount: 0);

    private static int ComputeSeverity(ulong retainedBytes, string kind)
    {
        int baseScore = retainedBytes switch
        {
            >= 100_000_000 => 100,
            >= 10_000_000 => 80,
            >= 1_000_000 => 60,
            >= 100_000 => 40,
            >= 10_000 => 20,
            _ => 5
        };

        int multiplier = kind switch
        {
            "StrongHandle" => 3,
            "FinalizerQueue" => 2,
            "PinnedHandle" => 2,
            "Stack" => 1,
            _ => 1
        };

        return Math.Min(baseScore * multiplier, 300);
    }

    /// <summary>Port of the pre-retyping <c>DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes</c>
    /// against <see cref="IHeapDominatorQuery"/> instead of the dump-internal
    /// <c>IDominatorTreeProvider</c> directly — same dominator-chain walk, same semantics.</summary>
    private static ulong ComputeExclusiveRetainedByKind(IHeapDominatorQuery dominatorQuery, IReadOnlyList<ulong> targets)
    {
        if (targets.Count == 0)
            return 0;

        var targetSet = new HashSet<ulong>(targets);

        ulong total = 0;
        foreach (ulong target in targetSet)
        {
            if (IsDominatedByAnotherTarget(dominatorQuery, target, targetSet))
                continue;

            if (dominatorQuery.TryGetRetainedSize(target, out ulong retainedBytes))
                total += retainedBytes;
        }

        return total;
    }

    private static bool IsDominatedByAnotherTarget(IHeapDominatorQuery dominatorQuery, ulong target, HashSet<ulong> targetSet)
    {
        ulong current = target;
        while (dominatorQuery.TryGetImmediateDominator(current, out ulong dominatorAddress))
        {
            if (dominatorAddress == 0)
                return false;

            if (targetSet.Contains(dominatorAddress))
                return true;

            current = dominatorAddress;
        }

        return false;
    }

    public void Dispose() { }
}
