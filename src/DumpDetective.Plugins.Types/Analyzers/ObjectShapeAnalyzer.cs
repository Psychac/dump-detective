using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Types;

/// <summary>
/// Pure Phase-2 type-metadata analyzer: no heap object enumeration. Reads the dump's per-type shape
/// cache and joins it with per-type aggregates for instance counts. Classifies types as
/// ReferenceHeavy / ValueHeavy / Balanced / Scalar and ranks by (referenceFieldRatio × instanceCount)
/// to surface GC-scan-cost hotspots.
/// </summary>
/// <remarks>
/// Tier-1 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): originally
/// grep-classified as Tier-1+Tier-2, corrected on inspection — this analyzer never reads a field
/// *value*, only field *shape* (counts/ref-vs-value split from the type's own metadata), which is a
/// coarse, structural fact exactly like everything else <see cref="IHeapTypeStatisticsQuery"/>
/// already exposes. Extended that interface with <c>TotalFields</c>/<c>ReferenceFields</c>/
/// <c>ValueFields</c>/<c>IsValueType</c>/<c>IsArray</c>/<c>BaseTypeChainDepth</c>/<c>InterfaceCount</c>
/// rather than adding a new capability. The pre-retyping analyzer's optional <c>ILogger</c>
/// constructor parameter (DI-resolved by the old pipeline for interface-enumeration failure
/// diagnostics) has no equivalent here — the retyped inner analyzer is constructed directly by its
/// adapter, not through DI, matching every other Tier-1/Tier-2 retyping so far; the failure path
/// itself (see <c>HeapTypeStatisticsQuery.BuildFromIndex</c>) is preserved, just silent.
/// </remarks>
[AnalyzerModule(key: "object-shape", displayName: "Object Shape Analysis", order: 130, tags: ["types"])]
[RequiresCapability(CapabilityVocabulary.HeapTypes)]
public sealed class ObjectShapeAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "Object Shape Analysis";
    public string Category => "Memory";
    public IReadOnlyCollection<string> Tags => ["gc", "object-shape", "memory", "gc-scan"];

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    public async ValueTask AnalyzeAsync(Sdk.Analysis.AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");

        var result = Analyze(typeStatistics);
        LastResult = result;

        if (result.TotalTypesAnalyzed > 0)
            await EmitObservationAsync(context, result, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): a single
    /// whole-heap <c>type.shape-composition</c> observation carries the plain scalars.
    /// <c>TopReferenceHeavyTypes</c>/<c>TopValueHeavyTypes</c>/<c>TopBalancedTypes</c>/
    /// <c>TopGen2RetainedTypes</c> themselves are still excluded wholesale: each is the *entire*
    /// whole-heap type population partitioned by <see cref="TypeShapeProfile.Category"/>, a
    /// hand-picked-threshold classification (refRatio &gt; 0.6 / &lt; 0.2) — both the
    /// unbounded-volume risk <c>GCGenerationAnalyzer</c>'s remarks flag and the Smell A/B judgment
    /// purity rule this phase exists to keep out of observations.
    ///
    /// What the old finding generator actually reads off those lists, though, is a handful of
    /// specific, already-selected entries — the same "port the selection, not the raw population"
    /// move <c>GCGenerationAnalyzer.SelectTopTypes</c> made. Three new per-type observation types
    /// carry exactly that selection, each reusing <c>TopReferenceHeavyTypes</c>/<c>TopValueHeavyTypes</c>'s
    /// own pre-existing sort order (already the analyzer's own ranking, not re-derived here):
    /// <c>type.reference-heavy</c> (top 3 by GC-scan-cost score), <c>type.value-heavy</c> (the single
    /// top type by total heap size), and <c>type.finalizable-reference-heavy</c> (every finalizable
    /// reference-heavy type with ≥10K instances — filtered, not top-N-capped, since the old
    /// generator needs an *exact* count of this narrow, distinct-type-scale population, the same
    /// "exact accumulator, not a capped derivation" reasoning <c>CollectionAnalyzer</c>'s waste
    /// dictionaries needed).
    /// </summary>
    private static async ValueTask EmitObservationAsync(Sdk.Analysis.AnalysisContext context, ObjectShapeAnalyzerDomainResult result, CancellationToken cancellationToken)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes };

        await context.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "type.shape-composition",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["type.count"] = new Measure(result.TotalTypesAnalyzed, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["avg-reference-fields"] = new Measure(result.AvgRefFieldsPerType, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["gc-scan-work"] = new Measure(result.TotalGcScanWork, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["gen2-gc-scan-work"] = new Measure(result.TotalGen2GcScanWork, MeasureUnit.Count, MeasureSemantics.Absolute),
                // Exact totals for the two classification populations — not derivable by counting
                // matched type.reference-heavy/type.value-heavy observations, which are capped to
                // the top 3/top 1 (see EmitTypeShapeObservationAsync below), unlike the old
                // generator's own "{Count} reference-heavy types found" text.
                ["reference-heavy-type.count"] = new Measure(result.TopReferenceHeavyTypes.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["value-heavy-type.count"] = new Measure(result.TopValueHeavyTypes.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = new Provenance
            {
                Artifact = context.ArtifactId!.Value,
                AnalyzerKey = "Object Shape Analysis",
                CapabilitiesUsed = capabilitiesUsed,
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);

        foreach (TypeShapeProfile profile in result.TopReferenceHeavyTypes.Take(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EmitTypeShapeObservationAsync(context, "type.reference-heavy", profile, capabilitiesUsed, cancellationToken);
        }

        if (result.TopValueHeavyTypes.Count > 0)
            await EmitTypeShapeObservationAsync(context, "type.value-heavy", result.TopValueHeavyTypes[0], capabilitiesUsed, cancellationToken);

        foreach (TypeShapeProfile profile in result.TopReferenceHeavyTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (profile.IsFinalizable && profile.InstanceCount >= 10_000)
                await EmitTypeShapeObservationAsync(context, "type.finalizable-reference-heavy", profile, capabilitiesUsed, cancellationToken);
        }
    }

    private static async ValueTask EmitTypeShapeObservationAsync(
        Sdk.Analysis.AnalysisContext context, string observationType, TypeShapeProfile profile,
        HashSet<Capability> capabilitiesUsed, CancellationToken cancellationToken)
    {
        (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(profile.TypeName);

        await context.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = observationType,
            Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity }],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["reference-field.count"] = new Measure(profile.ReferenceFields, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["total-field.count"] = new Measure(profile.TotalFields, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["instance.count"] = new Measure(profile.InstanceCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = new Provenance
            {
                Artifact = context.ArtifactId!.Value,
                AnalyzerKey = "Object Shape Analysis",
                CapabilitiesUsed = capabilitiesUsed,
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);
    }

    private static ObjectShapeAnalyzerDomainResult Analyze(IHeapTypeStatisticsQuery typeStatistics)
    {
        if (!typeStatistics.HasExactGenerationData || !typeStatistics.HasTypeShapeData)
            return Empty();

        var refHeavyCandidates = new List<TypeShapeProfile>();
        var valHeavyCandidates = new List<TypeShapeProfile>();
        var balancedCandidates = new List<TypeShapeProfile>();

        long totalRefFields = 0;
        long totalGcScanWork = 0;
        long totalGen2GcScanWork = 0;
        int typesAnalyzed = 0;

        foreach (KeyValuePair<string, HeapTypeStatistics> kv in typeStatistics.GetTypeStatistics())
        {
            string typeName = kv.Key; // raw resolved display name -- not stats.Type.CanonicalName,
                                       // which is a rewritten cross-source join key (Batch 2's own
                                       // HeapObjectRef.TypeDisplayName precedent applies here too).
            HeapTypeStatistics stats = kv.Value;
            typesAnalyzed++;
            totalRefFields += stats.ReferenceFields;
            totalGcScanWork += (long)(stats.ReferenceFields * (double)Math.Max(0, stats.InstanceCount));

            ulong gen2InstanceCount = (ulong)Math.Max(0, stats.Gen2Count);
            totalGen2GcScanWork += (long)(stats.ReferenceFields * (double)gen2InstanceCount);

            double refRatio = stats.TotalFields > 0
                ? stats.ReferenceFields * 1.0 / stats.TotalFields
                : 0.0;

            ObjectShapeCategory category = stats.TotalFields == 0
                ? ObjectShapeCategory.Scalar
                : refRatio > 0.6
                    ? ObjectShapeCategory.ReferenceHeavy
                    : refRatio < 0.2
                        ? ObjectShapeCategory.ValueHeavy
                        : ObjectShapeCategory.Balanced;

            var profile = new TypeShapeProfile(
                TypeName: typeName,
                TotalFields: stats.TotalFields,
                ReferenceFields: stats.ReferenceFields,
                ValueFields: stats.ValueFields,
                ReferenceFieldRatio: refRatio,
                InstanceCount: (ulong)Math.Max(0, stats.InstanceCount),
                TotalSize: stats.TotalSize,
                IsFinalizable: stats.IsFinalizableType,
                IsValueType: stats.IsValueType,
                IsArray: stats.IsArray,
                BaseTypeChainDepth: stats.BaseTypeChainDepth,
                InterfaceCount: stats.InterfaceCount,
                Category: category,
                Gen2InstanceCount: gen2InstanceCount);

            if (category == ObjectShapeCategory.ReferenceHeavy)
                refHeavyCandidates.Add(profile);
            else if (category == ObjectShapeCategory.ValueHeavy)
                valHeavyCandidates.Add(profile);
            else if (category == ObjectShapeCategory.Balanced)
                balancedCandidates.Add(profile);
        }

        // Sort by GC scan cost score (refRatio x instanceCount) in descending order
        refHeavyCandidates.Sort(static (a, b) =>
            (b.ReferenceFieldRatio * (double)b.InstanceCount)
                .CompareTo(a.ReferenceFieldRatio * (double)a.InstanceCount));

        // Sort value-heavy types by total heap size (more impactful types first)
        valHeavyCandidates.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));

        // Sort balanced types by instance count (most populous first)
        balancedCandidates.Sort(static (a, b) => b.InstanceCount.CompareTo(a.InstanceCount));

        // Retention-adjusted ranking: RefFields x Gen2InstanceCount rather than total InstanceCount,
        // since Gen2 objects are rescanned on every full GC while Gen0/Gen1 objects are collected
        // (and thus scanned) far more cheaply.
        var gen2RetainedCandidates = new List<TypeShapeProfile>(
            refHeavyCandidates.Count + valHeavyCandidates.Count + balancedCandidates.Count);
        gen2RetainedCandidates.AddRange(refHeavyCandidates);
        gen2RetainedCandidates.AddRange(valHeavyCandidates);
        gen2RetainedCandidates.AddRange(balancedCandidates);
        gen2RetainedCandidates.Sort(static (a, b) =>
            (b.ReferenceFields * (double)b.Gen2InstanceCount)
                .CompareTo(a.ReferenceFields * (double)a.Gen2InstanceCount));

        double avgRefFields = typesAnalyzed > 0 ? totalRefFields * 1.0 / typesAnalyzed : 0.0;

        return new ObjectShapeAnalyzerDomainResult(
            TopReferenceHeavyTypes: refHeavyCandidates,
            TopValueHeavyTypes: valHeavyCandidates,
            TopBalancedTypes: balancedCandidates,
            TotalTypesAnalyzed: typesAnalyzed,
            AvgRefFieldsPerType: avgRefFields,
            TotalGcScanWork: totalGcScanWork,
            TopGen2RetainedTypes: gen2RetainedCandidates,
            TotalGen2GcScanWork: totalGen2GcScanWork);
    }

    private static ObjectShapeAnalyzerDomainResult Empty() => new(
        TopReferenceHeavyTypes: [],
        TopValueHeavyTypes: [],
        TopBalancedTypes: [],
        TotalTypesAnalyzed: 0,
        AvgRefFieldsPerType: 0,
        TotalGcScanWork: 0,
        TopGen2RetainedTypes: [],
        TotalGen2GcScanWork: 0);

    public void Dispose() { }
}
