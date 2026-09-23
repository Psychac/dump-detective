using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

// DumpDetective.Analysis.Models and DumpDetective.Sdk.Analysis both declare HeapSegmentKind
// (deliberately identical names, see SdkSegmentKindMapper) — alias the SDK one to disambiguate.
using SdkHeapSegmentKind = DumpDetective.Sdk.Analysis.HeapSegmentKind;

namespace DumpDetective.Analysis.Analyzers.Gc;

/// <summary>
/// Phase 1 retyping batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// retyped onto the SDK's capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing handles
/// through <see cref="IHeapHandleQuery"/> (<c>heap.handles</c>), retained-byte estimates through
/// <see cref="IHeapDominatorQuery"/> (optional — null when Stage B wasn't built), per-address
/// SOH/generation classification through <see cref="IHeapSegmentQuery"/>, and object metadata
/// (dependent-handle source/target resolution, pinned-handle shallow size) through
/// <see cref="IHeapObjectLookup"/>. Runs through the existing pipeline via
/// <see cref="GCHandleAnalyzerLegacyAdapter"/>.
/// </summary>
[AnalyzerModule(key: "gc-handle", displayName: "GC Handle Analysis", order: 270, tags: ["handles"])]
[RequiresCapability(
    CapabilityVocabulary.HeapDominators,
    CapabilityVocabulary.HeapHandles,
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapSegments)]
[OptionalCapability(CapabilityVocabulary.TraceGcEvents)]
public sealed class GCHandleAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "GC Handle Analysis";
    public string Category => "Handles";

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapHandleQuery handleQuery = context.HeapHandles
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapHandles}' capability.");
        IHeapSegmentQuery segmentQuery = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");

        GCHandleAnalysisOptions options = context.AnalyzerOptions as GCHandleAnalysisOptions ?? new GCHandleAnalysisOptions();

        // context.HeapDominators is deliberately not required — null means Stage B wasn't built for
        // this run, and every retained-bytes consumer below degrades to the target's own shallow
        // size in that case, exactly like the pre-retyping analyzer's own null-treeProvider path.
        var result = Analyze(handleQuery, context.HeapDominators, segmentQuery, objectLookup, options, cancellationToken);
        LastResult = result;

        await EmitObservationsAsync(context, objectLookup, result, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): emits one
    /// <c>handle.pinned</c> observation per already-computed, already-bounded
    /// <see cref="GCHandleDomainResult.TopPinnedHandleAddresses"/> entry — the pinned-handle facts
    /// are the ones with a clean per-type entity anchor and a bounded (top-N, not per-handle) list
    /// already in the domain result. The many other aggregate facts on this result (weak-handle
    /// generation counts, dependent-handle resolution, ref-counted target types) have no comparably
    /// bounded per-entity shape yet; left for a later pass rather than invented here on a guess.
    /// </summary>
    private static async ValueTask EmitObservationsAsync(
        AnalysisContext context, IHeapObjectLookup objectLookup, GCHandleDomainResult result, CancellationToken cancellationToken)
    {
        var capabilitiesUsed = new HashSet<Capability>
        {
            CapabilityVocabulary.HeapHandles, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapSegments,
        };
        FidelityLevel fidelity = context.HeapDominators is not null ? FidelityLevel.Full : FidelityLevel.Degraded;

        foreach (PinnedHandleAddressEntry entry in result.TopPinnedHandleAddresses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-resolves the same (cache-backed) address rather than threading a TypeRef through
            // PinnedHandleAddressEntry — that record is presentation-shaped (a display type name),
            // and SdkTypeRefFactory (which built it the first time, inside Analyze) is internal to
            // DumpDetective.Analysis, not visible from this plugin assembly.
            if (!objectLookup.TryGetObject(entry.Address, out HeapObjectRef targetObj))
                continue;

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "handle.pinned",
                Subjects = [targetObj.Type],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["retained.bytes"] = new Measure(entry.Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "GC Handle Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = fidelity,
                },
                Confidence = result.PinnedRetainedBytesIsExact ? 1.0 : 0.7,
            }, cancellationToken);
        }

        // Whole-run scalar — no entity subject, same "no natural per-entity anchor" pattern as
        // gc.generation-composition. Backs every finding-generator signal handle.pinned cannot:
        // it is capped to GCHandleAnalysisOptions.TopPinnedHandleAddressesToShow, while these
        // aggregates are computed over the *entire* handle population. Also carries the analyzer's
        // configurable warning thresholds (GCHandleAnalysisOptions, threaded through the domain
        // result) rather than hard-coding them into the synthesis rule as private consts the way
        // other rules do — unlike those analyzers' fixed generator constants, these are genuinely
        // runtime-configurable via AnalysisContext.AnalyzerOptions, and hard-coding defaults into
        // the rule would silently break that configurability.
        await context.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "handle.population",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["total.count"] = new Measure(result.TotalHandles, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["strong-like.count"] = new Measure(result.StrongLikeHandles, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["pinned-target.count"] = new Measure(result.PinnedHandleTargets, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["pinned-retained.bytes"] = new Measure(result.PinnedRetainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["pinned-soh.count"] = new Measure(result.PinnedSohObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["async-pinned-soh.count"] = new Measure(result.AsyncPinnedSohObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["pinned-non-soh.count"] = new Measure(result.PinnedNonSohObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["async-pinned-non-soh.count"] = new Measure(result.AsyncPinnedNonSohObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["refcounted.count"] = new Measure(result.RefCountedHandleCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["weakshort-gen0.count"] = new Measure(result.WeakShortGen0Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["weakshort-gen1.count"] = new Measure(result.WeakShortGen1Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["weakshort-gen2.count"] = new Measure(result.WeakShortGen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["weakshort-loh.count"] = new Measure(result.WeakShortLohCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["weaklong-gen0.count"] = new Measure(result.WeakLongGen0Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["weaklong-gen1.count"] = new Measure(result.WeakLongGen1Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["weaklong-gen2.count"] = new Measure(result.WeakLongGen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["weaklong-loh.count"] = new Measure(result.WeakLongLohCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["dependent.count"] = new Measure(result.DependentHandleCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["dependent-resolved-edge.count"] = new Measure(result.DependentResolvedEdgeCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["dependent-unresolved-target.count"] = new Measure(result.DependentUnresolvedTargetCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["dependent-unresolved.percent"] = new Measure(result.DependentUnresolvedPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
                ["total-handles-warning.threshold"] = new Measure(result.TotalHandlesWarningThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["pinned-handle-targets-warning.threshold"] = new Measure(result.PinnedHandleTargetsWarningThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["pinned-retained-bytes-warning.threshold"] = new Measure(result.PinnedRetainedBytesWarningThreshold, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["pinned-soh-warning.threshold"] = new Measure(result.PinnedSohObjectCountWarningThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["refcounted-warning.threshold"] = new Measure(result.RefCountedHandleCountWarningThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["weaklong-gen2-fraction-warning.threshold"] = new Measure(result.WeakLongGen2FractionWarningThreshold, MeasureUnit.Percent, MeasureSemantics.Ratio),
                ["weaklong-gen2-minimum-count.threshold"] = new Measure(result.WeakLongGen2MinimumCountThreshold, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["dependent-unresolved-percent-warning.threshold"] = new Measure(result.DependentUnresolvedPercentWarningThreshold, MeasureUnit.Percent, MeasureSemantics.Ratio),
            },
            Provenance = new Provenance
            {
                Artifact = context.ArtifactId!.Value,
                AnalyzerKey = "GC Handle Analysis",
                CapabilitiesUsed = capabilitiesUsed,
                Fidelity = fidelity,
            },
            Confidence = 1.0,
        }, cancellationToken);

        // Per-type RefCounted (COM interop RCW) target population — bounded by distinct target
        // type count, not per-handle. Canonicalized from the display name the same way
        // FinalizableObjectAnalyzer's type.finalizable-population and BoxingAnalyzer's type.boxing
        // are: no per-handle address survives into TopRefCountedTargetTypes to re-resolve a real
        // TypeRef from, only a display name.
        foreach (NameCountEntry entry in result.TopRefCountedTargetTypes ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string canonicalName, MatchFidelity typeFidelity) = EntityCanonicalizer.CanonicalizeTypeName(entry.Name);

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "handle.refcounted-target",
                Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = typeFidelity }],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(entry.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "GC Handle Analysis",
                    CapabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapHandles },
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }

        // Per-type Pinned/Dependent target population (phase 5 step 6.1,
        // docs/refactor/modularity/phase-5-step6-insight-engine-plan.md) — same shape as
        // TopRefCountedTargetTypes above, added specifically so LeakCandidateSynthesisRule can
        // determine per-type isPinned/isDependent membership from observations instead of the
        // CompletedRunResults cross-reference LeakCandidateAnalyzer used before this. Before this,
        // handle.pinned only existed at the per-address level (TopPinnedHandleAddresses, bounded to
        // GCHandleAnalysisOptions.TopPinnedHandleAddressesToShow), which is too narrow a population
        // for a consumer that needs "every pinned target type", not just the top few addresses.
        foreach (NameCountEntry entry in result.TopPinnedTargetTypes ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string canonicalName, MatchFidelity typeFidelity) = EntityCanonicalizer.CanonicalizeTypeName(entry.Name);

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "handle.pinned-target-type",
                Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = typeFidelity }],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(entry.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "GC Handle Analysis",
                    CapabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapHandles },
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }

        // Phase 5 step 6.2 (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md):
        // PinnedStringLeakInsightRule needs per-type pinned *bytes*, which handle.pinned-target-type
        // above doesn't carry (count only) and handle.pinned can't safely stand in for — it's capped
        // to GCHandleAnalysisOptions.TopPinnedHandleAddressesToShow addresses, so summing it by type
        // would silently undercount. TopPinnedObjectsBySize/TopAsyncPinnedObjectsBySize are their own
        // separate top-N-by-bytes rankings (not the same entry set as TopPinnedTargetTypes — deliberately
        // not index-zipped with it), so this emits its own observation rather than trying to align two
        // independently-sorted lists.
        await EmitPinnedTargetBytesAsync(context, result.TopPinnedObjectsBySize, cancellationToken);
        await EmitPinnedTargetBytesAsync(context, result.TopAsyncPinnedObjectsBySize, cancellationToken);

        foreach (NameCountEntry entry in result.DependentTopTargetTypes ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string canonicalName, MatchFidelity typeFidelity) = EntityCanonicalizer.CanonicalizeTypeName(entry.Name);

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "handle.dependent-target-type",
                Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = typeFidelity }],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(entry.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "GC Handle Analysis",
                    CapabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapHandles },
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }
    }

    private static async ValueTask EmitPinnedTargetBytesAsync(
        AnalysisContext context, IReadOnlyList<NameBytesEntry>? entries, CancellationToken cancellationToken)
    {
        foreach (NameBytesEntry entry in entries ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string canonicalName, MatchFidelity typeFidelity) = EntityCanonicalizer.CanonicalizeTypeName(entry.Name);

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "handle.pinned-target-bytes",
                Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = typeFidelity }],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["retained.bytes"] = new Measure(entry.Bytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "GC Handle Analysis",
                    CapabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapHandles },
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }
    }

    private static GCHandleDomainResult Analyze(
        IHeapHandleQuery handleQuery,
        IHeapDominatorQuery? dominatorQuery,
        IHeapSegmentQuery segmentQuery,
        IHeapObjectLookup objectLookup,
        GCHandleAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        int pinnedExactCount = 0, pinnedFallbackCount = 0;
        int asyncPinnedExactCount = 0, asyncPinnedFallbackCount = 0;

        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var pinnedTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        // P2-2: RefCounted handles back COM interop (RCW) lifetime; concentration by target type
        // surfaces COM object leaks that would otherwise be invisible in the handle count.
        var refCountedTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        // P2-4: individual pinned-handle addresses for debugger follow-up. Bounded by pinned handle
        // count (already the scope of the existing per-handle byte resolution above), not heap
        // object count, so collecting the full set before ranking is cheap.
        var pinnedHandleAddresses = new List<PinnedHandleAddressEntry>();
        var pinnedBytesByType = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var asyncPinnedBytesByType = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var allTargetTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var nullTargetHandlesByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        ulong totalPinnedRetainedBytes = 0;
        ulong totalAsyncPinnedRetainedBytes = 0;
        // P2-1: SOH targets keep the GC from compacting around them; LOH/POH/Frozen targets don't
        // (LOH is never compacted, POH objects are already pinned by construction), so only the SOH
        // count signals an actionable compaction barrier.
        int pinnedSohObjectCount = 0;
        int pinnedNonSohObjectCount = 0;
        int asyncPinnedSohObjectCount = 0;
        int asyncPinnedNonSohObjectCount = 0;
        // P3-2: WeakShort clears when the target becomes unreachable, even mid-finalization;
        // WeakLong clears only after finalization completes. A WeakLong population concentrated in
        // Gen2/LOH can indicate a finalization backlog (targets lingering, weakly-referenced,
        // waiting for their finalizer to run).
        int weakShortGen0Count = 0, weakShortGen1Count = 0, weakShortGen2Count = 0, weakShortLohCount = 0;
        int weakLongGen0Count = 0, weakLongGen1Count = 0, weakLongGen2Count = 0, weakLongLohCount = 0;

        int totalHandles = 0;
        int strongLikeHandles = 0;
        int weakLikeHandles = 0;
        // Never increments — ResolveTypeNameFromRecord's contract (see IHeapHandleQuery.HeapHandleRef's
        // own remarks) guarantees a non-empty TargetTypeDisplayName for any non-zero TargetAddress,
        // so this branch is unreachable. Preserved as a pre-existing, always-zero metric rather than
        // "fixed" during a retyping-only change.
        int unknownTargetCount = 0;

        int dependentHandleCount = 0;
        int dependentResolvedEdgeCount = 0;
        int dependentUnresolvedTargetCount = 0;
        var dependentSourceTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependentTargetTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependentSourceTargetPairCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        int handlesProcessed = 0;
        foreach (HeapHandleRef handle in handleQuery.EnumerateHandles())
        {
            handlesProcessed++;
            if ((handlesProcessed % 1000) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            totalHandles++;
            string kind = handle.Kind.ToString();
            Increment(byKind, kind);

            if (IsWeakLike(kind))
                weakLikeHandles++;
            else
                strongLikeHandles++;

            bool isDependent = kind == "Dependent";
            if (isDependent)
                dependentHandleCount++;

            // P1-3: Track null-target handles per kind
            if (handle.TargetAddress == 0)
            {
                Increment(nullTargetHandlesByKind, kind);
                if (isDependent)
                    dependentUnresolvedTargetCount++;
                continue;
            }

            string typeName = handle.TargetTypeDisplayName;
            Increment(allTargetTypes, typeName);

            // P1-2: Separate AsyncPinned vs Pinned byte accounting
            if (kind == "AsyncPinned")
            {
                if (TryIsSoh(segmentQuery, handle.TargetAddress, out bool isSohAsync))
                {
                    if (isSohAsync) asyncPinnedSohObjectCount++;
                    else asyncPinnedNonSohObjectCount++;
                }

                ulong resolvedSize;
                if (dominatorQuery is not null && dominatorQuery.TryGetRetainedSize(handle.TargetAddress, out ulong exactAsyncPinnedBytes))
                {
                    resolvedSize = exactAsyncPinnedBytes;
                    asyncPinnedExactCount++;
                }
                else
                {
                    resolvedSize = ResolveSize(objectLookup, handle.TargetAddress);
                    asyncPinnedFallbackCount++;
                }

                if (resolvedSize > 0)
                {
                    totalAsyncPinnedRetainedBytes += resolvedSize;
                    asyncPinnedBytesByType[typeName] = asyncPinnedBytesByType.TryGetValue(typeName, out ulong existingBytes) ? existingBytes + resolvedSize : resolvedSize;
                    pinnedHandleAddresses.Add(new PinnedHandleAddressEntry(handle.TargetAddress, typeName, resolvedSize, kind));
                }
            }
            else if (kind == "Pinned")
            {
                Increment(pinnedTypes, typeName);

                if (TryIsSoh(segmentQuery, handle.TargetAddress, out bool isSohPinned))
                {
                    if (isSohPinned) pinnedSohObjectCount++;
                    else pinnedNonSohObjectCount++;
                }

                ulong resolvedSize;
                if (dominatorQuery is not null && dominatorQuery.TryGetRetainedSize(handle.TargetAddress, out ulong exactPinnedBytes))
                {
                    resolvedSize = exactPinnedBytes;
                    pinnedExactCount++;
                }
                else
                {
                    resolvedSize = ResolveSize(objectLookup, handle.TargetAddress);
                    pinnedFallbackCount++;
                }

                if (resolvedSize > 0)
                {
                    totalPinnedRetainedBytes += resolvedSize;
                    pinnedBytesByType[typeName] = pinnedBytesByType.TryGetValue(typeName, out ulong existingBytes) ? existingBytes + resolvedSize : resolvedSize;
                    pinnedHandleAddresses.Add(new PinnedHandleAddressEntry(handle.TargetAddress, typeName, resolvedSize, kind));
                }
            }
            else if (kind == "RefCounted")
            {
                Increment(refCountedTypes, typeName);
            }
            else if (isDependent)
            {
                if (!TryResolveDependentTypeName(objectLookup, handle.TargetAddress, out string sourceType))
                {
                    dependentUnresolvedTargetCount++;
                }
                else
                {
                    Increment(dependentSourceTypeCounts, sourceType);

                    ulong dependentTarget = handle.DependentTargetAddress ?? 0;
                    if (dependentTarget == 0 || !TryResolveDependentTypeName(objectLookup, dependentTarget, out string dependentTargetType))
                    {
                        dependentUnresolvedTargetCount++;
                    }
                    else
                    {
                        dependentResolvedEdgeCount++;
                        Increment(dependentTargetTypeCounts, dependentTargetType);
                        Increment(dependentSourceTargetPairCounts, $"{sourceType} -> {dependentTargetType}");
                    }
                }
            }
            else if (kind == "WeakShort" || kind == "WeakLong")
            {
                // P3-2: generation breakdown for weak handle targets. Loh/Poh/Frozen all fold into
                // the single "Loh" catch-all bucket, matching the pre-retyping analyzer's own
                // ResolveGeneration-based "else" bucket.
                switch (segmentQuery.GetGeneration(handle.TargetAddress))
                {
                    case HeapGenerationTag.Unknown:
                        break; // Unresolvable segment — no bucket to attribute to.
                    case HeapGenerationTag.Gen0:
                        if (kind == "WeakShort") weakShortGen0Count++; else weakLongGen0Count++;
                        break;
                    case HeapGenerationTag.Gen1:
                        if (kind == "WeakShort") weakShortGen1Count++; else weakLongGen1Count++;
                        break;
                    case HeapGenerationTag.Gen2:
                        if (kind == "WeakShort") weakShortGen2Count++; else weakLongGen2Count++;
                        break;
                    default: // Loh, Poh, Frozen
                        if (kind == "WeakShort") weakShortLohCount++; else weakLongLohCount++;
                        break;
                }
            }
        }

        int pinnedHandleTargets = 0;
        foreach (int v in pinnedTypes.Values) pinnedHandleTargets += v;
        int refCountedHandleCount = 0;
        foreach (int v in refCountedTypes.Values) refCountedHandleCount += v;
        double dependentUnresolvedPercent = dependentHandleCount == 0 ? 0
            : dependentUnresolvedTargetCount * 100.0 / dependentHandleCount;

        // P2-4: rank the exact set of collected pinned-handle addresses by bytes, keep top N for display.
        pinnedHandleAddresses.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        int topPinnedAddressCount = Math.Min(options.TopPinnedHandleAddressesToShow, pinnedHandleAddresses.Count);
        var topPinnedHandleAddresses = pinnedHandleAddresses.GetRange(0, topPinnedAddressCount);

        return new GCHandleDomainResult(
            totalHandles,
            strongLikeHandles,
            weakLikeHandles,
            pinnedHandleTargets,
            ToRankedEntries(byKind),
            ToRankedEntries(allTargetTypes),
            ToRankedEntries(pinnedTypes),
            totalPinnedRetainedBytes,
            ToRankedByteEntries(pinnedBytesByType),
            totalAsyncPinnedRetainedBytes,
            ToRankedByteEntries(asyncPinnedBytesByType),
            ToRankedEntries(nullTargetHandlesByKind),
            unknownTargetCount,
            dependentHandleCount,
            dependentResolvedEdgeCount,
            dependentUnresolvedTargetCount,
            dependentUnresolvedPercent,
            ToRankedEntries(dependentSourceTypeCounts),
            ToRankedEntries(dependentTargetTypeCounts),
            ToRankedEntries(dependentSourceTargetPairCounts),
            PinnedRetainedBytesIsExact: pinnedExactCount > 0 && pinnedFallbackCount == 0,
            AsyncPinnedRetainedBytesIsExact: asyncPinnedExactCount > 0 && asyncPinnedFallbackCount == 0,
            PinnedSohObjectCount: pinnedSohObjectCount,
            PinnedNonSohObjectCount: pinnedNonSohObjectCount,
            AsyncPinnedSohObjectCount: asyncPinnedSohObjectCount,
            AsyncPinnedNonSohObjectCount: asyncPinnedNonSohObjectCount,
            RefCountedHandleCount: refCountedHandleCount,
            TopRefCountedTargetTypes: ToRankedEntries(refCountedTypes),
            TopPinnedHandleAddresses: topPinnedHandleAddresses,
            WeakShortGen0Count: weakShortGen0Count,
            WeakShortGen1Count: weakShortGen1Count,
            WeakShortGen2Count: weakShortGen2Count,
            WeakShortLohCount: weakShortLohCount,
            WeakLongGen0Count: weakLongGen0Count,
            WeakLongGen1Count: weakLongGen1Count,
            WeakLongGen2Count: weakLongGen2Count,
            WeakLongLohCount: weakLongLohCount,
            TotalHandlesWarningThreshold: options.TotalHandlesWarningThreshold,
            PinnedHandleTargetsWarningThreshold: options.PinnedHandleTargetsWarningThreshold,
            PinnedRetainedBytesWarningThreshold: options.PinnedRetainedBytesWarningThreshold,
            PinnedSohObjectCountWarningThreshold: options.PinnedSohObjectCountWarningThreshold,
            RefCountedHandleCountWarningThreshold: options.RefCountedHandleCountWarningThreshold,
            WeakLongGen2FractionWarningThreshold: options.WeakLongGen2FractionWarningThreshold,
            WeakLongGen2MinimumCountThreshold: options.WeakLongGen2MinimumCountThreshold,
            DependentUnresolvedPercentWarningThreshold: options.DependentUnresolvedPercentWarningThreshold);
    }

    private static bool IsWeakLike(string kind) => kind.Contains("Weak", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves whether <paramref name="address"/> lives on the small object heap via a
    /// single segment lookup (bounded by pinned-handle count, not heap size).</summary>
    private static bool TryIsSoh(IHeapSegmentQuery segmentQuery, ulong address, out bool isSoh)
    {
        if (!segmentQuery.TryGetSegment(address, out HeapSegmentRef segment))
        {
            isSoh = false;
            return false;
        }

        isSoh = segment.Kind == SdkHeapSegmentKind.SmallObjectHeap;
        return true;
    }

    private static ulong ResolveSize(IHeapObjectLookup objectLookup, ulong address) =>
        objectLookup.TryGetObject(address, out HeapObjectRef obj) ? obj.Size : 0;

    /// <summary>
    /// Dependent-handle source/target resolution — deliberately not a plain
    /// <see cref="IHeapObjectLookup.TryGetObject"/> passthrough: that method still returns
    /// <c>true</c> (with a placeholder name) when the resolved method table is 0, but the
    /// pre-retyping analyzer's own <c>TryResolveTypeNameStrict</c> treated a zero method table as
    /// unresolved — preserved here exactly, since it changes whether an edge counts toward
    /// <see cref="GCHandleDomainResult.DependentResolvedEdgeCount"/> or
    /// <see cref="GCHandleDomainResult.DependentUnresolvedTargetCount"/>.
    /// </summary>
    private static bool TryResolveDependentTypeName(IHeapObjectLookup objectLookup, ulong address, out string typeName)
    {
        typeName = "Unknown";
        if (address == 0)
            return false;

        if (!objectLookup.TryGetObject(address, out HeapObjectRef obj))
            return false;

        if (obj.Type.MethodTable is null or 0)
            return false;

        typeName = obj.TypeDisplayName;
        return true;
    }

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out int value) ? value + 1 : 1;

    private static List<NameCountEntry> ToRankedEntries(Dictionary<string, int> source)
    {
        var list = new List<NameCountEntry>(source.Count);
        foreach (var kvp in source)
            list.Add(new NameCountEntry(kvp.Key, kvp.Value));
        list.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        return list;
    }

    private static List<NameBytesEntry> ToRankedByteEntries(Dictionary<string, ulong> source)
    {
        var list = new List<NameBytesEntry>(source.Count);
        foreach (var kvp in source)
            list.Add(new NameBytesEntry(kvp.Key, kvp.Value));
        list.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
        return list;
    }

    public void Dispose() { }
}
