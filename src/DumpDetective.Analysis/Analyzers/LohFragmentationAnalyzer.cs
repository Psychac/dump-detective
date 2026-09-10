using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

using Microsoft.Diagnostics.Runtime;

using System.Buffers.Binary;

// DumpDetective.Analysis.Models and DumpDetective.Sdk.Analysis both declare HeapSegmentKind
// (deliberately identical names, see SdkSegmentKindMapper) — alias the SDK one (what this analyzer
// sources segment data as) to disambiguate.
using SdkHeapSegmentKind = DumpDetective.Sdk.Analysis.HeapSegmentKind;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase 1 retyping batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
    /// retyped onto the SDK's capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing
    /// segments/free-blocks/captured-large-objects through <see cref="IHeapSegmentQuery"/>
    /// (<c>heap.segments</c>) and exact-index type aggregates through
    /// <see cref="IHeapTypeStatisticsQuery"/> (<c>heap.types</c>). Runs through the existing pipeline
    /// via <see cref="LohFragmentationAnalyzerLegacyAdapter"/>.
    /// </summary>
    /// <remarks>
    /// The pre-retyping analyzer's own fast/fallback fork (Phase-1 disk satellite indices vs. a live
    /// per-object segment scan) mostly disappears here — <see cref="IHeapSegmentQuery.EnumerateLohFreeBlocks"/>
    /// hides that fork internally, since both modes answer the exact same question (every free
    /// block, no cap). It stays explicit for exactly one thing: the large-object list and the
    /// per-type LOH/POH consumption view, because those two paths are genuinely different features
    /// pre-retyping, not just different implementations of the same one — the disk path is a
    /// capped, observation-order top-100 sample; the live-scan path is exact and unbounded. See
    /// <see cref="IHeapSegmentQuery.HasLohSatelliteIndex"/>'s own remarks for why this can't be the
    /// same signal as <see cref="IHeapTypeStatisticsQuery.HasExactGenerationData"/>.
    /// </remarks>
    public sealed class LohFragmentationAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
    {
        // Matches LargeObjectTracker's LOH threshold so both modes select the same candidates.
        private const ulong LohThreshold = 85_000;

        // Free-gap histogram bucket boundaries (minSize inclusive, maxSize exclusive).
        private static readonly (ulong Min, ulong Max, string Label)[] s_gapBuckets =
        [
            (0,              1_024UL,            "< 1 KB"),
            (1_024UL,        65_536UL,           "1 KB – 64 KB"),
            (65_536UL,       524_288UL,          "64 KB – 512 KB"),
            (524_288UL,      1_048_576UL,        "512 KB – 1 MB"),
            (1_048_576UL,    10_485_760UL,       "1 MB – 10 MB"),
            (10_485_760UL,   104_857_600UL,      "10 MB – 100 MB"),
            (104_857_600UL,  ulong.MaxValue,     "≥ 100 MB"),
        ];

        public string Name => "LOH & POH Fragmentation Analysis";
        public string Category => "Memory";

        public AnalyzerDomainResult? LastResult { get; private set; }

        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IHeapSegmentQuery segmentQuery = context.HeapSegments
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
            IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapTypes}' capability.");

            LastResult = Analyze(segmentQuery, typeStatistics, context.Progress, cancellationToken);
            return ValueTask.CompletedTask;
        }

        private static LohFragmentationDomainResult Analyze(
            IHeapSegmentQuery segmentQuery,
            IHeapTypeStatisticsQuery typeStatistics,
            IProgress<AnalyzerProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new(0, "reading LOH/POH segment metadata"));

            var lohSegments = new List<HeapSegmentRef>();
            foreach (HeapSegmentRef segment in segmentQuery.EnumerateSegments())
            {
                if (IsLohOrPohKind(segment.Kind))
                    lohSegments.Add(segment);
            }

            if (lohSegments.Count == 0)
                return new LohFragmentationDomainResult(0, 0, 0, 0, 0, 0, 0);

            // ── Free blocks — same shape/semantics whether the capability's fast or fallback path
            // answered it; this analyzer aggregates identically either way. ──
            progress?.Report(new(0, "reading LOH/POH free blocks"));
            var freeBySegment = new Dictionary<ulong, (ulong TotalFree, ulong Largest, ulong LargestAddress, int Count)>();
            var allFreeSizes = new List<ulong>(capacity: 256);
            foreach (HeapFreeBlockRef block in segmentQuery.EnumerateLohFreeBlocks())
            {
                cancellationToken.ThrowIfCancellationRequested();
                allFreeSizes.Add(block.Size);
                if (freeBySegment.TryGetValue(block.SegmentAddress, out var existing))
                    freeBySegment[block.SegmentAddress] = block.Size > existing.Largest
                        ? (existing.TotalFree + block.Size, block.Size, block.Address, existing.Count + 1)
                        : (existing.TotalFree + block.Size, existing.Largest, existing.LargestAddress, existing.Count + 1);
                else
                    freeBySegment[block.SegmentAddress] = (block.Size, block.Size, block.Address, 1);
            }

            ulong totalAllBytes = 0, totalFreeBytes = 0, totalUsedBytes = 0, maxFreeBlock = 0;
            int totalFreeBlocks = 0;
            var segStats = new List<(ulong Address, ulong TotalBytes, double FragPct, ulong FreeBytes, ulong LargestFree, ulong LargestFreeAddress, SdkHeapSegmentKind Kind)>(lohSegments.Count);

            foreach (HeapSegmentRef segment in lohSegments)
            {
                ulong totalBytes = segment.CommittedBytes;
                ulong segFree = 0, segLargest = 0, segLargestAddress = 0;
                int segFreeCount = 0;
                if (freeBySegment.TryGetValue(segment.Start, out var fb))
                {
                    segFree = fb.TotalFree;
                    segLargest = fb.Largest;
                    segLargestAddress = fb.LargestAddress;
                    segFreeCount = fb.Count;
                }
                ulong segUsed = totalBytes > segFree ? totalBytes - segFree : 0;
                double fragPct = totalBytes == 0 ? 0 : segFree * 100.0 / totalBytes;

                totalAllBytes += totalBytes;
                totalFreeBytes += segFree;
                totalUsedBytes += segUsed;
                totalFreeBlocks += segFreeCount;
                if (segLargest > maxFreeBlock) maxFreeBlock = segLargest;

                segStats.Add((segment.Start, totalBytes, fragPct, segFree, segLargest, segLargestAddress, segment.Kind));
            }

            double overallFragPct = totalAllBytes == 0 ? 0 : totalFreeBytes * 100.0 / totalAllBytes;

            // Sort top fragmented segments descending by fragmentation %, then free bytes.
            segStats.Sort(static (a, b) =>
            {
                int cmp = b.FragPct.CompareTo(a.FragPct);
                return cmp != 0 ? cmp : b.FreeBytes.CompareTo(a.FreeBytes);
            });

            var topSegs = new List<LohSegmentSnapshot>(segStats.Count);
            var kindInputs = new List<(Models.HeapSegmentKind Kind, ulong TotalBytes, ulong FreeBytes, ulong UsedBytes, ulong LargestFreeBlock)>(segStats.Count);
            foreach (var s in segStats)
            {
                Models.HeapSegmentKind dumpKind = SdkSegmentKindMapper.ToDump(s.Kind);
                topSegs.Add(new LohSegmentSnapshot(s.Address, s.TotalBytes, s.FragPct, s.FreeBytes, s.LargestFree, s.LargestFreeAddress, dumpKind));
                ulong segUsedForKind = s.TotalBytes > s.FreeBytes ? s.TotalBytes - s.FreeBytes : 0;
                kindInputs.Add((dumpKind, s.TotalBytes, s.FreeBytes, segUsedForKind, s.LargestFree));
            }
            List<LohKindBreakdown> kindBreakdown = BuildKindBreakdown(kindInputs);

            List<FreeGapBucket> freeGapHistogram = BuildFreeGapHistogram(allFreeSizes, cancellationToken);

            // ── Large objects + per-type LOH/POH consumption — genuinely different fast/fallback
            // semantics (see this class's own remarks), so explicit here. ──
            List<LargeObjectSnapshot> topLargeObjects;
            List<LohTypeProfile> typeProfiles;

            if (segmentQuery.HasLohSatelliteIndex)
            {
                progress?.Report(new(0, "reading captured large objects"));
                topLargeObjects = new List<LargeObjectSnapshot>();
                foreach (HeapObjectRef obj in segmentQuery.EnumerateCapturedLargeObjects())
                    topLargeObjects.Add(new LargeObjectSnapshot(obj.Address, obj.TypeDisplayName, obj.Size));

                typeProfiles = new List<LohTypeProfile>();
                foreach (KeyValuePair<string, HeapTypeStatistics> kv in typeStatistics.GetTypeStatistics())
                {
                    if (kv.Value.LohCount == 0)
                        continue;
                    if (string.Equals(kv.Key, "Free", StringComparison.Ordinal))
                        continue;
                    typeProfiles.Add(new LohTypeProfile(kv.Key, (int)Math.Min(kv.Value.LohCount, int.MaxValue), kv.Value.LohSize));
                }
            }
            else
            {
                progress?.Report(new(0, "scanning LOH/POH segments"));
                var largeObjectCandidates = new List<(ulong Address, string TypeName, ulong Size)>();
                var typeAggregation = new Dictionary<string, (int Count, ulong TotalBytes)>();

                foreach (HeapSegmentRef segment in lohSegments)
                {
                    foreach (HeapObjectRef obj in segmentQuery.EnumerateObjects(segment, includeFree: false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (typeAggregation.TryGetValue(obj.TypeDisplayName, out var existing))
                            typeAggregation[obj.TypeDisplayName] = (existing.Count + 1, existing.TotalBytes + obj.Size);
                        else
                            typeAggregation[obj.TypeDisplayName] = (1, obj.Size);

                        if (obj.Size >= LohThreshold)
                        {
                            // Unbounded: LOH-threshold-sized objects (>= 85 KB) are a small fraction
                            // of any real heap's population, so keeping every candidate and sorting
                            // once at the end costs single-digit MB even on a 25 GB dump.
                            largeObjectCandidates.Add((obj.Address, obj.TypeDisplayName, obj.Size));
                        }
                    }
                }

                largeObjectCandidates.Sort(static (a, b) => b.Size.CompareTo(a.Size));
                topLargeObjects = new List<LargeObjectSnapshot>(largeObjectCandidates.Count);
                foreach (var cand in largeObjectCandidates)
                    topLargeObjects.Add(new LargeObjectSnapshot(cand.Address, cand.TypeName, cand.Size));

                typeProfiles = new List<LohTypeProfile>(typeAggregation.Count);
                foreach ((string typeName, (int count, ulong totalBytes)) in typeAggregation)
                    typeProfiles.Add(new LohTypeProfile(typeName, count, totalBytes));
            }

            typeProfiles.Sort(static (a, b) => b.TotalBytes.CompareTo(a.TotalBytes));

            return new LohFragmentationDomainResult(
                lohSegments.Count, totalAllBytes, totalFreeBytes, totalUsedBytes,
                totalFreeBlocks, overallFragPct, maxFreeBlock,
                topSegs, freeGapHistogram, topLargeObjects, typeProfiles, kindBreakdown);
        }

        private static bool IsLohOrPohKind(SdkHeapSegmentKind kind) =>
            kind == SdkHeapSegmentKind.LargeObjectHeap || kind == SdkHeapSegmentKind.PinnedObjectHeap;

        // ── LOH/POH kind breakdown ───────────────────────────────────────────────

        /// <summary>
        /// Groups per-segment stats by <see cref="Models.HeapSegmentKind"/> (Large vs. Pinned) so the
        /// report can distinguish LOH from POH fragmentation instead of only showing the combined
        /// total that both heap-scan and index paths compute.
        /// </summary>
        internal static List<LohKindBreakdown> BuildKindBreakdown(
            IEnumerable<(Models.HeapSegmentKind Kind, ulong TotalBytes, ulong FreeBytes, ulong UsedBytes, ulong LargestFreeBlock)> segments)
        {
            var byKind = new Dictionary<Models.HeapSegmentKind, (int Count, ulong TotalBytes, ulong FreeBytes, ulong UsedBytes, ulong LargestFreeBlock)>();
            foreach (var s in segments)
            {
                if (byKind.TryGetValue(s.Kind, out var acc))
                    byKind[s.Kind] = (
                        acc.Count + 1,
                        acc.TotalBytes + s.TotalBytes,
                        acc.FreeBytes + s.FreeBytes,
                        acc.UsedBytes + s.UsedBytes,
                        s.LargestFreeBlock > acc.LargestFreeBlock ? s.LargestFreeBlock : acc.LargestFreeBlock);
                else
                    byKind[s.Kind] = (1, s.TotalBytes, s.FreeBytes, s.UsedBytes, s.LargestFreeBlock);
            }

            var result = new List<LohKindBreakdown>(byKind.Count);
            foreach (var (kind, acc) in byKind)
            {
                double fragPct = acc.TotalBytes == 0 ? 0 : acc.FreeBytes * 100.0 / acc.TotalBytes;
                result.Add(new LohKindBreakdown(kind, acc.Count, acc.TotalBytes, acc.FreeBytes, acc.UsedBytes, fragPct, acc.LargestFreeBlock));
            }
            result.Sort(static (a, b) => a.Kind.CompareTo(b.Kind));
            return result;
        }

        // Matches LohFreeBlockWriter.Write which indexes both Large and Pinned segments. Kept for
        // its existing direct unit-test coverage (LohFragmentationAnalyzerTests) — no longer called
        // by this analyzer's own retyped logic, which filters via the SDK's HeapSegmentKind instead.
        internal static bool IsLohSegment(GCSegmentKind kind)
            => kind == GCSegmentKind.Large || kind == GCSegmentKind.Pinned;

        // ── Index readers ─────────────────────────────────────────────────────────

        // Kept for its existing direct unit-test coverage (LohFragmentationAnalyzerTests) — no
        // longer called by this analyzer's own retyped logic, which reads free blocks through
        // IHeapSegmentQuery.EnumerateLohFreeBlocks (backed by SdkBridge.HeapSegmentQuery's own copy
        // of this same read, since a source-neutral SDK capability can't take a raw disk path).
        internal static void ReadFreeBlocks(
            string containerPath,
            Dictionary<ulong, (ulong TotalFree, ulong Largest, ulong LargestAddress, int Count)> bySegment,
            List<ulong> allSizes,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!CacheSectionHelper.TryOpenCacheSection(containerPath, CacheSectionId.LohFreeBlocks, out Stream? stream) || stream is null)
                    return;

                using (stream)
                {
                    if (!IndexHeader.TryRead(stream, out IndexHeader header))
                        return;

                    const int RecordSize = 24; // SegmentAddress(8) | Offset(8) | Size(8)
                    Span<byte> rec = stackalloc byte[RecordSize];
                    for (long i = 0; i < header.RecordCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false) < RecordSize)
                            break;

                        ulong segAddr = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
                        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]);
                        ulong address = segAddr + offset;

                        allSizes.Add(size);
                        if (bySegment.TryGetValue(segAddr, out var ex))
                            bySegment[segAddr] = size > ex.Largest
                                ? (ex.TotalFree + size, size, address, ex.Count + 1)
                                : (ex.TotalFree + size, ex.Largest, ex.LargestAddress, ex.Count + 1);
                        else
                            bySegment[segAddr] = (size, size, address, 1);
                    }
                }
            }
            catch (Exception)
            {
                // Section not found or read failed; caller will process without free blocks.
            }
        }

        // ── Free-gap histogram ────────────────────────────────────────────────────

        internal static List<FreeGapBucket> BuildFreeGapHistogram(List<ulong> allFreeSizes, CancellationToken cancellationToken = default)
        {
            if (allFreeSizes.Count == 0) return [];

            int[] counts = new int[s_gapBuckets.Length];
            foreach (ulong size in allFreeSizes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int b = 0; b < s_gapBuckets.Length; b++)
                {
                    if (size >= s_gapBuckets[b].Min && size < s_gapBuckets[b].Max)
                    {
                        counts[b]++;
                        break;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<FreeGapBucket>(s_gapBuckets.Length);
            for (int b = 0; b < s_gapBuckets.Length; b++)
                if (counts[b] > 0)
                    result.Add(new FreeGapBucket(s_gapBuckets[b].Label, counts[b]));
            return result;
        }

        public void Dispose() { }
    }
}
