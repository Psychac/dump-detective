using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

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
    [AnalyzerModule(key: "finalizable-object", displayName: "Finalizable Object Analysis", order: 340, tags: ["gc"])]
    [RequiresCapability(
        CapabilityVocabulary.DumpObjectFields,
        CapabilityVocabulary.HeapDominators,
        CapabilityVocabulary.HeapFinalizerQueue,
        CapabilityVocabulary.HeapSegments,
        CapabilityVocabulary.HeapTypes)]
    [OptionalCapability(CapabilityVocabulary.TraceGcEvents)]
    public sealed class FinalizableObjectAnalyzer :
        IAnalyzer, IProducesAnalyzerDomainResult, IRequiresReachableGraphIndex, IRequiresDominatorTreeIndex
    {
        private static readonly string[] DisposedFieldCandidates =
            ["_disposed", "disposed", "m_disposed", "_isDisposed", "isDisposed"];

        private const int RootPathSampleLimit = 10;

        private static readonly RootPathSearchOptions RootPathOptions = new(
            MaxCandidateNodes: 5_000, MaxCandidateDepth: 8, MaxRootExpansionDepth: 12, LargeFanoutThreshold: 100);

        public string Name => "Finalizable Object Analysis";
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

            LastResult = await Analyze(statsQuery, finalizerQueue, segments, dumpFields, rootPaths, dominators, context, cancellationToken);
        }

        /// <summary>
        /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): emits one
        /// <c>finalizer.queue-entry</c> observation per valid finalizer-queue entry, inline in the
        /// same loop that builds <see cref="FinalizableObjectDomainResult.TopQueueEntriesByRetainedSize"/>
        /// — unlike <c>GCRootAnalyzer</c>/<c>GCHandleAnalyzer</c>'s second-pass approach, this loop is
        /// the only place <see cref="FinalizerQueueObjectRef.Type"/> is still in scope; the domain
        /// result only carries a display-name string, not the <see cref="Sdk.Identity.TypeRef"/>
        /// itself. Raw facts only: retained bytes and whether it's disposed-but-live — no
        /// severity/classification (observation-and-correlation-model.md § 2a).
        /// </summary>
        private static async ValueTask<FinalizableObjectDomainResult> Analyze(
            IHeapTypeStatisticsQuery statsQuery,
            IHeapFinalizerQueueQuery finalizerQueue,
            IHeapSegmentQuery segments,
            IDumpObjectFieldQuery dumpFields,
            IHeapRootPathQuery rootPaths,
            IHeapDominatorQuery? dominators,
            AnalysisContext observationContext,
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
            var populationCapabilities = new HashSet<Capability> { CapabilityVocabulary.HeapTypes };
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

                (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(name);
                await observationContext.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "type.finalizable-population",
                    Subjects = [new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity }],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["instance.count"] = new Measure(s.InstanceCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["shallow.bytes"] = new Measure(s.TotalSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                        ["gen2.count"] = new Measure(s.Gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = observationContext.ArtifactId!.Value,
                        AnalyzerKey = "Finalizable Object Analysis",
                        CapabilitiesUsed = populationCapabilities,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                }, cancellationToken);
            }

            // Whole-run scalar — no entity subject, same "no natural per-entity anchor" pattern as
            // gc.generation-composition. total.count/total.bytes/gen2.count are reconstructable by
            // summing type.finalizable-population's own per-type measures above (that stream is
            // exhaustive, not capped), but the differ can't do that grouping — same pragmatic
            // direct-emit call made throughout Phase 5 step 4's Bucket D work (e.g.
            // type.array-population, infra.timer-population).
            await observationContext.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "gc.finalizable-composition",
                Subjects = [],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["total.count"] = new Measure(totalObjects, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["total.bytes"] = new Measure(totalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2.count"] = new Measure(gen2, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    AnalyzerKey = "Finalizable Object Analysis",
                    CapabilitiesUsed = populationCapabilities,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);

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

            var observationCapabilities = new HashSet<Capability>
            {
                CapabilityVocabulary.HeapFinalizerQueue, CapabilityVocabulary.HeapSegments, CapabilityVocabulary.HeapTypes,
            };
            FidelityLevel observationFidelity = dominators is not null ? FidelityLevel.Full : FidelityLevel.Degraded;

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

                await observationContext.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "finalizer.queue-entry",
                    Subjects = [entry.Type],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["retained.bytes"] = new Measure(retained, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                        ["shallow.bytes"] = new Measure(entry.ShallowSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                        // 1.0 = disposed-but-still-queued, 0.0 = not in that state — a raw boolean
                        // fact, not a leak judgment (that's a synthesis-rule call, Phase 5 step 2+).
                        ["disposed-but-live"] = new Measure(entry.IsDisposableType && disposedFound && !disposedValue ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["is-critical-finalizer"] = new Measure(entry.IsCriticalFinalizerType ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = observationContext.ArtifactId!.Value,
                        AnalyzerKey = "Finalizable Object Analysis",
                        CapabilitiesUsed = observationCapabilities,
                        Fidelity = observationFidelity,
                    },
                    Confidence = retainedIsExact ? 1.0 : 0.7,
                }, cancellationToken);

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

            // Whole-run scalar — no entity subject, same "no natural per-type anchor" pattern as
            // gc.generation-composition. queue.count includes invalid ("<unknown>"-typed) entries
            // that finalizer.queue-entry never observes individually (they have no resolvable Type
            // to use as a Subject), so it cannot be reconstructed by counting those per-entry
            // observations alone; retained.bytes/critical.count/critical.bytes/pressure.ratio (added
            // Phase 5 step 4 for FinalizableObjectTrendComparer, Bucket D of
            // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md) have the same
            // problem — retained.bytes in particular is per-entry Full/Degraded fidelity depending on
            // dominator availability, exact totals only, not summable from a capped display list.
            // pressure.ratio mirrors FinalizableObjectDomainResult.QueuePressureRatio's own formula
            // exactly (FinalizerQueueCount / TotalFinalizableObjects) rather than reading the
            // not-yet-constructed record.
            await observationContext.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "finalizer.queue-population",
                Subjects = [],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["queue.count"] = new Measure(queueCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["retained.bytes"] = new Measure(totalQueueRetained, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["critical.count"] = new Measure(criticalFinalizerQueueCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["critical.bytes"] = new Measure(criticalFinalizerQueueBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["pressure.ratio"] = new Measure(
                        totalObjects > 0 ? (double)queueCount / totalObjects : 0.0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                },
                Provenance = new Provenance
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    AnalyzerKey = "Finalizable Object Analysis",
                    CapabilitiesUsed = observationCapabilities,
                    Fidelity = observationFidelity,
                },
                // Mirrors finalizer.queue-entry's own exact-vs-estimated split: retained.bytes here
                // is an aggregate over the same per-entry values, so it inherits the same partial
                // uncertainty isRetainedEstimatePartial already tracks (queue.count/critical.count/
                // pressure.ratio don't depend on dominators at all, but one shared Confidence for the
                // whole observation is the existing convention, same as finalizer.queue-entry itself).
                Confidence = isRetainedEstimatePartial ? 0.7 : 1.0,
            }, cancellationToken);

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
