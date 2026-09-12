using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers.Gc
{
    /// <summary>
    /// Phase-2 analyzer covering §21.1 (finalizable object population) and §21.2
    /// (finalizer queue sub-graph retention and undisposed detection).
    ///
    /// Population sweep (§21.1) reads <c>heap.types</c>' per-type aggregates filtered by
    /// <see cref="HeapTypeStatistics.IsFinalizableType"/> — no full heap re-scan.
    ///
    /// Queue analysis (§21.2) reads <c>heap.finalizer-queue</c> exhaustively (no row cap); per-entry
    /// retained bytes come from the exact dominator tree (<see cref="IHeapDominatorQuery.TryGetRetainedSize"/>)
    /// when available, falling back to shallow size otherwise — no bounded BFS estimator here.
    /// </summary>
    /// <remarks>
    /// Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
    /// <see cref="IHeapFinalizerQueueQuery"/>'s <c>IsDisposableType</c>/<c>IsCriticalFinalizerType</c>
    /// are structural (interface/base-type-chain) facts, computed dump-side scoped to the finalizer
    /// queue's own bounded population — deliberately not folded into <c>heap.types</c>' whole-heap
    /// per-type dictionary, the exact mistake found and fixed in <c>BoxingAnalyzer</c>'s retyping
    /// earlier this wave. Only the disposed-field *value* read goes through <c>dump.object-fields</c>
    /// (Tier 2) directly, cached per method table the same way the pre-retyping analyzer's own
    /// <c>disposedFieldCache</c> avoided re-resolving which field name exists on a repeated type.
    ///
    /// A real, deliberate behavior extension found while retyping: the pre-retyping analyzer's
    /// no-index fallback (a full heap walk when no prebuilt index exists) is now handled by
    /// <c>heap.types</c>' own no-index fallback (<c>HeapTypeStatisticsQuery.BuildFromCachedStatistics</c>,
    /// backed by the shared <c>StatisticsCache</c> walk 5 other analyzers already use) — extended,
    /// not bypassed, to also carry <see cref="HeapTypeStatistics.IsFinalizableType"/>/per-generation
    /// counts, so this analyzer needs no fallback logic of its own at all: a single
    /// <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/> call now serves both paths.
    /// </remarks>
    public sealed class FinalizableObjectAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
    {
        private static readonly string[] DisposedFieldCandidates =
            ["_disposed", "disposed", "m_disposed", "_isDisposed", "isDisposed"];

        private const int RootPathSampleLimit = 10;

        private static readonly RootPathSearchOptions RootPathOptions = new(
            MaxCandidateNodes: 5_000, MaxCandidateDepth: 8, MaxRootExpansionDepth: 12, LargeFanoutThreshold: 100);

        public string Name => "Finalizable Object Analysis";
        public string Category => "Memory";

        public AnalyzerDomainResult? LastResult { get; private set; }

        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IHeapTypeStatisticsQuery statsQuery = context.HeapTypeStatistics
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapTypes}' capability.");
            IHeapFinalizerQueueQuery finalizerQueue = context.HeapFinalizerQueue
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapFinalizerQueue}' capability.");
            IHeapSegmentQuery segments = context.HeapSegments
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
            IDumpObjectFieldQuery dumpFields = context.DumpObjectFields
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.DumpObjectFields}' capability.");
            IHeapRootPathQuery rootPaths = context.HeapRootPaths
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapRootPaths}' capability.");
            IHeapDominatorQuery? dominators = context.HeapDominators; // optional -- Stage B may not be built

            LastResult = Analyze(statsQuery, finalizerQueue, segments, dumpFields, rootPaths, dominators, cancellationToken);
            return ValueTask.CompletedTask;
        }

        private static FinalizableObjectDomainResult Analyze(
            IHeapTypeStatisticsQuery statsQuery,
            IHeapFinalizerQueueQuery finalizerQueue,
            IHeapSegmentQuery segments,
            IDumpObjectFieldQuery dumpFields,
            IHeapRootPathQuery rootPaths,
            IHeapDominatorQuery? dominators,
            CancellationToken cancellationToken)
        {
            // ── Step 1: Population from heap.types, filtered by IsFinalizableType ────────
            IReadOnlyDictionary<string, HeapTypeStatistics> stats = statsQuery.GetTypeStatistics();

            long totalObjects = 0;
            ulong totalBytes = 0;
            long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0;
            var finalizableTypes = new List<(string Name, HeapTypeStatistics Stat)>();

            foreach (KeyValuePair<string, HeapTypeStatistics> kv in stats)
            {
                if (!kv.Value.IsFinalizableType)
                    continue;

                finalizableTypes.Add((kv.Key, kv.Value));
                totalObjects += kv.Value.InstanceCount;
                totalBytes += kv.Value.TotalSize;
                gen0 += kv.Value.Gen0Count;
                gen1 += kv.Value.Gen1Count;
                gen2 += kv.Value.Gen2Count;
                loh += kv.Value.LohCount;
            }

            finalizableTypes.Sort(static (a, b) => b.Stat.Gen2Count.CompareTo(a.Stat.Gen2Count));
            var topTypesByGen2 = new List<TypeGenerationProfile>(finalizableTypes.Count);
            foreach ((string name, HeapTypeStatistics s) in finalizableTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                topTypesByGen2.Add(new TypeGenerationProfile(
                    TypeName: name,
                    Gen0Count: s.Gen0Count,
                    Gen1Count: s.Gen1Count,
                    Gen2Count: s.Gen2Count,
                    LohCount: (int)Math.Min(s.LohCount, int.MaxValue),
                    TotalBytes: s.TotalSize,
                    Gen2Bytes: s.Gen2TotalSize,
                    IsFinalizable: true));
            }

            // ── Step 2: Finalizer queue analysis ─────────────────────────────────────────
            int queueCount = 0;
            var queueTypeCountMap = new Dictionary<string, int>(StringComparer.Ordinal);
            var criticalFinalizerTypeCountMap = new Dictionary<string, int>(StringComparer.Ordinal);
            int criticalFinalizerQueueCount = 0;
            ulong criticalFinalizerQueueBytes = 0;
            var queueSamples = new List<FinalizerQueueObjectRef>(128);

            foreach (FinalizerQueueObjectRef entry in finalizerQueue.EnumerateFinalizerQueue())
            {
                cancellationToken.ThrowIfCancellationRequested();
                queueCount++;

                string typeName = entry.IsValid ? entry.TypeDisplayName : "<unknown>";
                queueTypeCountMap[typeName] = queueTypeCountMap.GetValueOrDefault(typeName) + 1;

                if (!entry.IsValid)
                    continue;

                queueSamples.Add(entry);

                if (entry.IsCriticalFinalizerType)
                {
                    criticalFinalizerQueueCount++;
                    criticalFinalizerQueueBytes += entry.ShallowSize;
                    criticalFinalizerTypeCountMap[typeName] = criticalFinalizerTypeCountMap.GetValueOrDefault(typeName) + 1;
                }
            }

            var topQueueTypesByCount = new List<QueueTypeStatistic>(queueTypeCountMap.Count);
            foreach (KeyValuePair<string, int> kv in queueTypeCountMap)
                topQueueTypesByCount.Add(new QueueTypeStatistic(kv.Key, kv.Value));
            topQueueTypesByCount.Sort(static (a, b) => b.QueueCount.CompareTo(a.QueueCount));

            // CriticalFinalizerObject (SafeHandle, CriticalHandle, OS-handle wrappers): guaranteed
            // finalization even under partial/aborted finalization scenarios.
            var topCriticalFinalizerTypesByCount = new List<QueueTypeStatistic>(criticalFinalizerTypeCountMap.Count);
            foreach (KeyValuePair<string, int> kv in criticalFinalizerTypeCountMap)
                topCriticalFinalizerTypesByCount.Add(new QueueTypeStatistic(kv.Key, kv.Value));
            topCriticalFinalizerTypesByCount.Sort(static (a, b) => b.QueueCount.CompareTo(a.QueueCount));

            queueSamples.Sort(static (a, b) => b.ShallowSize.CompareTo(a.ShallowSize));

            var topEntries = new List<FinalizerQueueEntry>(queueSamples.Count);
            ulong totalQueueRetained = 0;
            bool hasUndisposedDisposable = false;
            bool isRetainedEstimatePartial = false;

            // Caches which disposed-field name (if any) exists on a given method table, so a
            // repeated type (e.g. a thousand leaked Timer objects) only pays for the name-guessing
            // once -- mirrors the pre-retyping analyzer's own disposedFieldCache exactly.
            var resolvedDisposedFieldNameCache = new Dictionary<ulong, string?>();

            for (int i = 0; i < queueSamples.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FinalizerQueueObjectRef entry = queueSamples[i];

                bool disposedFound = false;
                bool disposedValue = false;
                ulong mtKey = entry.Type.MethodTable ?? 0;
                if (!resolvedDisposedFieldNameCache.TryGetValue(mtKey, out string? fieldName))
                {
                    fieldName = null;
                    foreach (string candidate in DisposedFieldCandidates)
                    {
                        if (dumpFields.TryReadField(entry.Address, candidate, out FieldValue probe) && probe.Kind == FieldValueKind.Bool)
                        {
                            fieldName = candidate;
                            break;
                        }
                    }
                    resolvedDisposedFieldNameCache[mtKey] = fieldName;
                }

                if (fieldName is not null && dumpFields.TryReadField(entry.Address, fieldName, out FieldValue value) && value.Kind == FieldValueKind.Bool)
                {
                    disposedFound = true;
                    disposedValue = value.AsBool();
                }

                if (entry.IsDisposableType && disposedFound && !disposedValue)
                    hasUndisposedDisposable = true;

                ulong retained = 0;
                bool retainedIsExact = dominators is not null && dominators.TryGetRetainedSize(entry.Address, out retained);
                if (!retainedIsExact)
                {
                    retained = entry.ShallowSize;
                    isRetainedEstimatePartial = true;
                }
                totalQueueRetained += retained;

                int generation = ToLegacyGeneration(segments.GetGeneration(entry.Address));

                topEntries.Add(new FinalizerQueueEntry(
                    Address: entry.Address,
                    TypeName: entry.TypeDisplayName,
                    ShallowSize: entry.ShallowSize,
                    EstimatedRetainedBytes: retained,
                    IsDisposableType: entry.IsDisposableType,
                    DisposedFieldFound: disposedFound,
                    DisposedFieldValue: disposedValue,
                    RetainedBytesIsExact: retainedIsExact,
                    IsCriticalFinalizer: entry.IsCriticalFinalizerType,
                    Generation: generation));
            }

            topEntries.Sort(static (a, b) => b.EstimatedRetainedBytes.CompareTo(a.EstimatedRetainedBytes));

            PopulateRootPaths(rootPaths, topEntries, cancellationToken);

            return new FinalizableObjectDomainResult(
                TotalFinalizableObjects: (int)Math.Min(totalObjects, int.MaxValue),
                TotalFinalizableBytes: totalBytes,
                Gen0Count: gen0,
                Gen1Count: gen1,
                Gen2Count: gen2,
                LohCount: loh,
                FinalizerQueueCount: queueCount,
                FinalizerQueueRetainedBytes: totalQueueRetained,
                IsRetainedEstimatePartial: isRetainedEstimatePartial,
                HasUndisposedDisposableInQueue: hasUndisposedDisposable,
                CriticalFinalizerQueueCount: criticalFinalizerQueueCount,
                CriticalFinalizerQueueBytes: criticalFinalizerQueueBytes,
                TopFinalizableTypesByGen2Count: topTypesByGen2,
                TopQueueTypesByCount: topQueueTypesByCount,
                TopCriticalFinalizerTypesByCount: topCriticalFinalizerTypesByCount,
                TopQueueEntriesByRetainedSize: topEntries);
        }

        // BFS/dominator-chain root-path search is exhaustive-but-cheap only up to a point -- capped
        // to the highest-impact "top N by retained bytes" entries, same as pre-retyping. Every entry
        // still keeps its exact count/bytes/generation regardless of whether it got a sampled path.
        private static void PopulateRootPaths(
            IHeapRootPathQuery rootPaths,
            List<FinalizerQueueEntry> topEntries,
            CancellationToken cancellationToken)
        {
            int sampleCount = Math.Min(topEntries.Count, RootPathSampleLimit);
            for (int i = 0; i < sampleCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FinalizerQueueEntry entry = topEntries[i];

                bool found = rootPaths.TryFindRootPath(entry.Address, RootPathOptions, out string? formattedPath, out bool searchTruncated);

                topEntries[i] = entry with
                {
                    SampleRootPath = found ? formattedPath : null,
                    RootPathSearchTruncated = searchTruncated,
                };
            }
        }

        // Poh/Frozen/Unknown aren't one of the four buckets FormatGeneration's report table
        // distinguishes -- mapped to the same "-1 = Unknown" sentinel the pre-retyping analyzer's
        // own SegmentKindMapper.ResolveGeneration used for an unresolvable address, rather than
        // guessing which legacy raw ClrMD generation value they would have produced (GCRootAnalyzer's
        // retyping made the same conservative exclusion for these two rare tags).
        private static int ToLegacyGeneration(HeapGenerationTag tag) => tag switch
        {
            HeapGenerationTag.Gen0 => 0,
            HeapGenerationTag.Gen1 => 1,
            HeapGenerationTag.Gen2 => 2,
            HeapGenerationTag.Loh => 3,
            _ => -1,
        };

        public void Dispose() { }
    }
}
