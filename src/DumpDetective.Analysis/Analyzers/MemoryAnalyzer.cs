using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

// DumpDetective.Analysis.Models and DumpDetective.Sdk.Analysis both declare HeapSegmentKind and
// RegionGenerationKind (deliberately identical names, see SdkSegmentKindMapper) — alias the SDK
// ones (what this analyzer sources segment data as) to disambiguate.
using SdkHeapSegmentKind = DumpDetective.Sdk.Analysis.HeapSegmentKind;
using SdkRegionGenerationKind = DumpDetective.Sdk.Analysis.RegionGenerationKind;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Phase 1 retyping batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// retyped onto the SDK's capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing type
/// statistics through <see cref="IHeapTypeStatisticsQuery"/> (<c>heap.types</c>), segment data
/// through <see cref="IHeapSegmentQuery"/> (<c>heap.segments</c>), and the retained-size walk
/// through <see cref="IHeapObjectLookup"/>/<see cref="IHeapReferenceQuery"/>. Runs through the
/// existing pipeline via <see cref="MemoryAnalyzerLegacyAdapter"/>.
/// </summary>
/// <remarks>
/// <see cref="MemoryAnalysisProjection.Build"/> (histogram bucketing, top1/5/10 bytes, pressure
/// scores) is pure arithmetic over <c>Dictionary&lt;string, CachedTypeStatistics&gt;</c> — no
/// <c>ClrHeap</c>/<c>IHeapAnalysisCache</c> dependency at all — so it's reused unchanged here via a
/// small bridge dictionary built from <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/>,
/// rather than re-derived against capability types the way <c>GCRootAnalyzer</c>'s dominator/BFS
/// logic had to be (that logic took <c>ClrHeap</c>/<c>IHeapAnalysisCache</c> directly and couldn't
/// be called unchanged). <see cref="DumpDetective.Analysis.Analyzers"/> — unlike
/// <c>DumpDetective.Sdk</c> — has no restriction against referencing dump-side, ClrMD-touching
/// types, so this reuse is a plain internal call, not a boundary violation.
/// </remarks>
public sealed class MemoryAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    // Retained-size is a bounded BFS per candidate, not a cheap lookup — computing it for every
    // distinct type on a large heap (tens of thousands) would be a real wall-clock cost. Bounded to
    // the largest types by shallow size, a fixed internal constant rather than a tier-varying
    // option (§9.27) — the type *list* itself is exact and uncapped; only this expensive enrichment
    // is scoped.
    internal const int TypesToWalkForRetainedSize = 20;

    // Matches RetainedSizeCandidateSelector.SelectAndCompute's own defaults — this analyzer never
    // overrode them pre-retyping, unlike GCRootAnalyzer's explicit, much tighter 500/20 bound.
    private const int RetainedWalkMaxBreadth = 10_000;
    private const int RetainedWalkMaxDepth = 20;

    public string Name => "Memory Analysis";
    public string Category => "Memory";
    public IReadOnlyCollection<string> Tags => ["memory", "heap", "pressure"];
    public bool IsThreadSafe => true;

    public AnalyzerDomainResult? LastResult { get; private set; }

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapTypes}' capability.");
        IHeapSegmentQuery segmentQuery = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
        IHeapReferenceQuery referenceQuery = context.HeapReferences
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapReferences}' capability.");

        MemoryAnalysisOptions options = context.AnalyzerOptions as MemoryAnalysisOptions ?? new MemoryAnalysisOptions();

        LastResult = Analyze(typeStatistics, segmentQuery, objectLookup, referenceQuery, options, context.Progress, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static MemoryDomainResult Analyze(
        IHeapTypeStatisticsQuery typeStatistics,
        IHeapSegmentQuery segmentQuery,
        IHeapObjectLookup objectLookup,
        IHeapReferenceQuery referenceQuery,
        MemoryAnalysisOptions options,
        IProgress<AnalyzerProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(0, "building memory snapshot"));
        IReadOnlyDictionary<string, HeapTypeStatistics> typeStats = typeStatistics.GetTypeStatistics();
        IReadOnlyList<long>? globalBuckets = typeStatistics.TryGetGlobalSizeBuckets();

        Dictionary<string, CachedTypeStatistics> bridgedStats = ToCachedTypeStatistics(typeStats);
        MemoryAnalysisProjectionResult projection = MemoryAnalysisProjection.Build(bridgedStats, globalBuckets?.ToArray());

        List<GCSegmentSummary> segmentSummaries = BuildSegmentSummaries(segmentQuery);
        double lohFragmentationRatio = CalculateLohFragmentationRatio(segmentQuery);

        static TypeSnapshot ToSnapshot(CachedTypeStatistics s, ulong retainedBytes, ulong sampleAddress)
        {
            ulong avgSize = s.Count > 0 ? s.TotalSize / (ulong)s.Count : 0;
            return new TypeSnapshot(s.TypeName, s.Count, s.TotalSize, s.LohSize,
                AverageSize: avgSize,
                EstimatedRetainedBytes: retainedBytes,
                SampleAddress: sampleAddress,
                ModuleName: string.IsNullOrWhiteSpace(s.ModuleName) ? null : s.ModuleName);
        }

        IReadOnlyList<CachedTypeStatistics> allTypes = projection.AllTypesBySize;
        var topTypes = new List<TypeSnapshot>(allTypes.Count);

        if (segmentQuery.CanWalkHeap)
        {
            // Retained-size enrichment is bounded to the largest types by shallow size — a real
            // BFS per candidate, not affordable across every distinct type on a large heap. Every
            // other type still gets its exact count/size/LOH data, just no EstimatedRetainedBytes.
            int walkCount = Math.Min(allTypes.Count, TypesToWalkForRetainedSize);
            var sampleAddresses = new ulong[walkCount];
            var walkCandidates = new List<(ulong Address, ulong ShallowSize)>(walkCount);

            for (int i = 0; i < walkCount; i++)
            {
                ulong sampleAddress = typeStats.TryGetValue(allTypes[i].TypeName, out HeapTypeStatistics s) ? s.SampleAddress : 0;
                sampleAddresses[i] = sampleAddress;
                if (sampleAddress == 0)
                    continue;

                if (objectLookup.TryGetObject(sampleAddress, out HeapObjectRef root))
                    walkCandidates.Add((sampleAddress, root.Size));
            }

            // Stable order by descending shallow size — mirrors RetainedSizeCandidateSelector's own
            // tie-break so which candidate "claims" a shared subgraph first (via the shared
            // visited-set exclusivity below) stays deterministic the same way.
            walkCandidates.Sort(static (a, b) => b.ShallowSize.CompareTo(a.ShallowSize));

            var visited = new HashSet<ulong>();
            var retainedByAddress = new Dictionary<ulong, ulong>(walkCandidates.Count);
            try
            {
                foreach ((ulong address, _) in walkCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    retainedByAddress[address] = CapabilityBoundedGraphWalk.ComputeExclusiveRetained(
                        address, objectLookup, referenceQuery, visited, RetainedWalkMaxBreadth, RetainedWalkMaxDepth, cancellationToken);
                    progress?.Report(new(retainedByAddress.Count, "estimating retained size", $"{retainedByAddress.Count}/{walkCandidates.Count} types walked"));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                retainedByAddress.Clear();
            }

            for (int i = 0; i < walkCount; i++)
            {
                ulong retained = retainedByAddress.TryGetValue(sampleAddresses[i], out ulong r) ? r : 0;
                topTypes.Add(ToSnapshot(allTypes[i], retained, sampleAddresses[i]));
            }
            for (int i = walkCount; i < allTypes.Count; i++)
                topTypes.Add(ToSnapshot(allTypes[i], retainedBytes: 0, sampleAddress: 0));
        }
        else
        {
            for (int i = 0; i < allTypes.Count; i++)
            {
                ulong sampleAddress = typeStats.TryGetValue(allTypes[i].TypeName, out HeapTypeStatistics s) ? s.SampleAddress : 0;
                topTypes.Add(ToSnapshot(allTypes[i], retainedBytes: 0, sampleAddress));
            }
        }

        return new MemoryDomainResult(
            projection.TotalMemory,
            projection.TotalLohMemory,
            projection.LohPercent,
            projection.TotalObjects,
            projection.LohObjects,
            options.LohThresholdBytes,
            typeStats.Count,
            topTypes,
            SizeBucketHistogram: projection.Histogram,
            Top1BytesPercent: projection.TotalMemory == 0 ? 0 : projection.Top1Bytes * 100.0 / projection.TotalMemory,
            Top5BytesPercent: projection.TotalMemory == 0 ? 0 : projection.Top5Bytes * 100.0 / projection.TotalMemory,
            Top10BytesPercent: projection.TotalMemory == 0 ? 0 : projection.Top10Bytes * 100.0 / projection.TotalMemory,
            SmallObjectCountPercent: projection.TotalObjects == 0 ? 0 : projection.SmallObjectCount * 100.0 / projection.TotalObjects,
            SmallObjectBytesPercent: projection.TotalMemory == 0 ? 0 : projection.SmallObjectBytes * 100.0 / projection.TotalMemory,
            ObjectsPerMb: projection.ObjectsPerMb,
            MemoryPressureScore: projection.MemoryPressureScore,
            SegmentSummaries: segmentSummaries,
            LohFragmentationRatio: lohFragmentationRatio,
            LohPressureScore: projection.LohPressureScore,
            ConcentrationPressureScore: projection.ConcentrationPressureScore,
            SmallObjectPressureScore: projection.SmallObjectPressureScore,
            DensityPressureScore: projection.DensityPressureScore);
    }

    private static Dictionary<string, CachedTypeStatistics> ToCachedTypeStatistics(IReadOnlyDictionary<string, HeapTypeStatistics> stats)
    {
        var result = new Dictionary<string, CachedTypeStatistics>(stats.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, HeapTypeStatistics> kv in stats)
        {
            HeapTypeStatistics s = kv.Value;
            result[kv.Key] = new CachedTypeStatistics
            {
                TypeName = kv.Key,
                ModuleName = s.ModuleName,
                Count = (int)Math.Min(s.InstanceCount, int.MaxValue),
                TotalSize = s.TotalSize,
                LohCount = (int)Math.Min(s.LohCount, int.MaxValue),
                LohSize = s.LohSize,
            };
        }
        return result;
    }

    private static List<GCSegmentSummary> BuildSegmentSummaries(IHeapSegmentQuery segmentQuery)
    {
        var summaries = new Dictionary<string, (ulong committed, ulong reserved, ulong used, int count)>();

        foreach (HeapSegmentRef segment in segmentQuery.EnumerateSegments())
        {
            string generation = GetGenerationLabel(segment.RegionKind);
            // GetSegmentUsedBytes's pre-retyping computation was byte-identical to committed bytes
            // (both derive from the same CommittedMemory range) — reusing CommittedBytes here
            // rather than a second, redundant computation.
            ulong used = segment.CommittedBytes;

            if (summaries.TryGetValue(generation, out var current))
            {
                summaries[generation] = (
                    committed: current.committed + segment.CommittedBytes,
                    reserved: current.reserved + segment.ReservedBytes,
                    used: current.used + used,
                    count: current.count + 1);
            }
            else
            {
                summaries[generation] = (segment.CommittedBytes, segment.ReservedBytes, used, 1);
            }
        }

        var result = new List<GCSegmentSummary>(summaries.Count);
        foreach (var (gen, (committed, reserved, used, count)) in summaries.OrderBy(x => GetGenerationOrder(x.Key)))
            result.Add(new GCSegmentSummary(gen, committed, reserved, used, count));

        return result;
    }

    private static string GetGenerationLabel(SdkRegionGenerationKind regionKind) => regionKind switch
    {
        SdkRegionGenerationKind.Generation0 => "Gen0",
        SdkRegionGenerationKind.Generation1 => "Gen1",
        SdkRegionGenerationKind.Generation2 or SdkRegionGenerationKind.Ephemeral => "Gen2",
        SdkRegionGenerationKind.Large => "LOH",
        SdkRegionGenerationKind.Pinned => "POH",
        SdkRegionGenerationKind.Frozen => "Frozen",
    };

    private static int GetGenerationOrder(string generation) => generation switch
    {
        "Gen0" => 0,
        "Gen1" => 1,
        "Gen2" => 2,
        "LOH" => 3,
        "POH" => 4,
        "Frozen" => 5,
        _ => 6
    };

    private static double CalculateLohFragmentationRatio(IHeapSegmentQuery segmentQuery)
    {
        ulong lohCommitted = 0;
        ulong lohObjectBytes = 0;

        foreach (HeapSegmentRef segment in segmentQuery.EnumerateSegments())
        {
            if (segment.Kind != SdkHeapSegmentKind.LargeObjectHeap)
                continue;

            lohCommitted += segment.CommittedBytes;

            // includeFree: true — the pre-retyping computation summed every valid object
            // (live and free) on the segment, not just live ones, to derive a free-byte delta
            // against committed bytes. Preserved exactly rather than "fixed" during a retyping-only
            // change; see IHeapSegmentQuery.EnumerateObjects's own remarks.
            foreach (HeapObjectRef obj in segmentQuery.EnumerateObjects(segment, includeFree: true))
                lohObjectBytes += obj.Size;
        }

        if (lohCommitted == 0)
            return 0;

        ulong lohFreeBytes = lohCommitted > lohObjectBytes ? lohCommitted - lohObjectBytes : 0;
        return lohFreeBytes * 100.0 / lohCommitted;
    }

    public void Dispose() { }
}
