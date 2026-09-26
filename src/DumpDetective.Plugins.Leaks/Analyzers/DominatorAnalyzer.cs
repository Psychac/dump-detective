using DumpDetective.Analysis.Algorithms;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Detects heuristic "dominator suspect" types by combined size/fan-in/Gen2 scoring, with an
/// optional exact Lengauer-Tarjan overlay (per-type retained bytes, dominance chains, cross-type
/// overlap, root chains) when a prebuilt dominator tree is available for this session.
/// </summary>
/// <remarks>
/// Tier-1-only batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — despite
/// the plan's original grep-derived Tier-1+Tier-2 bucketing, this analyzer never reads a live object
/// field *value*; every ClrMD touch point (reference enumeration, dominator-tree lookups, type
/// statistics, root-path search) is coarse/structural, already covered by an existing Tier-1
/// surface. Same re-classification <c>LockGraphAnalyzer</c>/<c>ThreadStackClusterAnalyzer</c> got
/// during their own batch, for the same reason.
///
/// <para>
/// <see cref="IHeapReferenceQuery.EnumerateReferences"/> (<c>heap.references</c>) replaces the
/// pre-retyping analyzer's own hand-inlined array/instance-field walk in
/// <c>CountIncomingReferencesByAddress</c> (its <c>FIX-1</c>/<c>FIX-2</c> allocation-avoidance
/// comments) outright — it already mirrors <c>ClrObject.EnumerateReferences(carefully: true)</c>,
/// the same ClrMD API used at 18+ other call sites across the dump-side codebase.
/// <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/> (<c>heap.types</c>) already carries
/// <c>SampleAddress</c>/<c>TotalSize</c>/<c>LohSize</c>/<c>InstanceCount</c>/<c>Gen2Count</c> per
/// type (the last built for <c>GCGenerationAnalyzer</c>'s pilot, zero/false when
/// <see cref="IHeapTypeStatisticsQuery.HasExactGenerationData"/> is <c>false</c> — the same
/// fast/fallback duality the pre-retyping analyzer's own <c>TypeAggregateIndexEntry</c>-vs-
/// <c>CachedTypeStatistics</c> fork encoded by hand), removing the need for this analyzer to ever
/// resolve a <c>ClrObject</c>/<c>MethodTable</c> itself for candidate selection.
/// <see cref="CapabilityBoundedGraphWalk.ComputeExclusiveRetained"/> (extracted during
/// <c>MemoryAnalyzer</c>'s batch) replaces <c>RetainedSizeCandidateSelector.SelectAndCompute</c> for
/// both the heuristic top-K walk and <c>PopulateRetainedBytes</c>, called once per candidate against
/// a shared <c>visited</c> set to preserve the same exclusive (non-overlapping) retained-size
/// semantics the pre-retyping analyzer's own comment on that shared set called out.
/// </para>
/// <para>
/// Three narrow SDK additions, same "extend once a real need exists" pattern as every prior batch:
/// <see cref="IHeapDominatorQuery.TryGetRetainedBytesByType"/> (aggregate-by-type, distinct in shape
/// from the existing per-address <see cref="IHeapDominatorQuery.TryGetRetainedSize"/>) replaces
/// <c>IDominatorTreeProvider.TryGetRetainedBytesByMethodTable</c> for the exact per-type Gen2/LOH
/// sub-table lookup; <see cref="IHeapReferenceQuery.EnumerateIncomingReferenceCounts"/> (a genuinely
/// different shape from every prior <c>heap.reverse-references</c> consumer's per-address lookups)
/// replaces <c>IBackwardReferenceProvider.EnumerateChildCounts</c> for the primary (index-backed)
/// leak-signal pass, backed by a true per-row iterator
/// (<c>ReverseEdgeIndexReader.EnumerateChildAddressesAndCounts</c>) rather than buffering the whole
/// index into a list; <see cref="IHeapRootPathQuery.TryFindRootPathHops"/> (a hop-type-name-list
/// sibling to the existing single-formatted-string <see cref="IHeapRootPathQuery.TryFindRootPath"/>)
/// replaces the audit P2 root-chain search's need for <see cref="RootChainSummary.HopTypeNames"/>,
/// which the formatted string can't be reconstructed into. <see cref="IHeapObjectStream"/> also
/// gained its first live, non-scan-participant default implementation
/// (<see cref="HeapObjectStream"/>) for this batch — this analyzer's no-index fallback needs to
/// stream the whole heap only when no disk index exists, a different shape from every prior
/// <c>heap.objects</c> consumer (a heap-index scan participant, only invoked during the pipeline's
/// shared scan pass).
/// </para>
/// <para>
/// <b>Accepted simplifications</b> (same reasoning already applied in
/// <c>GCRootAnalyzer</c>/<c>MemoryAnalyzer</c>/<c>StaticRootAnalyzer</c>'s own batches): the
/// live-scan fallback's <c>cache.MethodTableHasOutgoingRefs</c> shape pre-filter is dropped — it
/// costs at most one trivial single-object iteration for a true leaf and changes no output value.
/// <c>ComputeCrossTypeOverlap</c>'s <c>cache.EnumerateIndexedEntriesAsTuples</c> disk-index stream
/// becomes a filtered <see cref="IHeapObjectStream.EnumerateObjects"/> pass — same data, an
/// already-existing Tier-1 surface, keyed by <see cref="HeapObjectRef.TypeDisplayName"/> instead of
/// a raw method table (this analyzer's candidate set is already keyed by display name via
/// <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/>'s dictionary, so no method-table lookup
/// is needed at all here, unlike the pre-retyping version's own <c>MethodTable</c>-keyed dictionary).
/// </para>
/// <para>
/// <b>Dropped, not ported</b>: the <c>ILogger&lt;DominatorAnalyzer&gt;</c>-based diagnostic logging
/// (exact-vs-heuristic retained-bytes comparison, memory-usage checkpoints gated on
/// <c>Diagnostics.EnableMemoryDiagnostics</c>) — no already-retyped analyzer's inner SDK type
/// receives a logger or diagnostics options through the <c>LegacyAnalyzerAdapter</c> chain (neither
/// is carried by <see cref="AnalysisContext"/> by design; see its own remarks), so this follows the
/// same precedent rather than inventing new plumbing for a single consumer.
/// </para>
/// </remarks>
[AnalyzerModule(key: "dominator", displayName: "Dominator Analysis", order: 220, tags: ["retention", "dominator"])]
[RequiresCapability(
    CapabilityVocabulary.HeapDominators,
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapReferences,
    CapabilityVocabulary.HeapReverseReferences,
    CapabilityVocabulary.HeapTypes)]
[OptionalCapability(CapabilityVocabulary.TraceAllocSamples)]
[OptionalCapability(CapabilityVocabulary.TraceGcEvents)]
public sealed class DominatorAnalyzer :
    IAnalyzer, IProducesAnalyzerDomainResult, Pipeline.IRequiresReachableGraphIndex, Pipeline.IRequiresDominatorTreeIndex
{
    public string Name => "Dominator Analysis";
    public string Category => "Memory";
    public int Order => 110;

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    // Only used by AnalyzeObjectsPass's live-heap fallback (no disk-backed reverse index
    // available) — the primary reverse-index path (BuildLeakSignalsFromReverseIndex) is
    // exhaustive by construction and never capped by these. Not user-configurable — folded from
    // the former ExecutionPolicy knob per docs/refactor/analysis-options-removal-plan.md: the same
    // dump must always produce the same findings.
    private const int FallbackMaxLeakScanObjects = 2_000_000;
    private const int FallbackMaxReferenceAddresses = 1_000_000;

    private const int MaxDepth = 20;

    // Fan-in (incoming-reference-count) histogram bucket boundaries (minCount inclusive, maxCount exclusive).
    private static readonly (int Min, int Max, string Label)[] s_fanInBuckets =
    [
        (0,   10,           "0 – 10"),
        (10,  50,           "10 – 50"),
        (50,  200,          "50 – 200"),
        (200, int.MaxValue, "≥ 200"),
    ];

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapObjectStream heapObjects = context.HeapObjects
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapObjects}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapObjects}' capability.");
        IHeapReferenceQuery references = context.HeapReferences
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapReferences}' capability.");
        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");
        IHeapRootPathQuery rootPaths = context.HeapRootPaths
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRootPaths}' capability.");
        IHeapReverseReferenceQuery? reverseReferences = context.HeapReverseReferences;
        IHeapDominatorQuery? dominators = context.HeapDominators;

        var options = context.AnalyzerOptions as RetentionOptions ?? new RetentionOptions();

        LastResult = await Analyze(heapObjects, objectLookup, references, reverseReferences, typeStatistics, dominators, rootPaths, options, context, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
    /// <c>type.retention</c> observation per top-by-retained-bytes type — the exact canonical
    /// example named in observation-and-correlation-model.md § 4's correlation-recipes table
    /// ("type.retention: T holds 800 MB, 2M instances, static-rooted"). Bounded to
    /// <c>RetentionOptions</c>'s top-N selection, inline where the sample address is resolved to a
    /// real <see cref="Sdk.Identity.TypeRef"/> via <see cref="IHeapObjectLookup"/>.
    /// </summary>
    private static async ValueTask<DominatorDomainResult> Analyze(
        IHeapObjectStream heapObjects,
        IHeapObjectLookup objectLookup,
        IHeapReferenceQuery references,
        IHeapReverseReferenceQuery? reverseReferences,
        IHeapTypeStatisticsQuery typeStatistics,
        IHeapDominatorQuery? dominators,
        IHeapRootPathQuery rootPaths,
        RetentionOptions options,
        AnalysisContext observationContext,
        CancellationToken cancellationToken)
    {
        LeakSignals signals = reverseReferences is not null
            ? BuildLeakSignalsFromReverseIndex(reverseReferences, objectLookup, options)
            : AnalyzeObjectsPass(heapObjects, objectLookup, references, options);

        DominatorDomainResult result = BuildHeuristicResult(objectLookup, references, rootPaths, typeStatistics, options, signals, cancellationToken);

        // §D9-gated exact Lengauer-Tarjan overlay. On success, attaches an exact per-type
        // retained-bytes lookup that §Report integration (DominatorSectionBuilder) uses to replace
        // the Gen2/LOH sub-table's heuristic column — everything else about `result` (including the
        // main dominator-suspects and highly-referenced-objects tables) is untouched. A cap-exceeded
        // or exception outcome leaves `result` exactly as the heuristic built it, same "ship dark"
        // safety property established for the pre-retyping analyzer.
        if (options.EnableExactDominatorTree && dominators is not null && result is DominatorDomainResult heuristicResult)
        {
            ExactDominatorData exact = TryReadExactDominatorTree(dominators, objectLookup, heapObjects, rootPaths, heuristicResult, options, cancellationToken);
            if (exact.RetainedBytesByTypeName is not null || exact.ChainsByTypeName is not null)
            {
                result = heuristicResult with
                {
                    ExactRetainedBytesByTypeName = exact.RetainedBytesByTypeName ?? heuristicResult.ExactRetainedBytesByTypeName,
                    DominatorChainsByTypeName = exact.ChainsByTypeName ?? heuristicResult.DominatorChainsByTypeName,
                    ContainingTypeNameByTypeName = exact.ContainingTypeNameByTypeName ?? heuristicResult.ContainingTypeNameByTypeName,
                    CrossTypeOverlapPairs = exact.CrossTypeOverlapPairs ?? heuristicResult.CrossTypeOverlapPairs,
                    CrossTypeOverlapInstanceScanCapped = exact.CrossTypeOverlapInstanceScanCapped,
                    RootChainsByTypeName = exact.RootChainsByTypeName ?? heuristicResult.RootChainsByTypeName,
                };
            }
        }

        await EmitRetentionObservationsAsync(objectLookup, result, observationContext, cancellationToken);

        return result;
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
    /// <c>type.retention</c> observation per <see cref="DominatorDomainResult.TopTypes"/> entry —
    /// the exact canonical example named in observation-and-correlation-model.md § 4's
    /// correlation-recipes table ("type.retention: T holds 800 MB, 2M instances, static-rooted").
    /// Runs after <see cref="Analyze"/>'s optional exact-dominator-tree overlay, so
    /// <c>retained.bytes</c> uses <see cref="DominatorDomainResult.ExactRetainedBytesByTypeName"/>
    /// when that overlay succeeded for this type, not <see cref="TypeSnapshot.EstimatedRetainedBytes"/>'s
    /// always-heuristic BFS estimate.
    /// Also emits, Phase 5 step 2: a whole-run <c>type.retention-population</c> observation (the
    /// highly-referenced-object count and the reference-tracking-capped count, neither of which the
    /// per-type stream above can provide — since extended Phase 5 step 4 with
    /// <see cref="DominatorDomainResult.CandidateCount"/>/<see cref="DominatorDomainResult.AnalyzedCount"/>/
    /// <see cref="DominatorDomainResult.TotalEstimatedRetainedBytes"/>/a retention-pressure ratio/
    /// <see cref="DominatorDomainResult.TopHighlyReferencedTotalBytes"/> for
    /// <c>DominatorTrendComparer</c>'s headline metrics) and a per-type <c>type.retention-hotspot</c> observation
    /// per <see cref="DominatorDomainResult.TopRetentionTypes"/> entry (an aggregation over
    /// <see cref="DominatorDomainResult.TopHighlyReferencedObjects"/> by type, a different population
    /// than <see cref="DominatorDomainResult.TopDominatorTypes"/>).
    /// </summary>
    private static async ValueTask EmitRetentionObservationsAsync(
        IHeapObjectLookup objectLookup, DominatorDomainResult result, AnalysisContext observationContext, CancellationToken cancellationToken)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapReferences, CapabilityVocabulary.HeapTypes };

        foreach (TypeSnapshot snapshot in result.TopDominatorTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (snapshot.SampleAddress == 0 || !objectLookup.TryGetObject(snapshot.SampleAddress, out HeapObjectRef subject))
                continue;

            ulong exactRetainedBytes = 0;
            bool isExact = result.ExactRetainedBytesByTypeName is not null
                && result.ExactRetainedBytesByTypeName.TryGetValue(snapshot.TypeName, out exactRetainedBytes);
            ulong retainedBytes = isExact ? exactRetainedBytes : snapshot.EstimatedRetainedBytes;

            await observationContext.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.retention",
                Subjects = [subject.Type],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(snapshot.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(snapshot.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["retained.bytes"] = new Measure(retainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["estimated-retained.bytes"] = new Measure(snapshot.EstimatedRetainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2.count"] = new Measure(snapshot.Gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["loh.bytes"] = new Measure(snapshot.LohBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    AnalyzerKey = "Dominator Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = isExact ? FidelityLevel.Full : FidelityLevel.Degraded,
                },
                Confidence = isExact ? 1.0 : 0.7,
            }, cancellationToken);
        }

        // candidate.count/analyzed.count/retained.bytes/pressure.ratio/highly-referenced.bytes
        // added Phase 5 step 4 for DominatorTrendComparer's headline metrics (Bucket D of
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md). analyzed.count
        // deliberately backs both the comparer's old dominator.analyzed and dominator.top.count
        // metrics — AnalyzedCount and TopDominatorTypes.Count are always identical (both assigned
        // from the same topTypes.Count at DominatorDomainResult's one construction site), so one
        // measure, not two. pressure.ratio is a plain derived ratio
        // (TotalEstimatedRetainedBytes / TotalHeapBytes), not a classifier composite.
        var populationMeasures = new Dictionary<string, Measure>
        {
            ["highly-referenced.count"] = new Measure(result.HighlyReferencedObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["highly-referenced.bytes"] = new Measure(result.TopHighlyReferencedTotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["approximated-reference-addresses.count"] = new Measure(result.ApproximatedReferenceAddresses, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["candidate.count"] = new Measure(result.CandidateCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["analyzed.count"] = new Measure(result.AnalyzedCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["retained.bytes"] = new Measure(result.TotalEstimatedRetainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["pressure.ratio"] = new Measure(
                result.TotalHeapBytes > 0 ? (double)result.TotalEstimatedRetainedBytes / result.TotalHeapBytes : 0.0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
            // Added phase 5 step 6.2 (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md)
            // for ReferenceChainDominatorCorrelationInsightRule — pressure.ratio alone can't
            // reconstruct this exactly (division is lossy at ratio == 0), and the rule needs the
            // raw denominator to compute its own per-type retained-bytes percentages.
            ["total.bytes"] = new Measure(result.TotalHeapBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
        };
        populationMeasures["is-object-scan-capped"] = new Measure(result.ObjectScanCapped ? 1 : 0, MeasureUnit.Count, MeasureSemantics.Absolute);
        populationMeasures["is-reference-counting-skipped"] = new Measure(result.ReferenceCountingSkipped ? 1 : 0, MeasureUnit.Count, MeasureSemantics.Absolute);
        RootPathEvidenceMeasures.Add(populationMeasures, "top-highly-referenced.", result.TopHighlyReferencedObjects is { Count: > 0 } ? result.TopHighlyReferencedObjects[0].Evidence : null);

        await observationContext.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "type.retention-population",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = populationMeasures,
            Provenance = new Provenance
            {
                Artifact = observationContext.ArtifactId!.Value,
                AnalyzerKey = "Dominator Analysis",
                CapabilitiesUsed = capabilitiesUsed,
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);

        if (result.TopRetentionTypes is { Count: > 0 } topRetentionTypes)
        {
            foreach (RetentionTypeSnapshot type in topRetentionTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(type.TypeName);

                await observationContext.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "type.retention-hotspot",
                    Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity }],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["object.count"] = new Measure(type.ObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["total.bytes"] = new Measure(type.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                        ["total-incoming-references.count"] = new Measure(type.TotalIncomingReferences, MeasureUnit.Count, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = observationContext.ArtifactId!.Value,
                        AnalyzerKey = "Dominator Analysis",
                        CapabilitiesUsed = capabilitiesUsed,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                }, cancellationToken);
            }
        }
    }

    private readonly record struct ExactDominatorData(
        IReadOnlyDictionary<string, ulong>? RetainedBytesByTypeName,
        IReadOnlyDictionary<string, IReadOnlyList<DominatorChainHop>>? ChainsByTypeName,
        IReadOnlyDictionary<string, string>? ContainingTypeNameByTypeName = null,
        IReadOnlyList<CrossTypeOverlapPair>? CrossTypeOverlapPairs = null,
        bool CrossTypeOverlapInstanceScanCapped = false,
        IReadOnlyDictionary<string, RootChainSummary>? RootChainsByTypeName = null);

    private static ExactDominatorData TryReadExactDominatorTree(
        IHeapDominatorQuery dominators,
        IHeapObjectLookup objectLookup,
        IHeapObjectStream heapObjects,
        IHeapRootPathQuery rootPaths,
        DominatorDomainResult heuristicResult,
        RetentionOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            // Only resolve type names the report will actually display (the Gen2/LOH sub-table's
            // candidates, already computed by the heuristic pass above) — resolving every unique
            // type's name would be wasted work for types the report never shows.
            //
            // §8 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): recognizing
            // another candidate's sample address as an ancestor needs every candidate's address known
            // up front, so this is a separate pass before the main loop below rather than being
            // populated lazily inside it.
            var candidateTypeNamesByAddress = new Dictionary<ulong, string>(heuristicResult.TopDominatorTypes.Count);
            foreach (TypeSnapshot candidate in heuristicResult.TopDominatorTypes)
                candidateTypeNamesByAddress[candidate.SampleAddress] = candidate.TypeName;

            Dictionary<string, ulong>? exactByTypeName = null;
            Dictionary<string, IReadOnlyList<DominatorChainHop>>? chainsByTypeName = null;
            Dictionary<string, string>? containingTypeNameByTypeName = null;
            // §8b needs every instance of every candidate type to be recognizable while streaming
            // the heap below — a plain display-name set, since this analyzer's candidates are
            // already keyed by display name (IHeapTypeStatisticsQuery.GetTypeStatistics).
            var candidateTypeNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (TypeSnapshot candidate in heuristicResult.TopDominatorTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!objectLookup.TryGetObject(candidate.SampleAddress, out HeapObjectRef sample))
                    continue;

                candidateTypeNames.Add(candidate.TypeName);

                if (dominators.TryGetRetainedBytesByType(sample.Type, out ulong exactRetained))
                {
                    exactByTypeName ??= new Dictionary<string, ulong>(StringComparer.Ordinal);
                    exactByTypeName[candidate.TypeName] = exactRetained;
                }

                // P3-3: dominance chain (A dominates B dominates ... dominates this type's sample
                // object) — walks the same disk-backed tree, no extra data structure needed.
                IReadOnlyList<DominatorChainHop> chain = BuildDominatorChain(objectLookup, dominators, sample.Address, options.MaxDominatorChainDepth);
                if (chain.Count > 0)
                {
                    chainsByTypeName ??= new Dictionary<string, IReadOnlyList<DominatorChainHop>>(StringComparer.Ordinal);
                    chainsByTypeName[candidate.TypeName] = chain;

                    // §8: cross-type retained overlap, sample-based — the chain above already walked
                    // every ancestor up to MaxDominatorChainDepth, so recognizing another candidate's
                    // sample address on it is free (no extra provider calls).
                    string? containingTypeName = FindContainingCandidateTypeName(chain, candidateTypeNamesByAddress);
                    if (containingTypeName is not null)
                    {
                        containingTypeNameByTypeName ??= new Dictionary<string, string>(StringComparer.Ordinal);
                        containingTypeNameByTypeName[candidate.TypeName] = containingTypeName;
                    }
                }
            }

            (IReadOnlyList<CrossTypeOverlapPair>? overlapPairs, bool overlapCapped) = ComputeCrossTypeOverlap(
                heapObjects, dominators, candidateTypeNames, options, cancellationToken);

            // Audit P2: root chains are scoped to the Gen2/LOH sub-table only, same candidate
            // filter DominatorSectionBuilder uses for rendering — a root-path search is a real
            // bidirectional BFS per candidate, not a free point lookup like every other per-type
            // field above, so this deliberately doesn't run over the full TopDominatorTypes set.
            var gen2LohCandidates = new List<TypeSnapshot>();
            foreach (TypeSnapshot candidate in heuristicResult.TopDominatorTypes)
            {
                if (candidate.Gen2Count > 0 || candidate.LohBytes > 0)
                    gen2LohCandidates.Add(candidate);
            }
            gen2LohCandidates.Sort(static (a, b) =>
            {
                int byGen2 = b.Gen2Count.CompareTo(a.Gen2Count);
                return byGen2 != 0 ? byGen2 : b.LohBytes.CompareTo(a.LohBytes);
            });
            if (gen2LohCandidates.Count > heuristicResult.MaxTopDominatorTypesToShow)
                gen2LohCandidates.RemoveRange(heuristicResult.MaxTopDominatorTypesToShow, gen2LohCandidates.Count - heuristicResult.MaxTopDominatorTypesToShow);

            IReadOnlyDictionary<string, RootChainSummary>? rootChainsByTypeName = ComputeRootChains(
                rootPaths, gen2LohCandidates, options, cancellationToken);

            return new ExactDominatorData(exactByTypeName, chainsByTypeName, containingTypeNameByTypeName, overlapPairs, overlapCapped, rootChainsByTypeName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return default;
        }
    }

    // P3-3: walks IHeapDominatorQuery.TryGetImmediateDominator from `sampleAddress` up toward
    // the virtual root (dominatorAddress == 0), collecting one hop per ancestor. Ordered root-most
    // first, sample leaf last — the natural "A holds B holds ... holds your object" reading order.
    // `maxDepth` is a safety bound, not a display truncation (see RetentionOptions.MaxDominatorChainDepth's
    // remarks): a real dominator tree's depth is bounded only by the longest single-parent chain in
    // the heap (e.g. a linked list).
    internal static IReadOnlyList<DominatorChainHop> BuildDominatorChain(
        IHeapObjectLookup objectLookup,
        IHeapDominatorQuery dominators,
        ulong sampleAddress,
        int maxDepth)
    {
        var ascending = new List<DominatorChainHop>(capacity: 8);
        ulong current = sampleAddress;

        for (int depth = 0; depth < maxDepth; depth++)
        {
            string typeName = objectLookup.TryGetObject(current, out HeapObjectRef obj) ? obj.TypeDisplayName : StringConstants.UnknownType;
            dominators.TryGetRetainedSize(current, out ulong retainedBytes);
            ascending.Add(new DominatorChainHop(typeName, current, retainedBytes));

            if (!dominators.TryGetImmediateDominator(current, out ulong parent) || parent == 0)
            {
                ascending.Reverse();
                return ascending;
            }

            current = parent;
        }

        // Safety cap reached before the walk reached the virtual root — the chain is deeper than
        // maxDepth (e.g. a long linked list). Append a sentinel (Address 0, never a real object
        // address) so callers render "chain continues" instead of a chain that looks complete.
        ascending.Add(new DominatorChainHop($"… chain continues beyond {maxDepth} hops", 0, 0));
        ascending.Reverse();
        return ascending;
    }

    /// <summary>
    /// §8 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): scans a
    /// root-most-first, sample-leaf-last <paramref name="chain"/> (as built by
    /// <see cref="BuildDominatorChain"/>) for the nearest ancestor hop that is another candidate
    /// type's sample address — i.e. the type whose dominator subtree most tightly contains this
    /// chain's own sample object. The chain's own last hop (itself) is always excluded. Scans from
    /// the leaf backward so the *nearest* containing candidate wins, not the outermost one.
    /// </summary>
    internal static string? FindContainingCandidateTypeName(
        IReadOnlyList<DominatorChainHop> chain,
        IReadOnlyDictionary<ulong, string> candidateTypeNamesByAddress)
    {
        for (int i = chain.Count - 2; i >= 0; i--)
        {
            if (chain[i].Address != 0 && candidateTypeNamesByAddress.TryGetValue(chain[i].Address, out string? typeName))
                return typeName;
        }

        return null;
    }

    /// <summary>
    /// §8b (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): unlike §8a's
    /// single-sample check, this needs every instance of every candidate type. Streams the whole
    /// heap once (<see cref="IHeapObjectStream.EnumerateObjects"/>, no ClrMD field walks) collecting
    /// instance addresses whose display name matches a candidate, bounded by
    /// <see cref="RetentionOptions.MaxCrossTypeOverlapInstancesScanned"/>, then walks each collected
    /// instance's ancestor chain looking for the nearest *different* candidate type — same primitive
    /// as <see cref="FindContainingCandidateTypeName"/>, generalized to a live walk over an instance
    /// population instead of one precomputed chain.
    /// </summary>
    internal static (IReadOnlyList<CrossTypeOverlapPair>? Pairs, bool Capped) ComputeCrossTypeOverlap(
        IHeapObjectStream heapObjects,
        IHeapDominatorQuery dominators,
        IReadOnlySet<string> candidateTypeNames,
        RetentionOptions options,
        CancellationToken cancellationToken)
    {
        if (candidateTypeNames.Count < 2)
            return (null, false); // nothing to overlap with

        var instanceTypeNamesByAddress = new Dictionary<ulong, string>();
        bool capped = false;
        foreach (HeapObjectRef obj in heapObjects.EnumerateObjects())
        {
            if (!candidateTypeNames.Contains(obj.TypeDisplayName))
                continue;

            if (instanceTypeNamesByAddress.Count >= options.MaxCrossTypeOverlapInstancesScanned)
            {
                capped = true;
                break;
            }

            instanceTypeNamesByAddress[obj.Address] = obj.TypeDisplayName;
        }

        if (instanceTypeNamesByAddress.Count == 0)
            return (null, capped);

        var countsByPair = new Dictionary<(string TypeName, string ContainingTypeName), int>();
        var bytesByPair = new Dictionary<(string TypeName, string ContainingTypeName), ulong>();
        foreach ((ulong address, string typeName) in instanceTypeNamesByAddress)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string? containingTypeName, bool hasSameTypeAncestor) = WalkInstanceAncestry(
                dominators, address, typeName, instanceTypeNamesByAddress, options.MaxDominatorChainDepth);
            if (containingTypeName is null)
                continue;

            var key = (typeName, containingTypeName);
            countsByPair[key] = countsByPair.GetValueOrDefault(key) + 1;

            // §8c: only "topmost" instances (no ancestor also of this instance's own type) are
            // exact-byte-summable — see CrossTypeOverlapPair's own doc comment for why a
            // non-topmost instance's bytes are already counted inside another instance's total.
            if (!hasSameTypeAncestor && dominators.TryGetRetainedSize(address, out ulong retainedBytes))
                bytesByPair[key] = bytesByPair.GetValueOrDefault(key) + retainedBytes;
        }

        if (countsByPair.Count == 0)
            return (Array.Empty<CrossTypeOverlapPair>(), capped);

        var pairs = new List<CrossTypeOverlapPair>(countsByPair.Count);
        foreach (var (key, count) in countsByPair)
            pairs.Add(new CrossTypeOverlapPair(key.TypeName, key.ContainingTypeName, count, bytesByPair.GetValueOrDefault(key)));

        return (pairs, capped);
    }

    /// <summary>
    /// Walks the *entire* ancestor chain from <paramref name="address"/> up to the virtual root (or
    /// <paramref name="maxDepth"/>, same safety bound as <see cref="BuildDominatorChain"/>) via
    /// <see cref="IHeapDominatorQuery.TryGetImmediateDominator"/>, recording two independent
    /// facts needed by §8b/§8c: the nearest ancestor of a *different* candidate type (§8b's count —
    /// same-type ancestors are skipped when looking for this, since same-type nesting isn't
    /// cross-type overlap), and whether *any* ancestor anywhere in the chain — not just the nearest
    /// one — is also <paramref name="ownTypeName"/> (§8c's "topmost" exclusion). Exits early once
    /// both are resolved and nothing further can change.
    /// </summary>
    internal static (string? ContainingTypeName, bool HasSameTypeAncestor) WalkInstanceAncestry(
        IHeapDominatorQuery dominators,
        ulong address,
        string ownTypeName,
        IReadOnlyDictionary<ulong, string> instanceTypeNamesByAddress,
        int maxDepth)
    {
        ulong current = address;
        string? nearestDifferentTypeName = null;
        bool hasSameTypeAncestor = false;

        for (int depth = 0; depth < maxDepth; depth++)
        {
            if (!dominators.TryGetImmediateDominator(current, out ulong parent) || parent == 0)
                break;

            if (instanceTypeNamesByAddress.TryGetValue(parent, out string? parentTypeName))
            {
                if (string.Equals(parentTypeName, ownTypeName, StringComparison.Ordinal))
                    hasSameTypeAncestor = true;
                else
                    nearestDifferentTypeName ??= parentTypeName;
            }

            if (hasSameTypeAncestor && nearestDifferentTypeName is not null)
                break; // nothing further up can change either result

            current = parent;
        }

        return (nearestDifferentTypeName, hasSameTypeAncestor);
    }

    /// <summary>
    /// Audit P2 (docs/analysis/phase1/dominator-analyzer-audit.md): "why is this alive," one
    /// root-path search per <paramref name="candidates"/> entry, resolved into a hop-type-name list
    /// (<see cref="IHeapRootPathQuery.TryFindRootPathHops"/>) to match
    /// <see cref="DominatorChainHop"/>'s list shape for rendering.
    /// </summary>
    internal static IReadOnlyDictionary<string, RootChainSummary>? ComputeRootChains(
        IHeapRootPathQuery rootPaths,
        IReadOnlyList<TypeSnapshot> candidates,
        RetentionOptions options,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
            return null;

        var searchOptions = new RootPathSearchOptions(
            options.MaxRootPathCandidateNodes, options.MaxRootPathCandidateDepth,
            options.MaxRootPathExpansionDepth, options.RootPathLargeFanoutThreshold);

        Dictionary<string, RootChainSummary>? rootChainsByTypeName = null;
        foreach (TypeSnapshot candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool found = rootPaths.TryFindRootPathHops(
                candidate.SampleAddress, searchOptions, out string? rootKind, out IReadOnlyList<string>? hopTypeNames, out bool searchTruncated);
            if (!found || rootKind is null || hopTypeNames is null || hopTypeNames.Count == 0)
                continue;

            rootChainsByTypeName ??= new Dictionary<string, RootChainSummary>(StringComparer.Ordinal);
            rootChainsByTypeName[candidate.TypeName] = new RootChainSummary(rootKind, hopTypeNames, searchTruncated);
        }

        return rootChainsByTypeName;
    }

    private static LeakSignals BuildLeakSignalsFromReverseIndex(
        IHeapReverseReferenceQuery reverseReferences,
        IHeapObjectLookup objectLookup,
        RetentionOptions options)
    {
        int threshold = options.HighReferenceThreshold;
        int topN = Math.Max(1, options.TopHighlyReferencedObjectsToShow);

        int highlyReferencedCount = 0;
        var topHeap = new PriorityQueue<(ulong Address, int Count), int>(topN + 1);
        int[] fanInCounts = new int[s_fanInBuckets.Length];

        foreach ((ulong address, int count) in reverseReferences.EnumerateIncomingReferenceCounts())
        {
            AddToFanInHistogram(fanInCounts, count);

            if (count <= threshold)
                continue;

            highlyReferencedCount++;

            topHeap.Enqueue((address, count), count);
            if (topHeap.Count > topN)
                topHeap.Dequeue(); // evict smallest
        }

        // Ascending by count out of the min-heap; reverse below for descending display order.
        var buffer = new List<(ulong Address, int Count)>(topHeap.Count);
        while (topHeap.Count > 0)
            buffer.Add(topHeap.Dequeue());

        var results = new List<HighlyReferencedObjectSnapshot>(buffer.Count);
        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            HighlyReferencedObjectSnapshot? snapshot = CreateHighlyReferencedObjectSnapshot(objectLookup, buffer[i].Address, buffer[i].Count);
            if (snapshot is not null)
                results.Add(snapshot);
        }

        // Unlike the live-scan fallback, there's no fixed-size accumulation dictionary here
        // (no MaxReferenceAddresses cap applies), so no addresses are ever skipped; and there's
        // no per-object ClrMD work to budget via MaxLeakScanObjects, since the whole pass is
        // sequential index reads, not live heap resolution — so the scan is exhaustive over
        // every recorded child, by construction, never capped.
        return new LeakSignals(highlyReferencedCount, ApproximatedReferenceAddresses: 0, results, ObjectScanCapped: false, FanInHistogram: BuildFanInHistogram(fanInCounts));
    }

    private static DominatorDomainResult BuildHeuristicResult(
        IHeapObjectLookup objectLookup,
        IHeapReferenceQuery references,
        IHeapRootPathQuery rootPaths,
        IHeapTypeStatisticsQuery typeStatistics,
        RetentionOptions options,
        LeakSignals signals,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, HeapTypeStatistics> typeStats = typeStatistics.GetTypeStatistics();

        ulong totalHeapBytes = 0;
        foreach (HeapTypeStatistics stat in typeStats.Values)
            totalHeapBytes += stat.TotalSize;

        if (typeStats.Count == 0)
            return new DominatorDomainResult(0, 0, 0, Array.Empty<TypeSnapshot>(), MaxTopDominatorTypesToShow: options.TopHighlyReferencedObjectsToShow, TotalHeapBytes: totalHeapBytes, FanInHistogram: signals.FanInHistogram);

        var candidates = new List<(string TypeName, ulong SampleAddress, int Count, ulong TotalSize, ulong LohSize, long Gen2Count, ulong Score)>(capacity: Math.Min(32, typeStats.Count));

        foreach (KeyValuePair<string, HeapTypeStatistics> kv in typeStats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HeapTypeStatistics stat = kv.Value;
            if (stat.SampleAddress == 0)
                continue;

            ulong totalSize = stat.TotalSize;
            ulong lohSize = stat.LohSize;
            int count = (int)Math.Min(int.MaxValue, stat.InstanceCount);
            long gen2Count = stat.Gen2Count;

            ulong averageSize = count > 0 ? Math.Max(1UL, totalSize / (ulong)count) : 1;
            ulong score = totalSize + lohSize + (ulong)Math.Max(0, gen2Count) * averageSize;
            if (count >= 1_000)
                score += totalSize / 4;

            candidates.Add((kv.Key, stat.SampleAddress, count, totalSize, lohSize, gen2Count, score));
        }

        List<HighlyReferencedObjectSnapshot> topHighlyReferencedObjects = signals.TopHighlyReferencedObjects as List<HighlyReferencedObjectSnapshot>
            ?? new List<HighlyReferencedObjectSnapshot>(signals.TopHighlyReferencedObjects);
        PopulateRetainedBytes(objectLookup, references, topHighlyReferencedObjects, options, cancellationToken);
        PopulateEvidence(rootPaths, topHighlyReferencedObjects, options);
        IReadOnlyList<RetentionTypeSnapshot> topRetentionTypes = BuildTopRetentionTypes(topHighlyReferencedObjects);
        ulong topHighlyReferencedTotalBytes = SumTopHighlyReferencedBytes(topHighlyReferencedObjects);

        if (candidates.Count == 0)
        {
            return new DominatorDomainResult(0, 0, 0, Array.Empty<TypeSnapshot>())
            {
                MaxBreadth = options.MaxLeakScanObjects,
                MaxDepth = MaxDepth,
                HighlyReferencedObjectCount = signals.HighlyReferencedObjectCount,
                ApproximatedReferenceAddresses = signals.ApproximatedReferenceAddresses,
                TopHighlyReferencedObjects = topHighlyReferencedObjects,
                ObjectScanCapped = signals.ObjectScanCapped,
                TopRetentionTypes = topRetentionTypes,
                TopHighlyReferencedTotalBytes = topHighlyReferencedTotalBytes,
                MaxTopDominatorTypesToShow = options.TopHighlyReferencedObjectsToShow,
                TotalHeapBytes = totalHeapBytes,
                FanInHistogram = signals.FanInHistogram
            };
        }

        candidates.Sort(static (a, b) =>
        {
            int score = b.Score.CompareTo(a.Score);
            if (score != 0)
                return score;

            int size = b.TotalSize.CompareTo(a.TotalSize);
            if (size != 0)
                return size;

            return StringComparer.Ordinal.Compare(a.TypeName, b.TypeName);
        });

        int topCount = Math.Min(options.TopHighlyReferencedObjectsToShow, candidates.Count);
        var topTypes = new List<TypeSnapshot>(topCount);

        int maxBreadth = options.MaxLeakScanObjects > 0 ? options.MaxLeakScanObjects : 10_000;

        // Use a shared visited set for all top-K types to produce exclusive (non-overlapping) retained-size semantics,
        // matching the semantics of PopulateRetainedBytes. This ensures the two retained-byte metrics are comparable.
        var visited = new HashSet<ulong>(capacity: Math.Min(topCount * 256, 4096));

        var retainedByAddress = new Dictionary<ulong, ulong>(topCount);
        for (int i = 0; i < topCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong sampleAddress = candidates[i].SampleAddress;
            if (!objectLookup.TryGetObject(sampleAddress, out _))
                continue;

            // Always recorded, even when the walk itself finds nothing new (e.g. a sample already
            // absorbed into an earlier candidate's shared subgraph) — matches the pre-retyping
            // selector's "every valid candidate gets an entry" contract exactly.
            ulong retained = CapabilityBoundedGraphWalk.ComputeExclusiveRetained(sampleAddress, objectLookup, references, visited, maxBreadth, MaxDepth, cancellationToken);
            retainedByAddress[sampleAddress] = retained;
        }

        ulong totalEstimatedRetainedBytes = 0;
        for (int i = 0; i < topCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string typeName, ulong sampleAddress, int count, ulong totalSize, ulong lohSize, long gen2Count, _) = candidates[i];
            if (!retainedByAddress.TryGetValue(sampleAddress, out ulong retainedBytes))
                continue;

            totalEstimatedRetainedBytes += retainedBytes;

            ulong averageSize = count > 0 ? totalSize / (ulong)count : 0;
            // TODO: Track WasCapped from CapabilityBoundedGraphWalk when it exceeds breadth/depth limits.
            // For now, always false — future enhancement to propagate cap indicator from BFS traversal.
            topTypes.Add(new TypeSnapshot(
                typeName,
                count,
                totalSize,
                lohSize,
                AverageSize: averageSize,
                EstimatedRetainedBytes: retainedBytes,
                SampleAddress: sampleAddress,
                Gen2Count: gen2Count,
                WasCapped: false));
        }

        topTypes.Sort(static (a, b) => b.EstimatedRetainedBytes.CompareTo(a.EstimatedRetainedBytes));

        return new DominatorDomainResult(
            candidates.Count,
            topTypes.Count,
            totalEstimatedRetainedBytes,
            topTypes,
            MaxBreadth: maxBreadth,
            MaxDepth: MaxDepth,
            HighlyReferencedObjectCount: signals.HighlyReferencedObjectCount,
            ApproximatedReferenceAddresses: signals.ApproximatedReferenceAddresses,
            TopHighlyReferencedObjects: topHighlyReferencedObjects,
            ObjectScanCapped: signals.ObjectScanCapped,
            TopRetentionTypes: topRetentionTypes,
            TopHighlyReferencedTotalBytes: topHighlyReferencedTotalBytes,
            MaxTopDominatorTypesToShow: options.TopHighlyReferencedObjectsToShow,
            TotalHeapBytes: totalHeapBytes,
            FanInHistogram: signals.FanInHistogram);
    }

    // No-index fallback: the pipeline dispatcher only calls BeforeHeapIndexScan/OnHeapEntry when
    // an on-disk heap index exists. When it doesn't, this method runs the same reference-counting
    // pass directly over the live heap (or an in-memory index) via IHeapObjectStream, whose dump-side
    // implementation already prefers the disk index internally when one exists.
    private static LeakSignals AnalyzeObjectsPass(
        IHeapObjectStream heapObjects, IHeapObjectLookup objectLookup, IHeapReferenceQuery references, RetentionOptions options)
    {
        var referenceCount = new SpaceSavingCounter<ulong>(FallbackMaxReferenceAddresses);
        long approximatedReferenceAddresses = 0;
        bool objectScanCapped = false;

        // FallbackMaxLeakScanObjects caps the number of reference-enumeration calls, the primary
        // bottleneck on multi-GB dumps. 0 = unlimited. The cap applies to both disk and memory
        // index paths.
        int maxScan = FallbackMaxLeakScanObjects;
        long objectsTraced = 0;

        foreach (HeapObjectRef entry in heapObjects.EnumerateObjects())
        {
            ulong objectAddress = entry.Address;
            if (objectAddress == 0) continue;

            if (maxScan > 0 && objectsTraced >= maxScan)
            {
                objectScanCapped = true;
                break;
            }

            CountIncomingReferencesByAddress(references, objectAddress, referenceCount, ref approximatedReferenceAddresses);
            objectsTraced++;
        }

        IReadOnlyList<HighlyReferencedObjectSnapshot> topHighlyReferencedObjects = ExtractHighlyReferencedObjects(objectLookup, referenceCount, options);
        int highlyReferencedCount = CountHighlyReferencedObjects(referenceCount, options, out List<FanInBucket> fanInHistogram);

        return new LeakSignals(
            highlyReferencedCount,
            approximatedReferenceAddresses,
            topHighlyReferencedObjects,
            objectScanCapped,
            FanInHistogram: fanInHistogram);
    }

    private static int CountHighlyReferencedObjects(SpaceSavingCounter<ulong> referenceCount, RetentionOptions options, out List<FanInBucket> fanInHistogram)
    {
        // OPT-#8: Replace LINQ .Count(predicate) with a plain foreach to avoid boxed IEnumerator allocation.
        // Fan-in histogram is built in the same pass to avoid a second full-dictionary iteration.
        int threshold = options.HighReferenceThreshold;
        int count = 0;
        int[] fanInCounts = new int[s_fanInBuckets.Length];
        foreach ((ulong _, int entryCount, int _) in referenceCount.Entries)
        {
            if (entryCount > threshold)
                count++;
            AddToFanInHistogram(fanInCounts, entryCount);
        }
        fanInHistogram = BuildFanInHistogram(fanInCounts);
        return count;
    }

    // Admission into `referenceCount` (a SpaceSavingCounter) is order-independent by
    // construction — see docs/analysis/phase1/dominator-analyzer-audit.md Area 6 item 3 and
    // SpaceSavingCounter's own doc comment — so no address's increments are ever silently dropped
    // based on scan position.
    private static void CountIncomingReferencesByAddress(
        IHeapReferenceQuery references,
        ulong sourceAddress,
        SpaceSavingCounter<ulong> referenceCount,
        ref long approximatedReferenceAddresses)
    {
        if (sourceAddress == 0)
            return;

        foreach (ulong childAddress in references.EnumerateReferences(sourceAddress))
        {
            if (childAddress != 0 && referenceCount.Offer(childAddress))
                approximatedReferenceAddresses++;
        }
    }

    private static IReadOnlyList<HighlyReferencedObjectSnapshot> ExtractHighlyReferencedObjects(
        IHeapObjectLookup objectLookup, SpaceSavingCounter<ulong> referenceCount, RetentionOptions options)
    {
        int threshold = options.HighReferenceThreshold;
        // Heuristic: for small tables the LINQ-based path is faster (no heap overhead).
        const int LinqFastPathThreshold = 50_000;
        if (referenceCount.TrackedCount <= LinqFastPathThreshold)
        {
            var topAddresses = referenceCount.Entries
                .Where(e => e.Count > threshold)
                .OrderByDescending(e => e.Count)
                .Take(options.TopHighlyReferencedObjectsToShow)
                .ToArray();

            if (topAddresses.Length == 0)
                return Array.Empty<HighlyReferencedObjectSnapshot>();

            var results = new List<HighlyReferencedObjectSnapshot>(topAddresses.Length);
            foreach (var top in topAddresses)
            {
                HighlyReferencedObjectSnapshot? snapshot = CreateHighlyReferencedObjectSnapshot(objectLookup, top.Key, top.Count);
                if (snapshot is not null)
                    results.Add(snapshot);
            }

            return results;
        }

        // Use a fixed-size min-heap (PriorityQueue) to track the top K addresses by incoming reference count for large inputs.
        var pq = new PriorityQueue<(ulong Address, int Count), int>(options.TopHighlyReferencedObjectsToShow + 1);

        foreach ((ulong address, int entryCount, int _) in referenceCount.Entries)
        {
            if (entryCount <= threshold)
                continue;

            pq.Enqueue((address, entryCount), entryCount);
            if (pq.Count > options.TopHighlyReferencedObjectsToShow)
                pq.Dequeue();
        }

        if (pq.Count == 0)
            return Array.Empty<HighlyReferencedObjectSnapshot>();

        var buffer = new List<(ulong Address, int Count)>(pq.Count);
        while (pq.Count > 0)
            buffer.Add(pq.Dequeue());

        var final = new List<HighlyReferencedObjectSnapshot>(buffer.Count);
        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            HighlyReferencedObjectSnapshot? snapshot = CreateHighlyReferencedObjectSnapshot(objectLookup, buffer[i].Address, buffer[i].Count);
            if (snapshot is not null)
                final.Add(snapshot);
        }

        return final;
    }

    private static HighlyReferencedObjectSnapshot? CreateHighlyReferencedObjectSnapshot(IHeapObjectLookup objectLookup, ulong objectAddress, int incomingReferences)
    {
        if (objectAddress == 0)
            return null;

        if (!objectLookup.TryGetObject(objectAddress, out HeapObjectRef obj))
            return null;

        return new HighlyReferencedObjectSnapshot(obj.Address, obj.TypeDisplayName, obj.Size, incomingReferences);
    }

    private static void PopulateRetainedBytes(
        IHeapObjectLookup objectLookup, IHeapReferenceQuery references, List<HighlyReferencedObjectSnapshot> objects, RetentionOptions options, CancellationToken cancellationToken)
    {
        if (objects.Count == 0)
            return;

        var visited = new HashSet<ulong>(capacity: Math.Min(objects.Count * 4, 256));
        int maxBreadth = options.MaxLeakScanObjects > 0 ? options.MaxLeakScanObjects : 10_000;

        for (int i = 0; i < objects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HighlyReferencedObjectSnapshot snapshot = objects[i];
            if (!objectLookup.TryGetObject(snapshot.Address, out _))
                continue;

            ulong retained = CapabilityBoundedGraphWalk.ComputeExclusiveRetained(snapshot.Address, objectLookup, references, visited, maxBreadth, MaxDepth, cancellationToken);
            objects[i] = snapshot with { EstimatedRetainedBytes = retained };
        }
    }

    private static void PopulateEvidence(IHeapRootPathQuery rootPaths, List<HighlyReferencedObjectSnapshot> objects, RetentionOptions options)
    {
        if (objects.Count == 0)
            return;

        var searchOptions = new RootPathSearchOptions(
            options.MaxRootPathCandidateNodes, options.MaxRootPathCandidateDepth,
            options.MaxRootPathExpansionDepth, options.RootPathLargeFanoutThreshold);

        for (int i = 0; i < objects.Count; i++)
        {
            HighlyReferencedObjectSnapshot snapshot = objects[i];
            bool found = rootPaths.TryFindRootPath(snapshot.Address, searchOptions, out string? rootPath, out bool searchTruncated);

            objects[i] = snapshot with
            {
                Evidence = new Evidence(
                    snapshot.EstimatedRetainedBytes,
                    found ? rootPath : null,
                    searchTruncated,
                    [new EvidenceSignal("IncomingReferences", "Incoming reference count", snapshot.IncomingReferences)])
            };
        }
    }

    private static ulong SumTopHighlyReferencedBytes(IReadOnlyList<HighlyReferencedObjectSnapshot> objects)
    {
        ulong total = 0;
        for (int i = 0; i < objects.Count; i++)
            total += objects[i].Size;

        return total;
    }

    private static IReadOnlyList<RetentionTypeSnapshot> BuildTopRetentionTypes(IReadOnlyList<HighlyReferencedObjectSnapshot> objects)
    {
        if (objects.Count == 0)
            return Array.Empty<RetentionTypeSnapshot>();

        var byType = new Dictionary<string, RetentionTypeAccumulator>(StringComparer.Ordinal);
        for (int i = 0; i < objects.Count; i++)
        {
            HighlyReferencedObjectSnapshot obj = objects[i];
            if (byType.TryGetValue(obj.TypeName, out RetentionTypeAccumulator acc))
            {
                acc.ObjectCount++;
                acc.TotalBytes += obj.Size;
                acc.TotalIncomingReferences += obj.IncomingReferences;
                acc.EstimatedRetainedBytes += obj.EstimatedRetainedBytes;
                if (obj.IncomingReferences > acc.MaxIncomingReferences)
                    acc.MaxIncomingReferences = obj.IncomingReferences;
                byType[obj.TypeName] = acc;
            }
            else
            {
                byType[obj.TypeName] = new RetentionTypeAccumulator
                {
                    ObjectCount = 1,
                    TotalBytes = obj.Size,
                    TotalIncomingReferences = obj.IncomingReferences,
                    MaxIncomingReferences = obj.IncomingReferences,
                    EstimatedRetainedBytes = obj.EstimatedRetainedBytes
                };
            }
        }

        return byType.Select(kvp => new RetentionTypeSnapshot(
                TypeName: kvp.Key,
                ObjectCount: kvp.Value.ObjectCount,
                TotalBytes: kvp.Value.TotalBytes,
                TotalIncomingReferences: kvp.Value.TotalIncomingReferences,
                MaxIncomingReferences: kvp.Value.MaxIncomingReferences,
                EstimatedRetainedBytes: kvp.Value.EstimatedRetainedBytes))
            .OrderByDescending(static t => t.EstimatedRetainedBytes)
            .ThenByDescending(static t => t.TotalBytes)
            .ThenByDescending(static t => t.TotalIncomingReferences)
            .ToArray();
    }

    private static void AddToFanInHistogram(int[] fanInCounts, int incomingReferences)
    {
        for (int b = 0; b < s_fanInBuckets.Length; b++)
        {
            if (incomingReferences >= s_fanInBuckets[b].Min && incomingReferences < s_fanInBuckets[b].Max)
            {
                fanInCounts[b]++;
                return;
            }
        }
    }

    private static List<FanInBucket> BuildFanInHistogram(int[] fanInCounts)
    {
        var result = new List<FanInBucket>(s_fanInBuckets.Length);
        for (int b = 0; b < s_fanInBuckets.Length; b++)
            if (fanInCounts[b] > 0)
                result.Add(new FanInBucket(s_fanInBuckets[b].Label, fanInCounts[b]));
        return result;
    }

    private readonly record struct LeakSignals(
        int HighlyReferencedObjectCount,
        long ApproximatedReferenceAddresses,
        IReadOnlyList<HighlyReferencedObjectSnapshot> TopHighlyReferencedObjects,
        bool ObjectScanCapped,
        bool ReferenceCountingSkipped = false,
        IReadOnlyList<FanInBucket>? FanInHistogram = null);

    private struct RetentionTypeAccumulator
    {
        public int ObjectCount;
        public ulong TotalBytes;
        public long TotalIncomingReferences;
        public int MaxIncomingReferences;
        public ulong EstimatedRetainedBytes;
    }
}
