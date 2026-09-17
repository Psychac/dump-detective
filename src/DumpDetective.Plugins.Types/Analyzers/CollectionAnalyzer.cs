using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Types
{
    // TODO: Need to check why root stack frames are not showing in deep mode.
    // Suspect a bug in ReferenceChainAnalyzer's root description logic where it doesn't populate descriptions for certain stack roots.
    // This would impact the root hints shown for wasteful collections that are rooted in stacks.

    /// <summary>
    /// Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), built
    /// on <c>TypedResourceCandidateStreamingLegacyAdapter</c> like the rest of the Tier-2 wave —
    /// <see cref="IsCandidateType"/> classifies every heap object by name during the pipeline's one
    /// shared heap-index scan (exactly what this analyzer's pre-retyping <c>OnHeapEntry</c> already
    /// did per entry), so unlike most wave members there was no separate discovery predicate to
    /// invent: this is the exact discovery shape the shared adapter already assumes.
    /// </summary>
    /// <remarks>
    /// Closes two real capability gaps flagged still-open by the retyping plan:
    /// <see cref="IDumpObjectFieldQuery.EnumerateInstanceFields"/> (per-type field
    /// name+kind discovery — this analyzer resolves a *different* field name per collection kind,
    /// per distinct type, via well-known names first, then a full-field classification fallback;
    /// nothing shipped before this let a retyped analyzer ask "what fields does this type have, and
    /// what primitive kind is each one" without a real <c>ClrType</c>); and a new
    /// <see cref="IHeapReverseReferenceQuery.TryGetReverseReferences"/> overload (this analyzer's own
    /// <c>OwnerTypeHint</c> needs the reverse index's truncation signal, which the pre-existing
    /// <c>EnumerateReverseReferences</c> stream can't carry).
    ///
    /// <b>A third gap, deliberately not closed after real-dump evidence, not guessed at up front</b>:
    /// the plan flagged the pre-retyping search's known-leak-type-pattern force-expand as a real
    /// policy mismatch against <c>IHeapRootPathQuery</c>'s "never force-expand" baked-in policy.
    /// A force-expand overload was built and wired up, then removed the same session once the
    /// real-dump gate (below) hung indefinitely: <c>ReferenceChainOptions.KnownLeakTypePatterns</c>'
    /// *defaults* are <c>"System.Collections.Generic.List"</c>/<c>"...Dictionary"</c> — this
    /// analyzer's own target type family — so force-expanding matches defeats
    /// <c>IndexBackedBidirectionalSearch</c>'s hub-fanout cap almost everywhere in a real heap's
    /// object graph. See <see cref="IHeapRootPathQuery"/>'s own remarks for the full account. This
    /// analyzer now uses the same "never force-expand" policy <c>DbConnectionAnalyzer</c>/
    /// <c>TimerLeakAnalyzer</c> already use.
    ///
    /// <b>A real simplification, not a mechanical port</b>: the pre-retyping analyzer hand-rolled four
    /// nearly-identical per-kind array-shaped probes (<c>AnalyzeDictionary</c>/<c>AnalyzeList</c>/
    /// <c>AnalyzeHashSet</c>/<c>AnalyzeArrayBackedCollection</c>), differing only in field-name
    /// priority, whether a free-count field is tracked, and a fallback type-name label. They collapse
    /// into <see cref="AnalyzeArrayShapedCollection"/> here — real, since the retyped version's field
    /// layout and array-shape resolution use exactly the same capability calls for all four kinds.
    /// <c>AnalyzeQueue</c> stays separate: it alone needs head/tail-based contiguous-free-segment
    /// math (<see cref="CollectionAnalysisHelpers.ComputeQueueFreeSegments"/>, ported unchanged — pure,
    /// no ClrMD dependency).
    ///
    /// <b>A real fidelity improvement, not silently changed behavior</b>: <see cref="IHeapRootPathQuery"/>
    /// returns an already-formatted root-to-target path string (the same shape
    /// <c>DbConnectionAnalyzer</c>/<c>TimerLeakAnalyzer</c> already store verbatim as their own
    /// domain-result field), so <see cref="WastefulCollection.RootDescription"/> now carries the real
    /// path text instead of the pre-retyping analyzer's generic <c>"{rootKind}: (reference path
    /// found)"</c> placeholder, which discarded the path <c>RootPathFinder</c> had already computed.
    ///
    /// <b>One accepted, documented behavior drop</b>: <see cref="CollectionAnalysisOptions.MaxDegreeOfParallelism"/>/
    /// <see cref="CollectionAnalysisOptions.SerializeHeapAccess"/> no longer apply — the pre-retyping
    /// analyzer's own parallel-segment-walk fallback (used only when no shared heap-index scan ran)
    /// no longer exists post-retyping, the same "the degraded case can no longer occur by
    /// construction" simplification <c>WeakReferenceAnalyzer</c>'s retyping already made: every
    /// candidate now always arrives via the pipeline's one shared heap-index scan
    /// (<c>context.HeapObjects</c>), never a second hand-rolled walk.
    /// </remarks>
    [AnalyzerModule(key: "collection", displayName: "Collection Analysis", order: 240, tags: ["collections"])]
    [RequiresCapability(
        CapabilityVocabulary.DumpObjectFields,
        CapabilityVocabulary.HeapDominators,
        CapabilityVocabulary.HeapObjects,
        CapabilityVocabulary.HeapReverseReferences,
        CapabilityVocabulary.HeapSegments,
        CapabilityVocabulary.HeapTypes)]
    [OptionalCapability(CapabilityVocabulary.TraceAllocSamples)]
    [OptionalCapability(CapabilityVocabulary.TraceGcEvents)]
    public sealed class CollectionAnalyzer :
        IAnalyzer, IProducesAnalyzerDomainResult, ITypedResourceCandidateSource,
        IParallelHeapIndexScanParticipant, IRequiresReachableGraphIndex, ICustomizesAnalysisContext
    {
        private readonly TypedResourceHeapIndexScanState _scanState = new();

        public string Name => "Collection Analysis";
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

        bool ITypedResourceCandidateSource.IsCandidateType(string typeName) => IsCandidateType(typeName);

        void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _scanState.BeforeHeapIndexScan(this, context);
        void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => _scanState.OnHeapEntry(in entry);
        void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanState.OnHeapIndexScanCompleted(succeeded);
        IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new CollectionAnalyzer();
        void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) =>
            _scanState.MergeFrom(partials.OfType<CollectionAnalyzer>().Select(a => a._scanState));

        // Preserves the exact object graph the pre-migration adapter's ResolveOptions built
        // (new CollectionAnalysisOptions(), new ReferenceChainOptions()) — not this analyzer's own
        // null-ReferenceChain fallback, which only applies when AnalyzerOptions is omitted entirely.
        Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
            AnalysisContextBuilder.Build(
                context,
                new CollectionAnalyzerOptions(new CollectionAnalysisOptions(), new ReferenceChainOptions()),
                heapObjectsOverride: new PrecomputedHeapObjectStream(_scanState.Candidates));

        public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IDumpObjectFieldQuery fields = context.DumpObjectFields
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.DumpObjectFields}' capability.");
            IHeapObjectLookup objectLookup = context.HeapObjectLookup
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
            IDumpArrayShapeQuery arrayShapes = context.DumpArrayShapes
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.DumpArrayElements}' capability.");
            IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapTypes}' capability.");

            CollectionAnalyzerOptions options = context.AnalyzerOptions as CollectionAnalyzerOptions
                ?? new CollectionAnalyzerOptions(new CollectionAnalysisOptions(), null);

            var result = (CollectionDomainResult)Analyze(
                context.HeapObjects, fields, objectLookup, arrayShapes, typeStatistics,
                context.HeapSegments, context.HeapDominators, context.HeapRootPaths, context.HeapReverseReferences,
                options, cancellationToken);
            LastResult = result;

            await EmitObservationsAsync(context, objectLookup, result, cancellationToken);
        }

        /// <summary>
        /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
        /// <c>collection.wasteful</c> observation per <see cref="CollectionDomainResult.TopWastefulCollections"/>
        /// entry — already-selected as wasteful/capacity-mismatched, bounded by
        /// <c>CollectionAnalyzerOptions.TopWastefulCollectionsToShow</c>. Subject resolved via
        /// <see cref="IHeapObjectLookup"/> from the snapshot's own address, as a second pass rather
        /// than inline — <see cref="BuildDomainResult"/> already re-resolves retained bytes the same
        /// way, so this mirrors that method's own shape rather than threading the sink through it.
        /// </summary>
        private static async ValueTask EmitObservationsAsync(
            AnalysisContext context, IHeapObjectLookup objectLookup, CollectionDomainResult result, CancellationToken cancellationToken)
        {
            var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapObjects, CapabilityVocabulary.DumpObjectFields };

            foreach (WastefulCollectionSnapshot snapshot in result.TopWastefulCollections ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!objectLookup.TryGetObject(snapshot.Address, out HeapObjectRef obj))
                    continue;

                var measures = new Dictionary<string, Measure>
                {
                    ["count"] = new Measure(snapshot.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["capacity"] = new Measure(snapshot.Capacity, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["fill-rate.percent"] = new Measure(snapshot.FillRate, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["wasted.bytes"] = new Measure(snapshot.WastedMemory, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                };
                if (snapshot.RetainedBytes is ulong retained)
                    measures["retained.bytes"] = new Measure(retained, MeasureUnit.Bytes, MeasureSemantics.Absolute);

                await context.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "collection.wasteful",
                    Subjects = [obj.Type],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = measures,
                    Provenance = new Provenance
                    {
                        Artifact = context.ArtifactId!.Value,
                        AnalyzerKey = "Collection Analysis",
                        CapabilitiesUsed = capabilitiesUsed,
                        Fidelity = snapshot.RetainedBytes is not null ? FidelityLevel.Full : FidelityLevel.Degraded,
                    },
                    Confidence = snapshot.RetainedBytes is not null ? 1.0 : 0.7,
                }, cancellationToken);
            }
        }

        private static AnalyzerDomainResult Analyze(
            IHeapObjectStream? candidates,
            IDumpObjectFieldQuery fields,
            IHeapObjectLookup objectLookup,
            IDumpArrayShapeQuery arrayShapes,
            IHeapTypeStatisticsQuery typeStatistics,
            IHeapSegmentQuery? segments,
            IHeapDominatorQuery? dominators,
            IHeapRootPathQuery? rootPaths,
            IHeapReverseReferenceQuery? reverseReferences,
            CollectionAnalyzerOptions options,
            CancellationToken cancellationToken)
        {
            var stats = new CollectionStatistics();
            if (candidates is null)
                return BuildDomainResult(stats, dominators, options.Collection);

            var fieldLayoutCache = new Dictionary<string, FieldLayoutNames>(StringComparer.Ordinal);
            var wasteful = new List<WastefulCollection>();
            int topCapacity = Math.Max(1, Math.Max(options.Collection.TopWastefulCollectionsToShow, options.Collection.PathAnalysisTopN));

            var generationCounts = new Dictionary<CollectionKind, int[]>(CollectionKindCount);
            foreach (CollectionKind k in Enum.GetValues<CollectionKind>())
                generationCounts[k] = new int[4];

            var wasteCountByKind = new int[CollectionKindCount];
            var wasteBytesByKind = new ulong[CollectionKindCount];
            var wasteCountByElementType = new Dictionary<string, int>();
            var wasteBytesByElementType = new Dictionary<string, ulong>();
            int wastefulCount = 0;
            ulong totalWasted = 0;

            // Fetched exactly once, not per candidate: IHeapTypeStatisticsQuery.GetTypeStatistics()
            // rebuilds its whole per-type dictionary from the index's full TypeAggregates on every
            // call (no internal memoization) -- calling it per wasteful candidate found was a real,
            // serious perf bug caught by this retyping's own real-dump gate (see
            // ResolveElementShape's own remarks), an O(candidates * distinct-types) blowup that hung
            // a real 3.35 GB dump indefinitely. Every other retyped analyzer already fetches this
            // once up front; this one is no different.
            IReadOnlyDictionary<string, HeapTypeStatistics> typeStats = typeStatistics.GetTypeStatistics();

            foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();

                CollectionKind kind = ClassifyCollectionTypeName(candidate.TypeDisplayName);
                if (kind == CollectionKind.None)
                    continue;

                stats.TotalCollections++;
                IncrementKindTotal(stats, kind);

                // SortedSet<T> is a red-black tree, not array-backed; exclude from waste analysis to
                // avoid misleading fill rates, but it's still counted in the totals above.
                if (kind == CollectionKind.SortedSet)
                    continue;

                CountGeneration(generationCounts, kind, segments?.GetGeneration(candidate.Address) ?? HeapGenerationTag.Unknown);

                WastefulCollection? waste = kind switch
                {
                    CollectionKind.Dictionary => AnalyzeArrayShapedCollection(fields, objectLookup, arrayShapes, typeStats, GetOrBuildFieldLayout(fields, fieldLayoutCache, candidate), candidate, "Dictionary", trackFreeCount: true),
                    CollectionKind.List or CollectionKind.ImmutableArrayBuilder => AnalyzeArrayShapedCollection(fields, objectLookup, arrayShapes, typeStats, GetOrBuildFieldLayout(fields, fieldLayoutCache, candidate), candidate, "List", trackFreeCount: false),
                    CollectionKind.HashSet => AnalyzeArrayShapedCollection(fields, objectLookup, arrayShapes, typeStats, GetOrBuildFieldLayout(fields, fieldLayoutCache, candidate), candidate, "HashSet", trackFreeCount: true),
                    CollectionKind.ArrayList or CollectionKind.Stack or CollectionKind.SortedList => AnalyzeArrayShapedCollection(fields, objectLookup, arrayShapes, typeStats, GetOrBuildFieldLayout(fields, fieldLayoutCache, candidate), candidate, "Collection", trackFreeCount: false),
                    CollectionKind.Queue => AnalyzeQueue(fields, objectLookup, arrayShapes, typeStats, GetOrBuildFieldLayout(fields, fieldLayoutCache, candidate), candidate),
                    // Boxed ImmutableArray<T> has no spare capacity by construction -- inventory only.
                    _ => null,
                };

                if (waste is null || waste.WastedMemory <= options.Collection.WasteThresholdBytes)
                    continue;

                waste.Kind = kind;
                wastefulCount++;
                totalWasted += waste.WastedMemory;
                wasteCountByKind[(int)kind]++;
                wasteBytesByKind[(int)kind] += waste.WastedMemory;
                AccumulateElementTypeWaste(wasteCountByElementType, wasteBytesByElementType, waste.ElementType, waste.WastedMemory);
                AddToTopWasteful(wasteful, waste, topCapacity);
            }

            if (stats.TotalCollections == 0)
                return BuildDomainResult(stats, dominators, options.Collection);

            wasteful.Sort(static (a, b) => b.WastedMemory.CompareTo(a.WastedMemory));

            stats.WastefulCollections = wasteful;
            stats.WastefulCollectionCount = wastefulCount;
            stats.TotalWastedMemory = totalWasted;
            stats.WasteCountsByKind = BuildKindDictionary(wasteCountByKind);
            stats.WasteBytesByKind = BuildKindDictionary(wasteBytesByKind);
            stats.WasteCountsByElementType = wasteCountByElementType;
            stats.WasteBytesByElementType = wasteBytesByElementType;

            var genList = new List<CollectionGenerationStats>(generationCounts.Count);
            foreach (KeyValuePair<CollectionKind, int[]> kv in generationCounts)
                genList.Add(new CollectionGenerationStats(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
            stats.GenerationBreakdown = genList;

            PopulateRootDescriptions(objectLookup, rootPaths, reverseReferences, wasteful, options);

            return BuildDomainResult(stats, dominators, options.Collection);
        }

        private static void IncrementKindTotal(CollectionStatistics stats, CollectionKind kind)
        {
            switch (kind)
            {
                case CollectionKind.Dictionary: stats.Dictionaries++; break;
                case CollectionKind.List: stats.Lists++; break;
                case CollectionKind.ArrayList: stats.ArrayLists++; break;
                case CollectionKind.Stack: stats.Stacks++; break;
                case CollectionKind.SortedList: stats.SortedLists++; break;
                case CollectionKind.SortedSet: stats.SortedSets++; break;
                case CollectionKind.HashSet: stats.HashSets++; break;
                case CollectionKind.Queue: stats.Queues++; break;
                case CollectionKind.ImmutableArray: stats.ImmutableArrays++; break;
                case CollectionKind.ImmutableArrayBuilder: stats.ImmutableArrayBuilders++; break;
            }
        }

        private static void CountGeneration(Dictionary<CollectionKind, int[]> generationCounts, CollectionKind kind, HeapGenerationTag tag)
        {
            // Poh/Frozen/Unknown don't map onto the legacy 0/1/2/3(LOH) bucketing -- same conservative
            // exclusion GCRootAnalyzer's retyping already made for these two rare tags.
            int? idx = tag switch
            {
                HeapGenerationTag.Gen0 => 0,
                HeapGenerationTag.Gen1 => 1,
                HeapGenerationTag.Gen2 => 2,
                HeapGenerationTag.Loh => 3,
                _ => null,
            };

            if (idx is int i)
                generationCounts[kind][i]++;
        }

        private static AnalyzerDomainResult BuildDomainResult(CollectionStatistics stats, IHeapDominatorQuery? dominators, CollectionAnalysisOptions options)
        {
            int topToShow = Math.Min(options.TopWastefulCollectionsToShow, stats.WastefulCollections.Count);
            var topSnapshots = new List<WastefulCollectionSnapshot>(topToShow);

            for (int i = 0; i < topToShow; i++)
            {
                WastefulCollection w = stats.WastefulCollections[i];
                ulong? retainedBytes = dominators is not null && dominators.TryGetRetainedSize(w.Address, out ulong exactRetained)
                    ? exactRetained
                    : null;

                topSnapshots.Add(new WastefulCollectionSnapshot(
                    w.Type,
                    w.Kind,
                    w.Count,
                    w.Capacity,
                    w.FillRate,
                    w.WastedMemory,
                    w.Address,
                    w.Head,
                    w.Tail,
                    w.LargestContiguousFreeSegmentBytes,
                    w.FreeSegmentCount,
                    w.ElementSize,
                    w.ElementType,
                    w.SizeEstimateConfidence,
                    w.DetectionMethod,
                    w.RootDescription,
                    retainedBytes,
                    BuildResizeRecommendation(w.Kind, w.Count, w.Capacity, w.FillRate),
                    w.OwnerTypeHint));
            }

            return new CollectionDomainResult(
                stats.TotalCollections,
                stats.Dictionaries,
                stats.Lists,
                stats.ArrayLists,
                stats.Stacks,
                stats.SortedLists,
                stats.SortedSets,
                stats.HashSets,
                stats.Queues,
                stats.TotalWastedMemory,
                stats.WastefulCollectionCount,
                topSnapshots,
                stats.WasteCountsByKind,
                stats.WasteBytesByKind,
                stats.GenerationBreakdown,
                stats.ImmutableArrays,
                stats.ImmutableArrayBuilders,
                stats.WasteCountsByElementType,
                stats.WasteBytesByElementType);
        }

        // Below this ratio the collection is essentially empty relative to its capacity, which
        // usually means the caller over-estimated the initial size rather than the collection
        // having simply shrunk after churn — right-sizing the constructor call fixes the cause,
        // where TrimExcess only fixes the current symptom.
        private const double SparseFillRateThreshold = 10.0;

        /// <summary>
        /// Always a concrete capacity fix for the given collection, never a reachability claim —
        /// <see cref="WastefulCollection.RootDescription"/> only reflects a budget-limited search
        /// (see <see cref="PopulateRootDescriptions"/>), not proof the collection is unreachable.
        /// </summary>
        internal static string BuildResizeRecommendation(CollectionKind kind, int count, int capacity, double fillRate)
        {
            string trimApi = kind switch
            {
                CollectionKind.ArrayList => "TrimToSize()",
                CollectionKind.ImmutableArrayBuilder => "ToImmutable()",
                _ => "TrimExcess()",
            };

            if (kind == CollectionKind.ImmutableArrayBuilder)
                return $"Call Builder.{trimApi} once population is complete to release the over-allocated backing array.";

            if (fillRate < SparseFillRateThreshold && capacity > 0)
                return $"Construct with an initial capacity near {count:N0} instead of the observed {capacity:N0} — this collection is populated far below its allocated capacity.";

            return $"Call {trimApi} once population is complete to release unused capacity.";
        }

        private static Dictionary<CollectionKind, int> BuildKindDictionary(int[] countsByKind)
        {
            var result = new Dictionary<CollectionKind, int>(countsByKind.Length);
            for (int i = 0; i < countsByKind.Length; i++)
            {
                if (countsByKind[i] > 0)
                    result[(CollectionKind)i] = countsByKind[i];
            }
            return result;
        }

        private static Dictionary<CollectionKind, ulong> BuildKindDictionary(ulong[] bytesByKind)
        {
            var result = new Dictionary<CollectionKind, ulong>(bytesByKind.Length);
            for (int i = 0; i < bytesByKind.Length; i++)
            {
                if (bytesByKind[i] > 0)
                    result[(CollectionKind)i] = bytesByKind[i];
            }
            return result;
        }

        // "" is WastefulCollection.ElementType's default when the component type couldn't be
        // resolved -- bucket it under one readable label instead of a blank dictionary key/report row.
        private const string UnknownElementTypeLabel = "(unknown)";

        internal static void AccumulateElementTypeWaste(Dictionary<string, int> counts, Dictionary<string, ulong> bytes, string elementType, ulong wastedMemory)
        {
            string key = string.IsNullOrEmpty(elementType) ? UnknownElementTypeLabel : elementType;
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
            bytes.TryGetValue(key, out ulong total);
            bytes[key] = total + wastedMemory;
        }

        private static void AddToTopWasteful(List<WastefulCollection> topList, WastefulCollection candidate, int capacity)
        {
            if (topList.Count < capacity)
            {
                topList.Add(candidate);
                return;
            }

            int minIndex = 0;
            ulong minWaste = topList[0].WastedMemory;
            for (int i = 1; i < topList.Count; i++)
            {
                ulong current = topList[i].WastedMemory;
                if (current < minWaste)
                {
                    minWaste = current;
                    minIndex = i;
                }
            }

            if (candidate.WastedMemory <= minWaste)
                return;

            topList[minIndex] = candidate;
        }

        private static readonly string[] BclCollectionNamespacePrefixes =
        [
            "System.Collections.",
            "System.Collections.Generic.",
            "System.Collections.Concurrent.",
            "System.Collections.Immutable.",
        ];

        private const string ImmutableArrayTypeNamePrefix = "System.Collections.Immutable.ImmutableArray";

        private const string NestedBuilderToken = "+Builder";

        public static bool IsCandidateType(string typeName) => ClassifyCollectionTypeName(typeName) != CollectionKind.None;

        /// <summary>
        /// Maps a resolved type name onto the collection kind whose capacity probe understands it.
        /// Pure function of the name, unchanged from the pre-retyping analyzer — this is the same
        /// classification the shared <c>TypedResourceCandidateStreamingLegacyAdapter</c> now also
        /// calls via <see cref="IsCandidateType"/> to pick candidates during the shared heap-index scan.
        /// </summary>
        internal static CollectionKind ClassifyCollectionTypeName(string typeName)
        {
            // ImmutableArray<T>.Builder is the one nested type worth probing: it is the only
            // immutable-collection type with a mutable, over-allocated backing array. Every other
            // nested type (Dictionary+Entry, ConcurrentDictionary+Node, ImmutableList+Builder over
            // a tree) is an implementation detail with no capacity to reclaim. The nested type's own
            // generic-argument bracket trails "+Builder" in ClrMD's rendered name, so this must be a
            // Contains, not an EndsWith.
            if (typeName.Contains(NestedBuilderToken, StringComparison.Ordinal))
            {
                return typeName.StartsWith(ImmutableArrayTypeNamePrefix, StringComparison.Ordinal)
                    ? CollectionKind.ImmutableArrayBuilder
                    : CollectionKind.None;
            }

            if (typeName.Contains('+'))
                return CollectionKind.None;

            // Match only well-known BCL collection namespaces to avoid false positives
            // from arbitrary application types whose names happen to contain these words.
            if (!TypeNamePatternMatcher.HasAnyPrefix(typeName, BclCollectionNamespacePrefixes))
                return CollectionKind.None;

            // normalize to the outer (non-generic) type name to avoid matching nested generic args
            string shortName = TypeNamePatternMatcher.GetShortName(typeName);

            // Exclude concurrent/non-array-backed variants explicitly
            if (shortName.StartsWith("Concurrent", StringComparison.OrdinalIgnoreCase) ||
                shortName.IndexOf("BlockingCollection", StringComparison.OrdinalIgnoreCase) >= 0)
                return CollectionKind.None;

            if (string.Equals(shortName, "Dictionary", StringComparison.OrdinalIgnoreCase))
                return CollectionKind.Dictionary;
            if (string.Equals(shortName, "List", StringComparison.OrdinalIgnoreCase))
                return CollectionKind.List;
            if (string.Equals(shortName, "HashSet", StringComparison.OrdinalIgnoreCase))
                return CollectionKind.HashSet;
            if (string.Equals(shortName, "Queue", StringComparison.OrdinalIgnoreCase))
                return CollectionKind.Queue;
            if (string.Equals(shortName, "ArrayList", StringComparison.OrdinalIgnoreCase))
                return CollectionKind.ArrayList;
            if (string.Equals(shortName, "Stack", StringComparison.OrdinalIgnoreCase))
                return CollectionKind.Stack;
            if (string.Equals(shortName, "SortedList", StringComparison.OrdinalIgnoreCase))
                return CollectionKind.SortedList;
            if (string.Equals(shortName, "SortedSet", StringComparison.OrdinalIgnoreCase))
                return CollectionKind.SortedSet;
            if (string.Equals(shortName, "ImmutableArray", StringComparison.OrdinalIgnoreCase))
                return CollectionKind.ImmutableArray;

            return CollectionKind.None;
        }

        private static readonly int CollectionKindCount = Enum.GetValues<CollectionKind>().Length;

        // ── Field-layout resolution ──────────────────────────────────────────────

        /// <summary>
        /// Field *names* resolved for one distinct collection type, cached per
        /// <see cref="HeapObjectRef.TypeDisplayName"/> — the SDK-side counterpart to the pre-retyping
        /// analyzer's <c>FieldLayout</c> struct, which cached resolved <c>ClrInstanceField</c>
        /// handles per <c>MethodTable</c>. Caching field *names* instead preserves the same perf
        /// characteristic: <see cref="IDumpObjectFieldQuery.EnumerateInstanceFields"/>'s full-field
        /// classification (the expensive fallback path) still runs at most once per distinct type;
        /// every subsequent instance of that type reads by name directly via
        /// <see cref="IDumpObjectFieldQuery.TryReadField"/>, the same cost every other Tier-2 analyzer
        /// already pays per read.
        /// </summary>
        private readonly record struct FieldLayoutNames(
            string? SizeField, string? CountField, string? ItemsField, string? EntriesField, string? ArrayField,
            string? HeadField, string? TailField, string? AnyIntField, string? FreeCountField);

        /// <summary>
        /// Resolves (and caches) the well-known collection-internal field names for
        /// <paramref name="candidate"/>'s type. Mirrors the pre-retyping analyzer's
        /// <c>GetOrBuildFieldLayout</c> exactly: well-known names first, then — only if any are still
        /// missing — one fallback pass over every declared instance field, classifying candidates by
        /// declared kind (any <see cref="FieldValueKind.Int32"/> field, any array-typed field) and by
        /// substring name match.
        /// </summary>
        private static FieldLayoutNames GetOrBuildFieldLayout(IDumpObjectFieldQuery fields, Dictionary<string, FieldLayoutNames> cache, HeapObjectRef candidate)
        {
            if (cache.TryGetValue(candidate.TypeDisplayName, out FieldLayoutNames existing))
                return existing;

            IReadOnlyList<InstanceFieldDescriptor> descriptors = fields.EnumerateInstanceFields(candidate.Address);
            var byName = new HashSet<string>(descriptors.Count, StringComparer.Ordinal);
            foreach (InstanceFieldDescriptor d in descriptors)
            {
                if (d.Name.Length > 0)
                    byName.Add(d.Name);
            }

            string? Find(params ReadOnlySpan<string> names)
            {
                foreach (string n in names)
                {
                    if (byName.Contains(n))
                        return n;
                }
                return null;
            }

            string? sizeField = Find("_size", "_count", "m_count");
            string? countField = Find("_count", "m_count", "count");
            string? itemsField = Find("_items", "_entries", "_array");
            string? entriesField = Find("_entries");
            string? arrayField = Find("_array");
            string? headField = Find("_head");
            string? tailField = Find("_tail");
            string? freeCountField = Find("_freeCount");
            string? anyInt = null;

            if (sizeField is null || itemsField is null || entriesField is null || arrayField is null
                || headField is null || tailField is null || anyInt is null)
            {
                foreach (InstanceFieldDescriptor d in descriptors)
                {
                    string name = d.Name;
                    if (name.Length == 0)
                        continue;

                    if (anyInt is null && d.Kind == FieldValueKind.Int32)
                        anyInt = name;

                    if (arrayField is null && d.IsArrayTyped)
                        arrayField = name;

                    if (itemsField is null &&
                        (name.Contains("items", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("entries", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("array", StringComparison.OrdinalIgnoreCase)))
                        itemsField = name;

                    if (entriesField is null && name.Contains("entries", StringComparison.OrdinalIgnoreCase))
                        entriesField = name;

                    if (headField is null && name.Contains("head", StringComparison.OrdinalIgnoreCase))
                        headField = name;

                    if (tailField is null && name.Contains("tail", StringComparison.OrdinalIgnoreCase))
                        tailField = name;

                    if (sizeField is null &&
                        (name.Contains("size", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("count", StringComparison.OrdinalIgnoreCase)))
                        sizeField = name;
                }
            }

            string? resolvedItems = itemsField ?? arrayField;
            string? resolvedEntries = entriesField ?? itemsField ?? arrayField;
            string? resolvedSize = sizeField ?? countField ?? anyInt;
            string? resolvedCount = countField ?? sizeField ?? anyInt;

            var layout = new FieldLayoutNames(resolvedSize, resolvedCount, resolvedItems, resolvedEntries, arrayField, headField, tailField, anyInt, freeCountField);
            cache[candidate.TypeDisplayName] = layout;
            return layout;
        }

        // ── Per-kind waste probes ─────────────────────────────────────────────────

        /// <summary>
        /// Generic probe for every collection kind shaped as "an int size/count field plus an
        /// items/entries array field" — Dictionary/HashSet (which also track a free-slot count) and
        /// List/ArrayList/Stack/SortedList/ImmutableArrayBuilder (which don't). Resolves the backing
        /// array's shape via <see cref="IDumpArrayShapeQuery.TryGetArrayShape"/> for its length and
        /// <see cref="IHeapTypeStatisticsQuery"/>'s array-shape fields (shipped for
        /// <c>ArrayAnalyzer</c>) for its component type/size — both already-shipped Tier-1/Tier-2
        /// primitives, no new capability needed for this part.
        /// </summary>
        private static WastefulCollection? AnalyzeArrayShapedCollection(
            IDumpObjectFieldQuery fields, IHeapObjectLookup objectLookup, IDumpArrayShapeQuery arrayShapes, IReadOnlyDictionary<string, HeapTypeStatistics> typeStats,
            FieldLayoutNames layout, HeapObjectRef candidate, string fallbackTypeLabel, bool trackFreeCount)
        {
            string? countFieldName = layout.CountField ?? layout.SizeField ?? layout.AnyIntField;
            string? itemsFieldName = layout.ItemsField ?? layout.EntriesField ?? layout.ArrayField;
            if (countFieldName is null || itemsFieldName is null)
                return null;

            if (!fields.TryReadField(candidate.Address, countFieldName, out FieldValue countValue) || countValue.Kind != FieldValueKind.Int32)
                return null;
            int count = Math.Max(0, countValue.AsInt32());

            int freeCount = 0;
            if (trackFreeCount && layout.FreeCountField is string freeCountFieldName
                && fields.TryReadField(candidate.Address, freeCountFieldName, out FieldValue freeCountValue)
                && freeCountValue.Kind == FieldValueKind.Int32)
            {
                freeCount = Math.Max(0, freeCountValue.AsInt32());
            }
            int liveCount = Math.Max(0, count - freeCount);

            if (!fields.TryReadField(candidate.Address, itemsFieldName, out FieldValue itemsValue) || itemsValue.Kind != FieldValueKind.ObjectReference)
                return null;
            ulong itemsAddress = itemsValue.AsObjectAddress();
            // Null/uninitialized backing array = empty collection never populated, no waste.
            if (itemsAddress == 0 || !objectLookup.TryGetObject(itemsAddress, out HeapObjectRef itemsObj))
                return null;

            if (!arrayShapes.TryGetArrayShape(itemsAddress, out int capacity, out int rank) || rank != 1)
                return null;

            // No waste if fully packed or empty.
            if (capacity <= 0 || liveCount >= capacity)
                return null;

            double fillRate = (liveCount / (double)capacity) * 100;
            (ulong elementSize, string elementType, string confidence) = ResolveElementShape(typeStats, itemsObj, capacity);
            ulong wastedMemory = CollectionAnalysisHelpers.ComputeWastedMemoryFromSlots(capacity, liveCount, elementSize);

            return new WastefulCollection
            {
                Address = candidate.Address,
                Type = candidate.TypeDisplayName.Length > 0 ? candidate.TypeDisplayName : fallbackTypeLabel,
                Count = liveCount,
                Capacity = capacity,
                FillRate = fillRate,
                WastedMemory = wastedMemory,
                ElementSize = elementSize,
                ElementType = elementType,
                SizeEstimateConfidence = confidence,
                DetectionMethod = itemsFieldName,
                FreeEntryCount = trackFreeCount && freeCount > 0 ? freeCount : null,
            };
        }

        private static WastefulCollection? AnalyzeQueue(
            IDumpObjectFieldQuery fields, IHeapObjectLookup objectLookup, IDumpArrayShapeQuery arrayShapes, IReadOnlyDictionary<string, HeapTypeStatistics> typeStats,
            FieldLayoutNames layout, HeapObjectRef candidate)
        {
            // Common field names in BCL: _array, _head, _tail, _size (.NET Core/Framework varies).
            string? arrayFieldName = layout.ArrayField ?? layout.ItemsField ?? layout.EntriesField;
            string? sizeFieldName = layout.SizeField ?? layout.CountField ?? layout.AnyIntField;
            if (arrayFieldName is null || sizeFieldName is null)
                return null;

            if (!fields.TryReadField(candidate.Address, arrayFieldName, out FieldValue arrayValue) || arrayValue.Kind != FieldValueKind.ObjectReference)
                return null;
            ulong arrayAddress = arrayValue.AsObjectAddress();
            if (arrayAddress == 0 || !objectLookup.TryGetObject(arrayAddress, out HeapObjectRef arrayObj))
                return null;

            if (!arrayShapes.TryGetArrayShape(arrayAddress, out int capacity, out int rank) || rank != 1)
                return null;

            if (!fields.TryReadField(candidate.Address, sizeFieldName, out FieldValue sizeValue) || sizeValue.Kind != FieldValueKind.Int32)
                return null;
            int size = Math.Max(0, sizeValue.AsInt32());

            // head/tail are best-effort for diagnostics only -- array + size are the only fields
            // required to compute waste.
            int? head = layout.HeadField is string headFieldName
                && fields.TryReadField(candidate.Address, headFieldName, out FieldValue headValue) && headValue.Kind == FieldValueKind.Int32
                ? headValue.AsInt32()
                : null;
            int? tail = layout.TailField is string tailFieldName
                && fields.TryReadField(candidate.Address, tailFieldName, out FieldValue tailValue) && tailValue.Kind == FieldValueKind.Int32
                ? tailValue.AsInt32()
                : null;

            // No waste if full or empty.
            if (capacity <= 0 || size >= capacity)
                return null;

            double fillRate = (size / (double)capacity) * 100;
            (ulong elementSize, string elementType, string confidence) = ResolveElementShape(typeStats, arrayObj, capacity);

            ulong wastedMemory = CollectionAnalysisHelpers.ComputeWastedMemoryFromSlots(capacity, size, elementSize);
            (int freeSegments, int largestFreeSlots) = CollectionAnalysisHelpers.ComputeQueueFreeSegments(capacity, size, head);
            ulong largestFreeBytes = (ulong)largestFreeSlots * elementSize;

            return new WastefulCollection
            {
                Address = candidate.Address,
                Type = candidate.TypeDisplayName.Length > 0 ? candidate.TypeDisplayName : "Queue",
                Count = size,
                Capacity = capacity,
                FillRate = fillRate,
                WastedMemory = wastedMemory,
                ElementSize = elementSize,
                ElementType = elementType,
                SizeEstimateConfidence = confidence,
                DetectionMethod = arrayFieldName,
                Head = head,
                Tail = tail,
                FreeSegmentCount = freeSegments,
                LargestContiguousFreeSegmentBytes = largestFreeBytes,
            };
        }

        /// <summary>
        /// Resolves a backing array's element size/type-name/confidence from the already-shipped
        /// <c>heap.types</c> array-shape fields (added for <c>ArrayAnalyzer</c>'s retyping) — falls
        /// back to <paramref name="arrayObj"/>'s own total byte size divided by its element count
        /// when the array's type couldn't be resolved (matches the pre-retyping analyzer's own
        /// "unknown component type" fallback).
        /// </summary>
        /// <remarks>
        /// Takes the already-fetched <paramref name="typeStats"/> dictionary, not
        /// <see cref="IHeapTypeStatisticsQuery"/> itself — a real, serious perf bug caught by this
        /// retyping's own real-dump gate: <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/>
        /// rebuilds its whole per-type dictionary from the index's full <c>TypeAggregates</c> on
        /// every call, no internal memoization. This method runs once per wasteful candidate found
        /// (thousands, on a real dump) — calling <c>GetTypeStatistics()</c> here directly (as this
        /// analyzer originally did) meant rebuilding that dictionary once per candidate, an
        /// O(candidates * distinct-types) blowup that hung a real 3.35 GB dump indefinitely instead
        /// of the &lt;10s every other retyped analyzer's real-dump gate measured.
        /// </remarks>
        private static (ulong ElementSize, string ElementType, string Confidence) ResolveElementShape(
            IReadOnlyDictionary<string, HeapTypeStatistics> typeStats, HeapObjectRef arrayObj, int capacity)
        {
            bool hasShape = typeStats.TryGetValue(arrayObj.TypeDisplayName, out HeapTypeStatistics arrayStats)
                && arrayStats.IsArray && arrayStats.ArrayElementTypeName.Length > 0;

            if (hasShape)
                return ((ulong)arrayStats.ArrayElementSizeBytes, arrayStats.ArrayElementTypeName, "High");

            ulong fallbackElementSize = capacity > 0 ? arrayObj.Size / (ulong)capacity : 0;
            return (fallbackElementSize, string.Empty, "Low");
        }

        // ── Post-scan enrichment: owner hints + root-path search, top-N only ─────

        /// <summary>
        /// One indexed "who points at this?" lookup (P3-4, docs/analysis/phase1/collection-analyzer-audit.md)
        /// — not a traversal, so it's cheap enough to run for every top-N item regardless of the
        /// root-path search's own outcome. The reverse index has no notion of which parent is the
        /// "real" owner when an object has more than one, so a single arbitrary parent is never
        /// reported as if it were definitive — ambiguity (multiple parents, or a truncated result the
        /// index couldn't fully extract) is surfaced explicitly instead.
        /// </summary>
        private static string? ResolveOwnerTypeHint(IHeapObjectLookup objectLookup, IHeapReverseReferenceQuery? reverseReferences, ulong address)
        {
            if (reverseReferences is null)
                return null;

            if (!reverseReferences.TryGetReverseReferences(address, out IReadOnlyList<ulong> parents, out bool truncated) || parents.Count == 0)
                return null;

            string? firstOwnerType = objectLookup.TryGetObject(parents[0], out HeapObjectRef owner) ? owner.TypeDisplayName : null;
            return FormatOwnerTypeHint(parents.Count, truncated, firstOwnerType);
        }

        /// <summary>
        /// Ambiguity-handling logic isolated from the reverse-index/lookup calls above so it's
        /// directly unit-testable — the reverse index has no notion of which parent is the "real"
        /// owner, so a count &gt; 1 (or a truncated extraction) is always surfaced explicitly
        /// rather than silently reporting one arbitrary parent as definitive.
        /// </summary>
        internal static string? FormatOwnerTypeHint(int parentCount, bool truncated, string? firstOwnerType)
        {
            if (firstOwnerType is null)
                return null;

            if (parentCount == 1 && !truncated)
                return firstOwnerType;

            // truncated means the index hit its fanout cap extracting parents — parentCount is a
            // lower bound on the real total, not the true count, so mark it as such.
            string countLabel = truncated ? $"{parentCount}+" : parentCount.ToString();
            return $"{countLabel} referrers, e.g. {firstOwnerType}";
        }

        // Populate root descriptions/owner hints for the top-N items only, after the scan is
        // complete. This avoids the catastrophic O(n * heap-walk) cost of doing it per item during
        // scanning.
        //
        // Deliberately never force-expands known-leak-pattern types the way the pre-retyping
        // analyzer's own RootPathFinder call did -- found and fixed as a real, previously-latent
        // perf bug during this retyping's own real-dump gate (no prior real-dump test exercised this
        // analyzer's root-path search at all): ReferenceChainOptions.KnownLeakTypePatterns' *defaults*
        // are "System.Collections.Generic.List"/"...Dictionary", i.e. exactly this analyzer's own
        // target type family, so force-expanding matches defeats IndexBackedBidirectionalSearch's
        // hub-fanout cap almost everywhere in a real heap's object graph -- hung a real 3.35 GB dump
        // indefinitely. Same "never force-expand" policy IHeapRootPathQuery's only remaining overload
        // already bakes in for DbConnectionAnalyzer/TimerLeakAnalyzer.
        private static void PopulateRootDescriptions(
            IHeapObjectLookup objectLookup, IHeapRootPathQuery? rootPaths, IHeapReverseReferenceQuery? reverseReferences,
            List<WastefulCollection> wasteful, CollectionAnalyzerOptions options)
        {
            if (wasteful.Count == 0 || options.Collection.PathAnalysisTopN <= 0 || options.ReferenceChain is null)
                return;

            ReferenceChainOptions refChainOptions = options.ReferenceChain;
            int topN = Math.Min(options.Collection.PathAnalysisTopN, wasteful.Count);

            var searchOptions = new RootPathSearchOptions(
                refChainOptions.MaxCandidateNodes, refChainOptions.MaxCandidateDepth,
                refChainOptions.MaxRootExpansionDepth, refChainOptions.LargeFanoutThreshold);

            for (int i = 0; i < topN; i++)
            {
                WastefulCollection item = wasteful[i];

                // Independent of the root-path search below -- a single indexed lookup, not a
                // traversal -- so it's still worth having even when the search times out or misses.
                item.OwnerTypeHint = ResolveOwnerTypeHint(objectLookup, reverseReferences, item.Address);

                if (!string.IsNullOrEmpty(item.RootDescription))
                    continue;

                if (rootPaths is null)
                    continue;

                bool found = rootPaths.TryFindRootPath(item.Address, searchOptions, out string? formattedPath, out _);
                item.RootDescription = found ? formattedPath : "No root path found (within budget)";
            }
        }

        public void Dispose() { }
    }

    internal class CollectionStatistics
    {
        public int TotalCollections { get; set; }
        public int Dictionaries { get; set; }
        public int Lists { get; set; }
        public int ArrayLists { get; set; }
        public int Stacks { get; set; }
        public int SortedLists { get; set; }
        public int SortedSets { get; set; }
        public int HashSets { get; set; }
        public int Queues { get; set; }
        public int ImmutableArrays { get; set; }
        public int ImmutableArrayBuilders { get; set; }
        public ulong TotalWastedMemory { get; set; }
        public int WastefulCollectionCount { get; set; }
        public List<WastefulCollection> WastefulCollections { get; set; } = new();
        public IReadOnlyDictionary<CollectionKind, int> WasteCountsByKind { get; set; } = new Dictionary<CollectionKind, int>();
        public IReadOnlyDictionary<CollectionKind, ulong> WasteBytesByKind { get; set; } = new Dictionary<CollectionKind, ulong>();
        public IReadOnlyDictionary<string, int> WasteCountsByElementType { get; set; } = new Dictionary<string, int>();
        public IReadOnlyDictionary<string, ulong> WasteBytesByElementType { get; set; } = new Dictionary<string, ulong>();
        public IReadOnlyList<CollectionGenerationStats>? GenerationBreakdown { get; set; }
    }

    internal class WastefulCollection
    {
        public ulong Address { get; set; }
        public string Type { get; set; } = string.Empty;
        public CollectionKind Kind { get; set; }
        public int Count { get; set; }
        public int Capacity { get; set; }
        public double FillRate { get; set; }
        public ulong WastedMemory { get; set; }
        public ulong ElementSize { get; set; }
        public string ElementType { get; set; } = string.Empty;
        public string SizeEstimateConfidence { get; set; } = "Unknown";
        public string DetectionMethod { get; set; } = string.Empty;
        public string? RootDescription { get; set; }
        /// <summary>Immediate-parent hint from a single reverse-index lookup — cheap alternative
        /// to <see cref="RootDescription"/>'s full BFS. See <see cref="CollectionAnalyzer.PopulateRootDescriptions"/>.</summary>
        public string? OwnerTypeHint { get; set; }
        public int? FreeEntryCount { get; set; }
        // Queue-specific diagnostics
        public int? Head { get; set; }
        public int? Tail { get; set; }
        public ulong? LargestContiguousFreeSegmentBytes { get; set; }
        public int? FreeSegmentCount { get; set; }
    }
}
