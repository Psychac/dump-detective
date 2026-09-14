using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;

using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

using SdkHeapSegmentKind = DumpDetective.Sdk.Analysis.HeapSegmentKind;

namespace DumpDetective.Analysis.Analyzers.Types;

/// <summary>
/// Analyze managed string usage: counts, sizes, LOH/FOH stats and duplicate patterns.
/// </summary>
/// <remarks>
/// Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Unlike
/// most Tier-2 wave members, this analyzer's P1-3 "string field ownership" feature needs to inspect
/// every heap object's *type*, not just objects matching a name-filtered candidate set — so it
/// doesn't fit <c>TypedResourceCandidateStreamingLegacyAdapter</c>. <see cref="StringAnalyzerLegacyAdapter"/>
/// is bespoke, mirroring the shape <c>CrashAnalyzer</c>/<c>HangAnalyzer</c> already established for
/// this kind of full-population scan participant.
///
/// The adapter streams every <c>System.String</c> instance as a <see cref="HeapObjectRef"/>
/// candidate (via <c>context.HeapObjects</c>) for this analyzer to fingerprint/bucket/dedup; the
/// P1-3 owner-type byte accounting is fundamentally a whole-heap-population aggregate with no
/// per-instance SDK-capability expression, so it's computed dump-side by the adapter during the
/// same shared scan pass and handed to this analyzer pre-aggregated via <see cref="StringAnalyzerOptions"/>
/// — the same "adapter accumulates real data during the pipeline's shared pass, hands its inner
/// analyzer the finished result" shape <c>HangAnalyzerLegacyAdapter</c> already uses.
///
/// <b>Dropped, not ported</b>: the pre-retyping analyzer's separate "prebuilt disk
/// <c>StringDedupIndex</c>" fast path and its "index-only estimation" fallback. Once the adapter's
/// shared scan pass always streams every string instance fresh (same cost it already pays today),
/// those two paths only existed to skip a scan that now always runs anyway — the same call
/// <c>AsyncTaskAnalyzer</c>'s retyping made for its own now-redundant fast path. Confirmed with the
/// user before implementing, since it's a real, user-visible behavior change (fewer
/// <see cref="StringDomainResult.DedupSource"/> values, no more index-only-estimated percentiles).
///
/// <b>Dropped, not ported</b>: the pre-retyping analyzer's independent live
/// <c>heap.EnumerateObjects()</c> fallback for when no shared scan ran. Every other Tier-2-wave
/// member already made this exact simplification (no live default <see cref="IHeapObjectStream"/>
/// implementation exists outside a scan participant) — an empty/zero per-instance result when
/// <c>context.HeapObjects</c> is null is accepted, matching <c>TimerLeakAnalyzer</c>/
/// <c>WeakReferenceAnalyzer</c>/<c>AsyncTaskAnalyzer</c>/<c>HangAnalyzer</c>. Scalar aggregate stats
/// (total strings/bytes, Gen0/1/2, LOH bytes) remain available either way, since they come from the
/// always-available <see cref="IHeapTypeStatisticsQuery"/> (which has its own no-index fallback,
/// already shared by five other analyzers).
/// </remarks>
public sealed partial class StringAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "String Analysis";
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

    private const int PreviewLength = 80;
    private const int MaxLengthSamples = 100_000;
    private const int MaxRootPathEnrichment = 20;

    private static readonly RootPathSearchOptions RootPathOptions = new(
        MaxCandidateNodes: 5_000, MaxCandidateDepth: 8, MaxRootExpansionDepth: 12, LargeFanoutThreshold: 100);

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");
        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");
        IHeapSegmentQuery segments = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapRootPathQuery rootPaths = context.HeapRootPaths
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRootPaths}' capability.");

        var options = context.AnalyzerOptions as StringAnalyzerOptions ?? new StringAnalyzerOptions(new StringAnalysisOptions(), null);

        LastResult = Analyze(typeStatistics, fields, segments, rootPaths, context.HeapObjects, options, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static StringDomainResult Analyze(
        IHeapTypeStatisticsQuery typeStatistics,
        IDumpObjectFieldQuery fields,
        IHeapSegmentQuery segments,
        IHeapRootPathQuery rootPaths,
        IHeapObjectStream? candidates,
        StringAnalyzerOptions analyzerOptions,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        StringAnalysisOptions stringOptions = analyzerOptions.Options;

        IReadOnlyDictionary<string, HeapTypeStatistics> stats = typeStatistics.GetTypeStatistics();

        int totalStrings = 0;
        ulong totalStringMemory = 0;
        ulong lohStringBytes = 0;
        long gen0StringCount = 0, gen1StringCount = 0, gen2StringCount = 0;
        ulong gen2StringBytes = 0;
        ulong totalManagedBytes = 0;

        foreach (HeapTypeStatistics entry in stats.Values)
        {
            totalManagedBytes += entry.TotalSize;
            if (!entry.IsStringType) continue;

            totalStrings += (int)Math.Min(entry.InstanceCount, int.MaxValue);
            totalStringMemory += entry.TotalSize;
            lohStringBytes += entry.LohSize;
            gen0StringCount += entry.Gen0Count;
            gen1StringCount += entry.Gen1Count;
            gen2StringCount += entry.Gen2Count;
            gen2StringBytes += entry.Gen2TotalSize;
        }

        var fohRanges = new List<(ulong Start, ulong End)>(capacity: 4);
        foreach (HeapSegmentRef segment in segments.EnumerateSegments())
            if (segment.Kind == SdkHeapSegmentKind.Frozen)
                fohRanges.Add((segment.Start, segment.End));

        var stringStats = new Dictionary<StringFingerprint, StringLeakInfo>(capacity: 1024);
        var lengthSamples = new List<int>(capacity: MaxLengthSamples);
        var lengthBuckets = NewLengthBuckets();
        var veryLongStrings = new List<LongStringEntry>(capacity: 16);
        var typeDupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int stringsSampled = 0;
        int internedStringCount = 0;
        ulong internedStringBytes = 0;
        string? dedupSource = null;
        int maxUnique = stringOptions.MaxUniqueStringTracking;

        if (candidates is not null)
        {
            dedupSource = "IndexScan";
            foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (fohRanges.Count > 0 && IsInFoh(candidate.Address, fohRanges))
                {
                    internedStringCount++;
                    internedStringBytes += candidate.Size;
                }

                if (candidate.Size >= (ulong)stringOptions.VeryLongStringThresholdBytes)
                {
                    int estCharLength = candidate.Size > 26 ? (int)Math.Min((candidate.Size - 26) / 2, int.MaxValue) : 0;
                    fields.TryGetStringValue(candidate.Address, 100, out string? longPreview);
                    veryLongStrings.Add(new LongStringEntry(candidate.Address, estCharLength, candidate.Size,
                        Preview: longPreview is not null ? CreatePreview(longPreview, PreviewLength) : null,
                        TypeName: candidate.TypeDisplayName));
                }

                if (!IsStringSizeInBounds(candidate.Size, stringOptions)) continue;
                stringsSampled++;
                if (!fields.TryGetStringValue(candidate.Address, stringOptions.MaxDuplicateStringLength - 1, out string? value)
                    || value is null || value.Length == 0 || value.Length < stringOptions.MinDuplicateCharLength)
                    continue;

                FingerprintValue(candidate.Address, value, candidate.TypeDisplayName, stringStats, maxUnique,
                    typeDupCounts, lengthSamples, lengthBuckets, samplingSource: "IndexScan");
            }
        }

        List<StringLeakInfo> duplicates = SelectDuplicates(stringStats.Values, stringOptions.MinDuplicateStringCount,
            out int duplicatePatternCount, out ulong duplicateWastedBytes);

        IReadOnlyList<DuplicateStringSnapshot> topDuplicates = BuildDuplicateSnapshots(duplicates);

        int sampledUniquePatterns = stringStats.Count;
        double duplicationRatio = stringsSampled > 0 ? (double)(stringsSampled - sampledUniquePatterns) / stringsSampled : 0.0;
        double samplingCoverage = totalStrings > 0 ? Math.Min(1.0, (double)stringsSampled / totalStrings) : 0.0;
        double pctOfManagedHeap = totalManagedBytes > 0 ? 100.0 * totalStringMemory / totalManagedBytes : 0.0;

        DistributionSummary? distribution = BuildDistribution(lengthSamples, lengthBuckets, stringStats.Values);

        const int maxVeryLongStringsToKeep = 1000;
        if (veryLongStrings.Count > maxVeryLongStringsToKeep)
        {
            veryLongStrings.Sort(static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            veryLongStrings.RemoveRange(maxVeryLongStringsToKeep, veryLongStrings.Count - maxVeryLongStringsToKeep);
        }

        IReadOnlyList<NameCountEntry>? topDuplicateTypes = null;
        if (typeDupCounts.Count > 0)
        {
            var top = new List<NameCountEntry>(capacity: Math.Min(10, typeDupCounts.Count));
            foreach (KeyValuePair<string, int> kv in typeDupCounts.OrderByDescending(static kv => kv.Value).Take(10))
                top.Add(new NameCountEntry(kv.Key, kv.Value));
            topDuplicateTypes = top;
        }

        IReadOnlyList<DuplicateStringRetentionPath>? retentionPaths =
            BuildRetentionPaths(rootPaths, topDuplicates, stringOptions.RetentionPathSampleCount, cancellationToken);

        stopwatch.Stop();

        return new StringDomainResult(
            TotalStrings: totalStrings,
            TotalStringMemoryBytes: totalStringMemory,
            SampledUniquePatterns: sampledUniquePatterns,
            DuplicatePatternCount: duplicatePatternCount,
            DuplicateWastedBytes: duplicateWastedBytes,
            DuplicationRatio: duplicationRatio,
            PctOfManagedHeap: pctOfManagedHeap,
            TopDuplicates: topDuplicates,
            VeryLongStrings: veryLongStrings,
            LohStringBytes: lohStringBytes,
            InternedStringCount: internedStringCount,
            InternedStringBytes: internedStringBytes,
            Gen0StringCount: gen0StringCount,
            Gen1StringCount: gen1StringCount,
            Gen2StringCount: gen2StringCount,
            Gen2StringBytes: gen2StringBytes,
            StringsSampled: stringsSampled,
            SamplingCoverage: samplingCoverage,
            DedupSource: dedupSource,
            AnalysisDurationMs: stopwatch.ElapsedMilliseconds,
            TopDuplicateTypes: topDuplicateTypes,
            TopStringOwnerTypes: analyzerOptions.OwnerTypeBytes,
            Distribution: distribution,
            Artifacts: null,
            TopDuplicateRetentionPaths: retentionPaths);
    }

    private static Dictionary<string, int> NewLengthBuckets() => new(StringComparer.Ordinal)
    {
        ["0-15"] = 0, ["16-31"] = 0, ["32-63"] = 0, ["64-127"] = 0, ["128-255"] = 0,
        ["256-511"] = 0, ["512-1023"] = 0, ["1024-4095"] = 0, ["4096-16383"] = 0,
        ["16384-65535"] = 0, ["65536+"] = 0,
    };

    private static bool IsInFoh(ulong address, List<(ulong Start, ulong End)> fohSegments)
    {
        for (int i = 0; i < fohSegments.Count; i++)
            if (address >= fohSegments[i].Start && address < fohSegments[i].End) return true;
        return false;
    }

    /// <summary>
    /// Heuristic to decide whether a raw object size likely corresponds to a string character
    /// length within the configured maximum, avoiding a content read for strings that are
    /// obviously too long to be worth fingerprinting. Uses the same approximate formula used
    /// elsewhere: chars ~= (size-26)/2.
    /// </summary>
    private static bool IsStringSizeInBounds(ulong size, StringAnalysisOptions stringOptions)
    {
        if (stringOptions.MaxDuplicateStringLength <= 0) return true;
        if (size <= 26) return true;
        ulong estChars = (size - 26) / 2;
        return estChars <= (ulong)stringOptions.MaxDuplicateStringLength;
    }

    /// <summary>Fingerprints a string value already read from <paramref name="address"/>, updating
    /// <paramref name="stringStats"/>/<paramref name="lengthSamples"/>/<paramref name="lengthBuckets"/>/
    /// <paramref name="typeDupCounts"/> in place.</summary>
    private static void FingerprintValue(
        ulong address, string value, string typeDisplayName,
        Dictionary<StringFingerprint, StringLeakInfo> stringStats, int maxUniqueTracking,
        Dictionary<string, int> typeDupCounts, List<int> lengthSamples, Dictionary<string, int> lengthBuckets,
        string samplingSource)
    {
        var fingerprint = CreateFingerprint(value);

        if (!stringStats.ContainsKey(fingerprint) && stringStats.Count >= maxUniqueTracking) return;

        ref StringLeakInfo info = ref CollectionsMarshal.GetValueRefOrAddDefault(stringStats, fingerprint, out bool existed);

        if (!existed)
        {
            info.Preview = CreatePreview(value, PreviewLength);
            info.SampleAddresses = new[] { address };
            info.FingerprintHash = fingerprint.Hash;
            info.SamplingSource = samplingSource;
        }
        else
        {
            if (info.SampleAddresses is null) info.SampleAddresses = new[] { address };
            else if (info.SampleAddresses.Length == 1 && info.SampleAddresses[0] != address)
                info.SampleAddresses = new[] { info.SampleAddresses[0], address };
            if (string.IsNullOrEmpty(info.SamplingSource)) info.SamplingSource = samplingSource;
        }

        int charLen = value.Length;
        if (lengthSamples.Count < MaxLengthSamples) lengthSamples.Add(charLen);
        string key = charLen switch
        {
            < 16 => "0-15",
            < 32 => "16-31",
            < 64 => "32-63",
            < 128 => "64-127",
            < 256 => "128-255",
            < 512 => "256-511",
            < 1024 => "512-1023",
            < 4096 => "1024-4095",
            < 16384 => "4096-16383",
            < 65536 => "16384-65535",
            _ => "65536+",
        };
        lengthBuckets.TryGetValue(key, out int bucketCount);
        lengthBuckets[key] = bucketCount + 1;

        info.TotalSize += (ulong)value.Length;
        typeDupCounts.TryGetValue(typeDisplayName, out int existingTypeCount);
        typeDupCounts[typeDisplayName] = existingTypeCount + 1;
        info.Count++;
    }

    private static DistributionSummary? BuildDistribution(
        List<int> lengthSamples, Dictionary<string, int> lengthBuckets, Dictionary<StringFingerprint, StringLeakInfo>.ValueCollection stats)
    {
        var freqBuckets = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["1"] = 0, ["2"] = 0, ["3-10"] = 0, ["11-100"] = 0, ["101-1000"] = 0, ["1001+"] = 0,
        };
        foreach (StringLeakInfo info in stats)
        {
            int c = info.Count;
            if (c <= 1) freqBuckets["1"]++;
            else if (c == 2) freqBuckets["2"]++;
            else if (c <= 10) freqBuckets["3-10"]++;
            else if (c <= 100) freqBuckets["11-100"]++;
            else if (c <= 1000) freqBuckets["101-1000"]++;
            else freqBuckets["1001+"]++;
        }

        if (lengthSamples.Count == 0) return null;

        var sorted = new List<int>(lengthSamples);
        sorted.Sort();
        int sampleCount = sorted.Count;
        var percentiles = new Dictionary<string, double>
        {
            ["p50"] = sorted[(int)Math.Floor((sampleCount - 1) * 0.50)],
            ["p75"] = sorted[(int)Math.Floor((sampleCount - 1) * 0.75)],
            ["p90"] = sorted[(int)Math.Floor((sampleCount - 1) * 0.90)],
            ["p95"] = sorted[(int)Math.Floor((sampleCount - 1) * 0.95)],
        };

        return new DistributionSummary(percentiles, lengthBuckets, freqBuckets, sampleCount);
    }

    /// <summary>
    /// GC root-path search for the top duplicate patterns by wasted bytes (P3-2,
    /// string-analyzer-audit.md) — answers "why is this duplicated value still alive."
    /// </summary>
    private static IReadOnlyList<DuplicateStringRetentionPath>? BuildRetentionPaths(
        IHeapRootPathQuery rootPaths, IReadOnlyList<DuplicateStringSnapshot> topDuplicates, int sampleCount,
        CancellationToken cancellationToken)
    {
        if (sampleCount <= 0 || topDuplicates.Count == 0) return null;

        IReadOnlyList<(DuplicateStringSnapshot Duplicate, ulong Address)> candidates =
            SelectRetentionPathCandidates(topDuplicates, sampleCount);
        if (candidates.Count == 0) return null;

        var results = new List<DuplicateStringRetentionPath>(candidates.Count);
        foreach ((DuplicateStringSnapshot dup, ulong address) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool found = rootPaths.TryFindRootPath(address, RootPathOptions, out string? formattedPath, out bool searchTruncated);
            results.Add(new DuplicateStringRetentionPath(dup.Preview, address, found, found ? formattedPath : null, searchTruncated));
        }

        return results.Count > 0 ? results : null;
    }

    /// <summary>
    /// Picks up to <paramref name="sampleCount"/> duplicate patterns (in supplied order — callers
    /// pass a wasted-bytes-descending list) with a resolvable sample address, pairing each with the
    /// address the root-path search should target. Pure selection logic, split out from
    /// <see cref="BuildRetentionPaths"/> so it's testable without live capability implementations.
    /// </summary>
    internal static IReadOnlyList<(DuplicateStringSnapshot Duplicate, ulong Address)> SelectRetentionPathCandidates(
        IReadOnlyList<DuplicateStringSnapshot> topDuplicates, int sampleCount)
    {
        var candidates = new List<(DuplicateStringSnapshot, ulong)>(Math.Min(sampleCount, topDuplicates.Count));
        for (int i = 0; i < topDuplicates.Count && candidates.Count < sampleCount; i++)
        {
            DuplicateStringSnapshot dup = topDuplicates[i];
            if (dup.SampleAddresses is { Count: > 0 })
                candidates.Add((dup, dup.SampleAddresses[0]));
        }
        return candidates;
    }

    /// <summary>Compute a content fingerprint for <paramref name="value"/>.</summary>
    private static StringFingerprint CreateFingerprint(string value)
    {
        // XxHash64 over the UTF-16 bytes is SIMD-accelerated, faster than a character-loop hash.
        ulong hash = XxHash64.HashToUInt64(MemoryMarshal.AsBytes(value.AsSpan()));
        return new StringFingerprint(hash, value.Length, value[0], value[^1]);
    }

    /// <summary>Create a display-safe preview of a string value.</summary>
    private static string CreatePreview(string value, int maxLength)
    {
        int cut = Math.Max(8, Math.Min(maxLength, value.Length));
        string preview = value.Length > cut ? value[..cut] + "..." : value;
        return preview.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private readonly record struct StringFingerprint(ulong Hash, int Length, char FirstChar, char LastChar);

    /// <summary>
    /// Select string patterns whose occurrence count meets <paramref name="minCount"/> and compute
    /// total wasted bytes (bytes beyond the first occurrence of each pattern). Pure, unchanged from
    /// the pre-retyping analyzer — reflected-into directly by
    /// <c>StringAnalyzerDuplicateSelectionTests</c>, so its name/signature/behavior are preserved
    /// exactly.
    /// </summary>
    private static List<StringLeakInfo> SelectDuplicates(
        Dictionary<StringFingerprint, StringLeakInfo>.ValueCollection stats,
        int minCount,
        out int duplicatePatternCount,
        out ulong duplicateWastedBytes)
    {
        var duplicates = new List<StringLeakInfo>();
        duplicatePatternCount = 0;
        duplicateWastedBytes = 0;

        foreach (StringLeakInfo info in stats)
        {
            if (info.Count < minCount) continue;
            duplicatePatternCount++;
            duplicateWastedBytes += info.TotalSize * (ulong)(info.Count - 1) / (ulong)info.Count;
            duplicates.Add(info);
        }

        return duplicates;
    }

    /// <summary>
    /// Convert duplicate patterns into snapshots, ranked by wasted bytes descending (count and
    /// total size tiebreak). Every candidate is a <c>System.String</c> instance by construction (the
    /// adapter only streams string-typed candidates), so <see cref="DuplicateStringSnapshot.DominantType"/>
    /// is always <c>"System.String"</c> — no method-table-to-name resolution needed, unlike the
    /// pre-retyping analyzer's more generic (but, for this analyzer, always-degenerate) lookup.
    /// </summary>
    private static IReadOnlyList<DuplicateStringSnapshot> BuildDuplicateSnapshots(List<StringLeakInfo> duplicates)
    {
        if (duplicates.Count == 0) return [];

        var list = new List<DuplicateStringSnapshot>(duplicates.Count);
        foreach (StringLeakInfo info in duplicates)
        {
            ulong wasted = info.TotalSize * (ulong)(info.Count - 1) / (ulong)info.Count;
            int avg = info.Count > 0 ? (int)Math.Min(info.TotalSize / (ulong)info.Count, int.MaxValue) : 0;
            list.Add(new DuplicateStringSnapshot(
                Preview: info.Preview ?? string.Empty,
                Count: info.Count,
                WastedBytes: wasted,
                SampleAddresses: info.SampleAddresses,
                DominantMethodTable: 0,
                DominantType: "System.String",
                FingerprintHash: info.FingerprintHash == 0 ? null : info.FingerprintHash,
                TotalSize: info.TotalSize,
                AvgSize: avg,
                SamplingSource: info.SamplingSource));
        }

        list.Sort(static (a, b) =>
        {
            int c = b.WastedBytes.CompareTo(a.WastedBytes);
            if (c != 0) return c;
            c = b.Count.CompareTo(a.Count);
            return c != 0 ? c : b.TotalSize.CompareTo(a.TotalSize);
        });

        return list;
    }

    public void Dispose() { }
}
