using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Types;

/// <summary>
/// Covers array population, large arrays, sparse/wasteful arrays, jagged vs multi-dimensional
/// analysis, and pinned array detection.
///
/// Population aggregate reads per-type array-shape facts (<see cref="HeapTypeStatistics.IsArray"/>)
/// — no heap scan for basic totals. Large array analysis reads the Phase-1 captured-large-object
/// sample (top-100 LOH objects) and cross-references with array-flagged types; falls back to
/// per-type LOH aggregates in memory mode. Sparse sampling walks every element of every array with
/// at least <see cref="ArrayAnalysisOptions.SparseSampleMinLength"/> elements to compute exact
/// null/zero density. Pinned array detection cross-references pinned/async-pinned roots against the
/// array-type set built in Step 1 — every pinned array instance is checked exactly, not sampled.
/// </summary>
/// <remarks>
/// Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): the one
/// genuinely new capability this retyping needed is array *element* access
/// (<see cref="IDumpArrayShapeQuery"/>, <c>dump.array-elements</c>) — not a "third tier" distinct
/// from Tier 1/Tier 2, just a second dump-only capability alongside <c>dump.object-fields</c>, since
/// array contents are exactly as dump-only-forever as field values. Everything else (population,
/// large-array satellite reads, pinned-root cross-referencing) reuses <see cref="IHeapTypeStatisticsQuery"/>
/// (extended with array-shape fields), <see cref="IHeapSegmentQuery.EnumerateCapturedLargeObjects"/>,
/// and <see cref="IHeapRootQuery"/> unchanged — no scan-participant needed at all, unlike the Infra
/// batch's typed-resource-quartet analyzers.
/// </remarks>
[AnalyzerModule(key: "array", displayName: "Array Analysis", order: 360, tags: ["types"])]
[RequiresCapability(
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapRoots,
    CapabilityVocabulary.HeapSegments,
    CapabilityVocabulary.HeapTypes)]
[OptionalCapability(CapabilityVocabulary.TraceAllocSamples)]
public sealed class ArrayAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "Array Analysis";
    public string Category => "Memory";

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

        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");
        IHeapSegmentQuery segments = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapRootQuery roots = context.HeapRoots
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRoots}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapObjects}' capability.");
        IDumpArrayShapeQuery arrayShapes = context.DumpArrayShapes
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpArrayElements}' capability.");

        ArrayAnalysisOptions options = context.AnalyzerOptions as ArrayAnalysisOptions ?? new ArrayAnalysisOptions();
        var result = Analyze(typeStatistics, segments, roots, objectLookup, arrayShapes, options, context.Progress, cancellationToken);
        LastResult = result;

        await EmitObservationsAsync(context, objectLookup, result, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): four observation
    /// types, plus a fifth (<c>type.array-population</c>) added Phase 5 step 4 for
    /// <c>ArrayTrendComparer</c>'s headline metrics (Bucket D of
    /// docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md) — the same 5 totals
    /// <see cref="ArrayDomainResult.TotalArrayObjects"/>/<see cref="ArrayDomainResult.TotalArrayBytes"/>/
    /// <see cref="ArrayDomainResult.LohArrayBytes"/>/<see cref="ArrayDomainResult.PinnedArrayCount"/>/
    /// <see cref="ArrayDomainResult.PinnedArrayBytes"/> that back the domain result, promoted as a
    /// whole-run, no-entity-subject observation — no new heap traversal, they're already accumulated
    /// once during the same scan that builds <see cref="ArrayDomainResult.TopArrayTypesBySize"/>.
    /// <c>type.array-composition</c> is emitted per <see cref="ArrayDomainResult.TopArrayTypesBySize"/>
    /// entry (exhaustive over distinct element-type+rank combinations, despite the "Top" name — no
    /// cap) and carries raw <c>loh.count</c>/<c>loh.bytes</c>/<c>pinned.count</c>/<c>pinned.bytes</c>
    /// per type+rank, not just the blended <c>gen2-plus-loh.percent</c>, specifically so a synthesis
    /// rule can reconstruct <see cref="ArrayDomainResult.LohArrayBytes"/>/<see cref="ArrayDomainResult.PinnedArrayBytes"/>
    /// exactly by summing across all of them (audit finding from the per-analyzer playbook's step 3 —
    /// see docs/refactor/modularity/phase-5-observations-synthesis.md's "Before step 2" prerequisites).
    /// <c>heap.sparse-array</c>, <c>heap.large-array</c>, and <c>heap.pinned-array</c> are each
    /// emitted per their respective bounded top-N entry (≤64 sparse, whatever the LOH satellite
    /// index/fallback captured for large, ≤<c>options.TopPinnedArrayLimit</c> for pinned), resolved
    /// to a real <see cref="Sdk.Identity.TypeRef"/> via <see cref="IHeapObjectLookup"/> since
    /// each entry retains its sample/instance address — same shape, same narrative-fidelity ceiling as
    /// the domain result's own bounded lists.
    /// </summary>
    private static async ValueTask EmitObservationsAsync(AnalysisContext context, IHeapObjectLookup objectLookup, ArrayDomainResult result, CancellationToken cancellationToken)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes, CapabilityVocabulary.HeapObjects };

        foreach (ArrayTypeProfile profile in result.TopArrayTypesBySize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(profile.ElementTypeName);

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.array-composition",
                Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity }],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(profile.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(profile.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2-plus-loh.percent"] = new Measure(profile.Gen2PlusLohPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["avg-size.bytes"] = new Measure(profile.AverageInstanceSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["array.rank"] = new Measure(profile.Rank, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["loh.count"] = new Measure(profile.LohCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["loh.bytes"] = new Measure(profile.LohBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["pinned.count"] = new Measure(profile.PinnedCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pinned.bytes"] = new Measure(profile.PinnedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "Array Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }

        // Whole-run scalar — no entity subject, same "no natural per-entity anchor" pattern as
        // gc.generation-composition. All 5 totals are reconstructable by summing
        // type.array-composition's own per-type+rank measures above (that stream is exhaustive,
        // not capped, unlike TopSparseArrays/TopLargeArrays/TopPinnedArrays), but the differ can't
        // do that grouping — same pragmatic direct-emit call made throughout Phase 5 step 4's
        // Bucket B work for infra.sql-connection-pool-population.near-capacity.count and others.
        await context.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "type.array-population",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["total.count"] = new Measure(result.TotalArrayObjects, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["total.bytes"] = new Measure(result.TotalArrayBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["loh.bytes"] = new Measure(result.LohArrayBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                ["pinned.count"] = new Measure(result.PinnedArrayCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["pinned.bytes"] = new Measure(result.PinnedArrayBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            },
            Provenance = new Provenance
            {
                Artifact = context.ArtifactId!.Value,
                AnalyzerKey = "Array Analysis",
                CapabilitiesUsed = capabilitiesUsed,
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);

        foreach (SparseArrayEntry sparse in result.TopSparseArrays)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!objectLookup.TryGetObject(sparse.Address, out HeapObjectRef obj))
                continue;

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.sparse-array",
                Subjects = [obj.Type],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["length"] = new Measure(sparse.Length, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["null-or-zero.count"] = new Measure(sparse.NullOrZeroCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["sparse.ratio"] = new Measure(sparse.SparseRatio, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["wasted.bytes"] = new Measure(sparse.WastedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "Array Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
                // The sparse-array finding narrative prints the exact sample address ("Array of X at
                // 0xADDR has..."); nothing else on the observation carries it, so ArraySynthesisRule
                // needs this to reproduce that text.
                Evidence = [new EvidenceRef { Artifact = context.ArtifactId!.Value, Locator = sparse.Address.ToString(System.Globalization.CultureInfo.InvariantCulture) }],
            }, cancellationToken);
        }

        foreach (LargeArrayEntry large in result.TopLargeArrays)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!objectLookup.TryGetObject(large.Address, out HeapObjectRef obj))
                continue;

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.large-array",
                Subjects = [obj.Type],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["length"] = new Measure(large.Length, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(large.Size, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["array.rank"] = new Measure(large.Rank, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "Array Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }

        foreach (LargeArrayEntry pinned in result.TopPinnedArrays ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!objectLookup.TryGetObject(pinned.Address, out HeapObjectRef obj))
                continue;

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.pinned-array",
                Subjects = [obj.Type],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["length"] = new Measure(pinned.Length, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(pinned.Size, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["array.rank"] = new Measure(pinned.Rank, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value,
                    AnalyzerKey = "Array Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }
    }

    private static ArrayDomainResult Analyze(
        IHeapTypeStatisticsQuery typeStatistics, IHeapSegmentQuery segments, IHeapRootQuery roots,
        IHeapObjectLookup objectLookup, IDumpArrayShapeQuery arrayShapes, ArrayAnalysisOptions options,
        IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
    {
        if (!typeStatistics.HasExactGenerationData)
            return new ArrayDomainResult(0, 0, 0, 0, 0, 0, [], [], []);

        // -- Step 1: Aggregate population from per-type array-shape stats -----------------
        progress?.Report(new(0, "scanning array type aggregates"));

        ulong totalHeapBytes = typeStatistics.GetTotalIndexedBytes();

        var arrayTypeNames = new HashSet<string>(StringComparer.Ordinal);
        // Map: elementTypeName+rank key -> (count, totalBytes, isMultiDim, rank, gen2Count, lohCount, lohBytes, moduleName)
        var typeMap = new Dictionary<string, (long Count, ulong Bytes, bool IsMultiDim, int Rank, long Gen2Count, long LohCount, ulong LohBytes, string ModuleName)>(256);

        long totalObjects = 0;
        ulong totalBytes = 0;
        int multiDimCount = 0;
        int lohCount = 0;
        ulong lohBytes = 0;
        ulong multiDimBytes = 0;

        // Sparse candidates and LOH fallback candidates are collected in this same pass to avoid a
        // second scan over the per-type dictionary. Sorted by size descending before use so the
        // largest arrays are probed/used first, matching the pre-retyping analyzer's own ordering.
        var sparseCandidates = new List<(ulong SampleAddress, string ElemName, ulong TotalSize)>(256);
        var lohFallbackCandidates = new List<(ulong SampleAddress, ulong LohSize)>(256);

        foreach (KeyValuePair<string, HeapTypeStatistics> kv in typeStatistics.GetTypeStatistics())
        {
            HeapTypeStatistics e = kv.Value;
            if (!e.IsArray) continue;

            arrayTypeNames.Add(kv.Key);

            bool isMultiDim = e.ArrayRank > 1;

            totalObjects += e.InstanceCount;
            totalBytes += e.TotalSize;
            if (isMultiDim) { multiDimCount += (int)Math.Min(e.InstanceCount, int.MaxValue); multiDimBytes += e.TotalSize; }
            if (e.LohCount > 0)
            {
                lohCount += (int)Math.Min(e.LohCount, int.MaxValue);
                lohBytes += e.LohSize;
                if (e.SampleAddress != 0)
                    lohFallbackCandidates.Add((e.SampleAddress, e.LohSize));
            }

            string key = $"{e.ArrayElementTypeName}[rank={e.ArrayRank}]";
            if (typeMap.TryGetValue(key, out var existing))
                typeMap[key] = (existing.Count + e.InstanceCount,
                                existing.Bytes + e.TotalSize, isMultiDim, e.ArrayRank,
                                existing.Gen2Count + e.Gen2Count, existing.LohCount + e.LohCount,
                                existing.LohBytes + e.LohSize, existing.ModuleName);
            else
                typeMap[key] = (e.InstanceCount, e.TotalSize, isMultiDim, e.ArrayRank, e.Gen2Count, e.LohCount, e.LohSize, e.ModuleName);

            // Sparse candidate: 1-D ref-type arrays (walked via GetObjectValue), or 1-D
            // primitive-numeric-type arrays (walked via chunked ReadValues<T>). Arbitrary structs
            // are excluded -- ReadValues<T> requires an exact blittable layout match, which isn't
            // guaranteed for hand-authored struct field ordering, and "zero" is ill-defined for
            // structs holding reference fields.
            if (!isMultiDim && e.ArrayRank == 1 && e.SampleAddress != 0
                && (e.ArrayElementIsObjectReference || e.ArrayElementIsSupportedNumeric))
            {
                sparseCandidates.Add((e.SampleAddress, e.ArrayElementTypeName, e.TotalSize));
            }
        }

        // -- Step 2: Top array types by total bytes -----------------------------------------
        var typeList = new List<(string ElemName, int Rank, int Count, ulong Bytes, bool IsMultiDim, long Gen2Count, long LohCount, ulong LohBytes, string ModuleName)>(typeMap.Count);
        foreach (KeyValuePair<string, (long Count, ulong Bytes, bool IsMultiDim, int Rank, long Gen2Count, long LohCount, ulong LohBytes, string ModuleName)> kv in typeMap)
        {
            int rankSep = kv.Key.LastIndexOf("[rank=", StringComparison.Ordinal);
            string elemName = rankSep > 0 ? kv.Key[..rankSep] : kv.Key;
            typeList.Add((elemName, kv.Value.Rank, (int)Math.Min(kv.Value.Count, int.MaxValue), kv.Value.Bytes, kv.Value.IsMultiDim, kv.Value.Gen2Count, kv.Value.LohCount, kv.Value.LohBytes, kv.Value.ModuleName));
        }
        typeList.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));

        var topArrayTypes = new List<ArrayTypeProfile>(typeList.Count);
        foreach (var t in typeList)
        {
            double percentOfHeap = totalHeapBytes > 0 ? t.Bytes * 100.0 / totalHeapBytes : 0.0;
            double gen2PlusLohPct = t.Count > 0 ? (t.Gen2Count + t.LohCount) * 100.0 / t.Count : 0.0;
            double avgSize = t.Count > 0 ? t.Bytes / (double)t.Count : 0.0;
            topArrayTypes.Add(new ArrayTypeProfile(
                t.ElemName, t.Rank, t.Count, t.Bytes, t.IsMultiDim, percentOfHeap, gen2PlusLohPct, avgSize, t.ModuleName,
                LohCount: t.LohCount, LohBytes: t.LohBytes));
        }

        // -- Step 3: Large array analysis ----------------------------------------------------
        // Try the Phase-1 captured-large-object sample first; fall back to per-type LOH
        // aggregates in memory mode.
        progress?.Report(new(0, "analysing large arrays"));

        var topLargeArrays = new List<LargeArrayEntry>(64);

        if (segments.HasLohSatelliteIndex)
        {
            foreach (HeapObjectRef candidate in segments.EnumerateCapturedLargeObjects())
            {
                if (candidate.IsFree || !arrayTypeNames.Contains(candidate.TypeDisplayName))
                    continue;
                if (!arrayShapes.TryGetArrayShape(candidate.Address, out int length, out int rank))
                    continue;

                topLargeArrays.Add(new LargeArrayEntry(
                    Address: candidate.Address,
                    ElementTypeName: candidate.TypeDisplayName,
                    Length: length,
                    Rank: rank,
                    Size: candidate.Size));
            }
        }

        // If the index wasn't available, use the LOH fallback candidates collected in Step 1 (no
        // second per-type scan). Sorted by LohSize descending first so the top entries taken here
        // are actually the largest LOH arrays, matching the index-based path's ordering.
        if (topLargeArrays.Count == 0 && lohFallbackCandidates.Count > 0)
        {
            lohFallbackCandidates.Sort(static (a, b) => b.LohSize.CompareTo(a.LohSize));
            foreach (var candidate in lohFallbackCandidates)
            {
                if (!objectLookup.TryGetObject(candidate.SampleAddress, out HeapObjectRef obj)) continue;
                if (!arrayShapes.TryGetArrayShape(candidate.SampleAddress, out int length, out int rank)) continue;

                topLargeArrays.Add(new LargeArrayEntry(
                    Address: candidate.SampleAddress,
                    ElementTypeName: obj.TypeDisplayName,
                    Length: length,
                    Rank: rank,
                    Size: obj.Size));
            }
        }

        // Use the candidates pre-collected in Step 1 -- no second per-type scan. Every candidate is
        // probed; the sort only affects display/processing order, not which arrays get evaluated.
        progress?.Report(new(0, "sampling sparse arrays"));

        sparseCandidates.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));

        var topSparseArrays = new List<SparseArrayEntry>(64);

        foreach (var (sampleAddr, elemName, _) in sparseCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!arrayShapes.TryGetArrayShape(sampleAddr, out int length, out int rank)) continue;
            if (length < options.SparseSampleMinLength) continue;
            if (rank > 1) continue;
            if (length == 0) continue;

            // Walk every element -- no stride sampling, so NullOrZeroCount/WastedBytes below are
            // exact counts, not extrapolated estimates. Null means part of the array was
            // unreadable (e.g. a truncated dump); the candidate is skipped rather than reporting a
            // ratio over a partial read.
            int? nullOrZeroCount = arrayShapes.CountNullOrZeroElements(sampleAddr);
            if (nullOrZeroCount is null) continue;

            double sparseRatio = (double)nullOrZeroCount.Value / length;
            if (sparseRatio < 0.5) continue;

            // Element size isn't carried per-candidate (it's a per-type fact); resolved by looking
            // the sample address's type back up rather than threading it through the tuple, since
            // this path is already an out-of-line lookup for shape.
            if (!objectLookup.TryGetObject(sampleAddr, out HeapObjectRef sampleObj)) continue;
            if (!typeStatistics.GetTypeStatistics().TryGetValue(sampleObj.TypeDisplayName, out HeapTypeStatistics typeStats)) continue;

            ulong elemSize = typeStats.ArrayElementIsObjectReference ? (ulong)IntPtr.Size : (ulong)typeStats.ArrayElementSizeBytes;
            ulong wastedBytes = (ulong)nullOrZeroCount.Value * elemSize;

            topSparseArrays.Add(new SparseArrayEntry(
                Address: sampleAddr,
                ElementTypeName: elemName,
                Length: length,
                NullOrZeroCount: nullOrZeroCount.Value,
                SparseRatio: sparseRatio,
                WastedBytes: wastedBytes));
        }

        topSparseArrays.Sort(static (a, b) => b.WastedBytes.CompareTo(a.WastedBytes));

        // -- Step 4: Pinned array detection via GC root query --------------------------------
        // Cross-reference PinnedHandle/AsyncPinnedHandle root targets against the array-type set
        // built in Step 1. Pinned handle counts are small (tens-thousands, not heap-sized), so this
        // walk is cheap and -- unlike the sparse/LOH candidates above -- checks every pinned
        // instance exactly rather than one sample per type.
        progress?.Report(new(0, "detecting pinned arrays"));

        int pinnedArrayCount = 0;
        ulong pinnedArrayBytes = 0;
        var topPinnedArrays = new List<LargeArrayEntry>(64);
        // Per-type accumulation, merged into topArrayTypes below -- exhaustive like LohCount/LohBytes
        // above, not just the options.TopPinnedArrayLimit-capped sample topPinnedArrays keeps for
        // narrative purposes. Keyed identically to typeMap's own key so the merge is a plain lookup.
        var pinnedByType = new Dictionary<string, (long Count, ulong Bytes)>(64);

        foreach (HeapRootRef root in roots.EnumerateRoots())
        {
            if (root.Kind is not (HeapRootKind.PinnedHandle or HeapRootKind.AsyncPinnedHandle))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            if (!objectLookup.TryGetObject(root.TargetAddress, out HeapObjectRef obj)) continue;
            if (!arrayTypeNames.Contains(obj.TypeDisplayName)) continue;
            if (!arrayShapes.TryGetArrayShape(root.TargetAddress, out int length, out int rank)) continue;

            pinnedArrayCount++;
            pinnedArrayBytes += obj.Size;
            topPinnedArrays.Add(new LargeArrayEntry(
                Address: root.TargetAddress,
                ElementTypeName: obj.TypeDisplayName,
                Length: length,
                Rank: rank,
                Size: obj.Size));

            string pinnedKey = $"{obj.TypeDisplayName}[rank={rank}]";
            pinnedByType[pinnedKey] = pinnedByType.TryGetValue(pinnedKey, out var existingPinned)
                ? (existingPinned.Count + 1, existingPinned.Bytes + obj.Size)
                : (1, obj.Size);
        }

        topPinnedArrays.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        if (topPinnedArrays.Count > options.TopPinnedArrayLimit)
            topPinnedArrays.RemoveRange(options.TopPinnedArrayLimit, topPinnedArrays.Count - options.TopPinnedArrayLimit);

        if (pinnedByType.Count > 0)
        {
            for (int i = 0; i < topArrayTypes.Count; i++)
            {
                ArrayTypeProfile profile = topArrayTypes[i];
                string key = $"{profile.ElementTypeName}[rank={profile.Rank}]";
                if (pinnedByType.TryGetValue(key, out var pinned))
                    topArrayTypes[i] = profile with { PinnedCount = pinned.Count, PinnedBytes = pinned.Bytes };
            }
        }

        return new ArrayDomainResult(
            TotalArrayObjects: (int)Math.Min(totalObjects, int.MaxValue),
            TotalArrayBytes: totalBytes,
            MultiDimArrayCount: multiDimCount,
            MultiDimArrayBytes: multiDimBytes,
            LohArrayCount: lohCount,
            LohArrayBytes: lohBytes,
            TopArrayTypesBySize: topArrayTypes,
            TopLargeArrays: topLargeArrays,
            TopSparseArrays: topSparseArrays,
            PinnedArrayCount: pinnedArrayCount,
            PinnedArrayBytes: pinnedArrayBytes,
            TopPinnedArrays: topPinnedArrays);
    }

    public void Dispose() { }
}
