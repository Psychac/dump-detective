using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers.Gc
{
    /// <summary>
    /// Phase-2 analyzer covering §24.1–§24.3 (weak GC handle population, WeakReference&lt;T&gt;
    /// object analysis, and ConditionalWeakTable dead-key analysis).
    ///
    /// Phase A/C (weak + dependent handle enumeration) reads <c>heap.handles</c> directly — its
    /// dump-side implementation already carries the pre-retyping analyzer's own three-tier handle
    /// source (in-memory snapshot, disk/live reader, raw <c>runtime.EnumerateHandles()</c>) unchanged.
    /// Phase B (WeakReference&lt;T&gt;/WeakReference object analysis) uses the same
    /// candidate-streaming shape <c>TimerLeakAnalyzer</c>/<c>SqlCommandAnalyzer</c>/etc. already use —
    /// <c>context.HeapObjects</c> gives every WeakReference-shaped instance directly from the
    /// pipeline's one shared heap-index scan, exact regardless of storage mode.
    /// </summary>
    /// <remarks>
    /// Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
    /// a real, deliberate behavior simplification found while retyping, not silently dropped —
    /// <see cref="WeakReferenceDomainResult.PhaseBFallbackUsed"/>/<see cref="WeakReferenceDomainResult.PhaseBSkipped"/>
    /// and the degraded (non-exact) branch of <see cref="WeakReferenceDomainResult.StaleWrapperCountIsExact"/>
    /// existed to distinguish three pre-retyping data-source states (disk-index-backed exact scan,
    /// live-heap-scan exact fallback, and a degraded one-sample-per-type extrapolation when
    /// <c>TypeAggregates</c> existed but the disk object index didn't). The candidate-streaming
    /// mechanism this retyping uses instead is backed by the pipeline's shared heap-index *scan*
    /// itself (not a post-hoc read of its persisted output), so it produces the same exact
    /// per-instance data regardless of whether the result gets stored to disk or kept in memory —
    /// the degraded case can no longer occur by construction. All three fields are always reported
    /// as their non-degraded value now; kept in the domain result (not removed) since the report/
    /// finding-generator layer already branches on them and stays correct either way.
    /// </remarks>
    [AnalyzerModule(key: "weak-reference", displayName: "Weak Reference Analysis", order: 390, tags: ["gc"])]
    public sealed class WeakReferenceAnalyzer :
        IAnalyzer, IProducesAnalyzerDomainResult, ITypedResourceCandidateSource,
        IParallelHeapIndexScanParticipant, IRequiresReachableGraphIndex, ICustomizesAnalysisContext
    {
        private const string WeakRefGenericName = "System.WeakReference`1";
        private const string WeakRefNonGenericName = "System.WeakReference";

        private readonly TypedResourceHeapIndexScanState _scanState = new();

        public string Name => "Weak Reference Analysis";
        public string Category => "Memory";

        bool ITypedResourceCandidateSource.IsCandidateType(string typeName) => IsCandidateType(typeName);

        void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _scanState.BeforeHeapIndexScan(this, context);
        void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => _scanState.OnHeapEntry(in entry);
        void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanState.OnHeapIndexScanCompleted(succeeded);
        IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new WeakReferenceAnalyzer();
        void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) =>
            _scanState.MergeFrom(partials.OfType<WeakReferenceAnalyzer>().Select(a => a._scanState));

        Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
            AnalysisContextBuilder.Build(context, analyzerOptions: null, heapObjectsOverride: new PrecomputedHeapObjectStream(_scanState.Candidates));

        public AnalyzerDomainResult? LastResult
        {
            get => _lastResult;
            // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
            // longer exists) so every LastResult consumer — pipeline or direct caller — sees
            // them populated the same way.
            private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
        }
        private AnalyzerDomainResult? _lastResult;

        internal static bool IsCandidateType(string typeName) =>
            typeName.StartsWith(WeakRefGenericName, StringComparison.Ordinal)
            || string.Equals(typeName, WeakRefNonGenericName, StringComparison.Ordinal);

        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IHeapHandleQuery handles = context.HeapHandles
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapHandles}' capability.");
            IHeapSegmentQuery segments = context.HeapSegments
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapSegments}' capability.");
            IHeapObjectLookup objectLookup = context.HeapObjectLookup
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
            IDumpObjectFieldQuery dumpFields = context.DumpObjectFields
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.DumpObjectFields}' capability.");
            IHeapObjectStream weakRefCandidates = context.HeapObjects
                ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
            IHeapDominatorQuery? dominators = context.HeapDominators; // optional -- Stage B may not be built
            IHeapReachabilityQuery? reachability = context.HeapReachability; // optional -- Stage A may not be built

            LastResult = Analyze(handles, segments, objectLookup, dumpFields, weakRefCandidates, dominators, reachability, cancellationToken);
            return ValueTask.CompletedTask;
        }

        private static WeakReferenceDomainResult Analyze(
            IHeapHandleQuery handles,
            IHeapSegmentQuery segments,
            IHeapObjectLookup objectLookup,
            IDumpObjectFieldQuery dumpFields,
            IHeapObjectStream weakRefCandidates,
            IHeapDominatorQuery? dominators,
            IHeapReachabilityQuery? reachability,
            CancellationToken cancellationToken)
        {
            // ── Phases A & C: Weak handle liveness + Dependent handle dead-key count ──
            int totalWeakHandles = 0;
            int aliveWeakTargets = 0;
            int deadWeakTargets = 0;
            ulong aliveWeakTargetsRetainedBytes = 0;

            var targetTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);
            var weakHandleKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var aliveByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var deadByKind = new Dictionary<string, int>(StringComparer.Ordinal);

            int dependentHandleDeadKeyCount = 0;
            var dependentDeadValueTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);
            int dependentDeadValueUnresolvedCount = 0;

            var aliveGenerationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int aliveGenerationUnresolvedCount = 0;

            var heldOnlyViaWeakReferenceTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);
            int heldOnlyViaWeakReferenceCount = 0;

            foreach (HeapHandleRef h in handles.EnumerateHandles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (h.Kind is HeapHandleKind.WeakShort or HeapHandleKind.WeakLong or HeapHandleKind.WeakWinRT)
                {
                    totalWeakHandles++;
                    string kindName = h.Kind.ToString();
                    IncrementDict(weakHandleKinds, kindName);

                    if (h.TargetAddress != 0 && objectLookup.TryGetObject(h.TargetAddress, out HeapObjectRef target))
                    {
                        aliveWeakTargets++;
                        IncrementDict(aliveByKind, kindName);
                        IncrementDict(targetTypeHits, target.TypeDisplayName);

                        // §24.1 P3-3: GC generation distribution of alive weak targets. Scoped to
                        // alive targets only -- a dead weak handle's address is either already
                        // cleared (0) or points at memory that may since have been reused by an
                        // unrelated object, so resolving "its" generation would attribute a
                        // misleading value rather than the collected object's actual former one.
                        string? bucket = ToGenerationBucket(segments.GetGeneration(h.TargetAddress));
                        if (bucket is null)
                            aliveGenerationUnresolvedCount++;
                        else
                            IncrementDict(aliveGenerationCounts, bucket);

                        // §24.1 P3-2: "held only via weak reference" detection. Stage A's walk
                        // starts from heap.EnumerateRoots() (which, like every ClrMD root
                        // enumeration, never includes weak or dependent handles) and follows only
                        // strong references, so IsReachableFromRoot == false for a still-alive
                        // object means nothing but the weak handle itself currently points at it --
                        // exactly the signal dotMemory calls "held only via weak reference".
                        if (reachability is not null && !reachability.IsReachableFromRoot(h.TargetAddress))
                        {
                            heldOnlyViaWeakReferenceCount++;
                            IncrementDict(heldOnlyViaWeakReferenceTypeHits, target.TypeDisplayName);
                        }

                        if (dominators is not null && dominators.TryGetRetainedSize(h.TargetAddress, out ulong retained))
                            aliveWeakTargetsRetainedBytes += retained;
                    }
                    else
                    {
                        deadWeakTargets++;
                        IncrementDict(deadByKind, kindName);
                    }
                }
                else if (h.Kind == HeapHandleKind.Dependent)
                {
                    if (h.TargetAddress == 0)
                    {
                        dependentHandleDeadKeyCount++;
                        TrackDependentDeadKeyValueType(objectLookup, h.DependentTargetAddress ?? 0, dependentDeadValueTypeHits, ref dependentDeadValueUnresolvedCount);
                    }
                }
            }

            // ── Phase B: WeakReference<T> object analysis ─────────────────────
            int weakRefObjCount = 0;
            ulong weakRefObjBytes = 0;
            int staleWrapperCount = 0;
            var staleHolderTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (HeapObjectRef candidate in weakRefCandidates.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                weakRefObjCount++;
                weakRefObjBytes += candidate.Size;

                if (dumpFields.TryReadField(candidate.Address, "m_handle", out FieldValue value)
                    && value.Kind == FieldValueKind.NativeInt
                    && value.AsNativeInt() == 0)
                {
                    staleWrapperCount++;
                    IncrementDict(staleHolderTypeHits, candidate.TypeDisplayName);
                }
            }

            // ── Build output ──────────────────────────────────────────────────
            double deadRatio = totalWeakHandles == 0
                ? 0.0
                : (double)deadWeakTargets / totalWeakHandles;

            var topTargetTypes = BuildSorted(targetTypeHits);
            var topStaleTypes = BuildSorted(staleHolderTypeHits);
            var kindLiveness = BuildKindLiveness(aliveByKind, deadByKind);
            var dependentDeadKeyValueTypes = BuildSorted(dependentDeadValueTypeHits);
            var aliveGenerationDistribution = BuildGenerationDistribution(aliveGenerationCounts);
            var heldOnlyViaWeakReferenceTopTypes = BuildSorted(heldOnlyViaWeakReferenceTypeHits);

            return new WeakReferenceDomainResult(
                TotalWeakHandles: totalWeakHandles,
                AliveWeakTargets: aliveWeakTargets,
                DeadWeakTargets: deadWeakTargets,
                DeadTargetRatio: deadRatio,
                WeakHandleKinds: BuildSorted(weakHandleKinds),
                WeakReferenceObjectCount: weakRefObjCount,
                WeakReferenceObjectBytes: weakRefObjBytes,
                StaleWrapperCount: staleWrapperCount,
                TopWeakTargetTypes: topTargetTypes,
                TopStaleWrapperHolderTypes: topStaleTypes,
                DependentHandleDeadKeyCount: dependentHandleDeadKeyCount,
                PhaseBFallbackUsed: false,
                PhaseBSkipped: false,
                Artifacts: null,
                AliveWeakTargetsRetainedBytes: aliveWeakTargetsRetainedBytes,
                AliveWeakTargetsRetainedBytesIsExact: dominators is not null,
                WeakHandleKindLiveness: kindLiveness,
                DependentDeadKeyValueTypes: dependentDeadKeyValueTypes,
                DependentDeadKeyValueTypesUnresolvedCount: dependentDeadValueUnresolvedCount,
                AliveWeakTargetGenerationDistribution: aliveGenerationDistribution,
                AliveWeakTargetGenerationUnresolvedCount: aliveGenerationUnresolvedCount,
                HeldOnlyViaWeakReferenceCount: heldOnlyViaWeakReferenceCount,
                HeldOnlyViaWeakReferenceTopTypes: heldOnlyViaWeakReferenceTopTypes,
                HeldOnlyViaWeakReferenceDetectionAvailable: reachability is not null,
                StaleWrapperCountIsExact: true);
        }

        // §24.3 P3-1: resolves a dead-key dependent handle's secondary (value) object's type.
        // Matches the pre-retyping analyzer's own TryGetObjectMetadata-based check exactly: a
        // resolved address counts as a hit (reported as "(unknown)" when its method table is 0),
        // and only a fully unresolvable address counts toward the "unresolved" bucket -- a
        // deliberately looser bar than GCHandleAnalyzer's own TryResolveDependentTypeName helper,
        // which treats a zero method table as unresolved too. The two pre-retyping analyzers had
        // genuinely different semantics here; each retyping preserves its own.
        private static void TrackDependentDeadKeyValueType(
            IHeapObjectLookup objectLookup, ulong dependentTarget,
            Dictionary<string, int> dependentDeadValueTypeHits, ref int dependentDeadValueUnresolvedCount)
        {
            if (dependentTarget != 0 && objectLookup.TryGetObject(dependentTarget, out HeapObjectRef value))
                IncrementDict(dependentDeadValueTypeHits, value.TypeDisplayName);
            else
                dependentDeadValueUnresolvedCount++;
        }

        // Poh/Frozen/Unknown aren't one of the four buckets this report distinguishes -- same
        // conservative exclusion FinalizableObjectAnalyzer's/GCRootAnalyzer's retyping already made
        // for these two rare tags, rather than guessing which legacy raw ClrMD generation value they
        // would have produced.
        private static string? ToGenerationBucket(HeapGenerationTag tag) => tag switch
        {
            HeapGenerationTag.Gen0 => "Gen0",
            HeapGenerationTag.Gen1 => "Gen1",
            HeapGenerationTag.Gen2 => "Gen2",
            HeapGenerationTag.Loh => "LOH",
            _ => null,
        };

        // ── Utility ───────────────────────────────────────────────────────────

        private static void IncrementDict(Dictionary<string, int> dict, string key)
        {
            if (dict.TryGetValue(key, out int v))
                dict[key] = v + 1;
            else
                dict[key] = 1;
        }

        // §24.1 P2-1: per-kind alive/dead breakdown, descending by total handle count. Only
        // 3 possible kinds (WeakShort/WeakLong/WeakWinRT) so no cap needed.
        private static List<HandleKindLivenessEntry> BuildKindLiveness(
            Dictionary<string, int> aliveByKind,
            Dictionary<string, int> deadByKind)
        {
            var kinds = new HashSet<string>(aliveByKind.Keys, StringComparer.Ordinal);
            kinds.UnionWith(deadByKind.Keys);

            var list = new List<HandleKindLivenessEntry>(kinds.Count);
            foreach (string kind in kinds)
            {
                aliveByKind.TryGetValue(kind, out int alive);
                deadByKind.TryGetValue(kind, out int dead);
                list.Add(new HandleKindLivenessEntry(kind, alive, dead));
            }
            list.Sort(static (a, b) => b.Total.CompareTo(a.Total));
            return list;
        }

        // §24.1 P3-3: fixed Gen0 -> Gen1 -> Gen2 -> LOH ordering (generation progression), not
        // sorted by count -- reads more naturally than the descending-count convention used
        // elsewhere in this file for open-ended type breakdowns.
        private static readonly string[] GenerationBucketOrder = ["Gen0", "Gen1", "Gen2", "LOH"];

        private static List<NameCountEntry> BuildGenerationDistribution(Dictionary<string, int> source)
        {
            var list = new List<NameCountEntry>(GenerationBucketOrder.Length);
            foreach (string bucket in GenerationBucketOrder)
            {
                if (source.TryGetValue(bucket, out int count) && count > 0)
                    list.Add(new NameCountEntry(bucket, count));
            }
            return list;
        }

        // Complete population, descending by count -- no Top-N cap (§11.2 D5); the render layer
        // slices for display. Dictionaries here are O(distinct types), cheap to sort in full.
        private static List<NameCountEntry> BuildSorted(Dictionary<string, int> source)
        {
            var list = new List<NameCountEntry>(source.Count);
            foreach (KeyValuePair<string, int> kv in source)
                list.Add(new NameCountEntry(kv.Key, kv.Value));
            list.Sort(static (a, b) => b.Count.CompareTo(a.Count));
            return list;
        }

        public void Dispose() { }
    }
}
