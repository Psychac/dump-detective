using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    internal sealed class MemoryAnalyzer : IAnalyzer
    {
        public string Name => "Memory Analysis";
        public string Category => "Memory";
        public IReadOnlyCollection<string> Tags => ["memory", "heap", "pressure"];
        public bool IsThreadSafe => true;

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MemoryAnalysisOptions options = context.AnalysisOptions.MemoryAnalysis;
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, context.Progress).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache)
        {
            return Analyze(heap, cache, new MemoryAnalysisOptions(), progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache cache, MemoryAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress)
        {
            progress?.Report(new(0, "building memory snapshot"));
            var typeStats = cache.GetOrBuildTypeStatistics(heap);

            // Read Phase 1 GlobalSizeBuckets if available (zero extra heap scan)
            long[]? globalBuckets = null;
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
                globalBuckets = heapIndex.GlobalSizeBuckets;

            return BuildDomainResult(heap, cache, typeStats, globalBuckets, options);
        }

        private static MemoryDomainResult BuildDomainResult(
            ClrHeap heap,
            IHeapAnalysisCache cache,
            Dictionary<string, CachedTypeStatistics> typeStats,
            long[]? globalSizeBuckets,
            MemoryAnalysisOptions options)
        {
            MemoryAnalysisProjectionResult projection = MemoryAnalysisProjection.Build(typeStats, globalSizeBuckets, options);
            var segmentSummaries = BuildSegmentSummaries(heap);
            double lohFragmentationRatio = CalculateLohFragmentationRatio(heap);

            static TypeSnapshot ToSnapshot(CachedTypeStatistics s, ulong retainedBytes, ulong sampleAddress)
            {
                ulong avgSize = s.Count > 0 ? s.TotalSize / (ulong)s.Count : 0;
                return new TypeSnapshot(s.TypeName, s.Count, s.TotalSize, s.LohSize,
                    AverageSize: avgSize,
                    EstimatedRetainedBytes: retainedBytes,
                    SampleAddress: sampleAddress,
                    ModuleName: string.IsNullOrWhiteSpace(s.ModuleName) ? null : s.ModuleName);
            }

            var topTypes = new List<TypeSnapshot>(projection.SelectedTypes.Count);

            if (heap.CanWalkHeap)
            {
                var sampleAddresses = new ulong[projection.SelectedTypes.Count];
                var walkCandidates = new List<(ulong Address, ulong MethodTable, ulong ShallowSize)>(projection.SelectedTypes.Count);

                for (int i = 0; i < projection.SelectedTypes.Count; i++)
                {
                    ulong sampleAddress = cache.GetSampleInstanceAddress(projection.SelectedTypes[i].TypeName) ?? 0;
                    sampleAddresses[i] = sampleAddress;
                    if (sampleAddress == 0)
                        continue;

                    ClrObject root = heap.GetObject(sampleAddress);
                    if (root.IsValid && root.Type is not null)
                        walkCandidates.Add((sampleAddress, root.Type.MethodTable, root.Size));
                }

                var visited = new HashSet<ulong>();
                IReadOnlyList<RetainedSizeResult> retainedResults;
                try
                {
                    retainedResults = RetainedSizeCandidateSelector.SelectAndCompute(
                        walkCandidates, heap, cache, visited, maxCandidatesToWalk: walkCandidates.Count);
                }
                catch
                {
                    retainedResults = Array.Empty<RetainedSizeResult>();
                }

                var retainedByAddress = new Dictionary<ulong, ulong>(retainedResults.Count);
                foreach (RetainedSizeResult r in retainedResults)
                    retainedByAddress[r.Address] = r.RetainedSize;

                for (int i = 0; i < projection.SelectedTypes.Count; i++)
                {
                    ulong retained = retainedByAddress.TryGetValue(sampleAddresses[i], out ulong r) ? r : 0;
                    topTypes.Add(ToSnapshot(projection.SelectedTypes[i], retained, sampleAddresses[i]));
                }
            }
            else
            {
                for (int i = 0; i < projection.SelectedTypes.Count; i++)
                    topTypes.Add(ToSnapshot(projection.SelectedTypes[i], retainedBytes: 0, cache.GetSampleInstanceAddress(projection.SelectedTypes[i].TypeName) ?? 0));
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
                LohFragmentationRatio: lohFragmentationRatio);
        }

        private static List<GCSegmentSummary> BuildSegmentSummaries(ClrHeap heap)
        {
            var summaries = new Dictionary<string, (ulong committed, ulong reserved, ulong used, int count)>();

            foreach (ClrSegment segment in heap.Segments)
            {
                string generation = GetGenerationLabel(segment);
                ulong committed = SegmentKindMapper.GetCommittedBytes(segment);
                ulong reserved = SegmentKindMapper.GetReservedBytes(segment);
                ulong used = GetSegmentUsedBytes(segment);

                if (summaries.TryGetValue(generation, out var current))
                {
                    summaries[generation] = (
                        committed: current.committed + committed,
                        reserved: current.reserved + reserved,
                        used: current.used + used,
                        count: current.count + 1);
                }
                else
                {
                    summaries[generation] = (committed, reserved, used, 1);
                }
            }

            var result = new List<GCSegmentSummary>(summaries.Count);
            foreach (var (gen, (committed, reserved, used, count)) in summaries.OrderBy(x => GetGenerationOrder(x.Key)))
            {
                result.Add(new GCSegmentSummary(gen, committed, reserved, used, count));
            }

            return result;
        }

        private static string GetGenerationLabel(ClrSegment segment)
        {
            return segment.Kind switch
            {
                GCSegmentKind.Generation0 => "Gen0",
                GCSegmentKind.Generation1 => "Gen1",
                GCSegmentKind.Generation2 or GCSegmentKind.Ephemeral => "Gen2",
                GCSegmentKind.Large => "LOH",
                GCSegmentKind.Pinned => "POH",
                GCSegmentKind.Frozen => "Frozen",
                _ => "Unknown"
            };
        }

        private static int GetGenerationOrder(string generation)
        {
            return generation switch
            {
                "Gen0" => 0,
                "Gen1" => 1,
                "Gen2" => 2,
                "LOH" => 3,
                "POH" => 4,
                "Frozen" => 5,
                _ => 6
            };
        }

        private static ulong GetSegmentUsedBytes(ClrSegment segment)
        {
            try
            {
                ulong used = 0;
                MemoryRange committed = segment.CommittedMemory;
                if (committed.Start < committed.End)
                    used = committed.End - committed.Start;
                return used;
            }
            catch
            {
                return 0;
            }
        }

        private static double CalculateLohFragmentationRatio(ClrHeap heap)
        {
            try
            {
                ulong lohCommitted = 0;
                ulong lohObjectBytes = 0;

                foreach (ClrSegment segment in heap.Segments)
                {
                    if (segment.Kind != GCSegmentKind.Large)
                        continue;

                    lohCommitted += SegmentKindMapper.GetCommittedBytes(segment);

                    foreach (ClrObject obj in segment.EnumerateObjects())
                    {
                        if (obj.IsValid)
                            lohObjectBytes += obj.Size;
                    }
                }

                if (lohCommitted == 0)
                    return 0;

                ulong lohFreeBytes = lohCommitted > lohObjectBytes ? lohCommitted - lohObjectBytes : 0;
                return lohFreeBytes * 100.0 / lohCommitted;
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose() { }
    }
}
