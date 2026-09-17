using CoreGenerationTag = DumpDetective.Core.Enums.GenerationTag;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Scans the heap for timer-related objects that commonly accumulate when timers are not disposed.
///
/// Covered framework types:
///   - System.Threading.Timer
///   - System.Timers.Timer
///   - System.Threading.TimerQueueTimer / TimerHolder
/// </summary>
/// <remarks>
/// Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), seventh
/// consumer of <c>TypedResourceCandidateStreamingLegacyAdapter</c> and (unlike the rest of the
/// typed-resource quartet) the only member whose pre-retyping design used a completely different
/// discovery shape: <c>TypedResourceScanDriver.DiscoverCandidates</c> (type-aggregate counts, no
/// per-instance data) for the main tally, plus an entirely separate <em>second full heap pass</em>
/// (<c>BuildIntervalHistogram</c>, via <c>cache.EnumerateIndexedEntriesAsTuples()</c>/live-heap
/// fallback) purely to read <c>TimerQueueTimer._period</c> per instance for the interval histogram.
/// Retyped onto the single shared candidate-stream scan every other typed-resource-quartet member
/// already uses — <c>context.HeapObjects</c> gives every timer-shaped instance directly, so the
/// interval histogram is now built in the same pass as the main tally, and per-type sample addresses
/// (needed for <c>PopulateEvidence</c>) come from the first candidate encountered per type instead of
/// a separate <c>IHeapAnalysisCache.GetSampleInstanceAddress</c> lookup. Same observable output,
/// fewer heap passes — not a behavior change.
///
/// <see cref="PeriodFieldNames"/>/<see cref="TimerCallbackFieldNames"/> both gained a second,
/// .NET-Framework field name (<c>m_period</c>/<c>m_timerCallback</c>) the pre-retyping analyzer never
/// tried at all — decompiled <c>System.Threading.TimerQueueTimer</c> from both the real .NET
/// Framework <c>mscorlib.dll</c> (GAC) and every modern .NET <c>System.Private.CoreLib.dll</c>
/// checked (7/8/9/10) to confirm: the field is always genuinely <c>uint</c>, never <c>long</c>, and
/// its declared name changed from the Framework's Hungarian-notation <c>m_period</c>/
/// <c>m_timerCallback</c> to modern .NET's <c>_period</c>/<c>_timerCallback</c>. The pre-retyping
/// analyzer only ever tried the modern name, so it was silently blind to <c>_period</c> on any
/// .NET Framework dump. Its <c>Read&lt;int&gt;</c>-then-<c>Read&lt;long&gt;</c> dual-width fallback
/// was likewise unverified: since the field is genuinely <c>uint</c> everywhere checked, the
/// <c>long</c> fallback path was dead code, and <c>uint.MaxValue</c> (the real "infinite" sentinel)
/// only round-tripped correctly through a signed <c>Read&lt;int&gt;</c> by two's-complement
/// coincidence. Fixed properly with a genuine <see cref="FieldValueKind.UInt32"/> read instead of
/// relying on that coincidence — see <see cref="TryReadPeriod"/>.
/// </remarks>
[AnalyzerModule(key: "timer-leak", displayName: "Timer Leak Analysis", order: 450, tags: ["infra", "timers"])]
[RequiresCapability(
    CapabilityVocabulary.DumpObjectFields,
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapSegments)]
[OptionalCapability(CapabilityVocabulary.TraceThreadTimeline)]
public sealed class TimerLeakAnalyzer :
    IAnalyzer, IProducesAnalyzerDomainResult, ITypedResourceCandidateSource,
    IParallelHeapIndexScanParticipant, IRequiresReachableGraphIndex, ICustomizesAnalysisContext
{
    private readonly TypedResourceHeapIndexScanState _scanState = new();

    public string Name => "Timer Leak Analysis";
    public string Category => "Infrastructure";

    bool ITypedResourceCandidateSource.IsCandidateType(string typeName) => IsCandidateType(typeName);

    void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _scanState.BeforeHeapIndexScan(this, context);
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => _scanState.OnHeapEntry(in entry);
    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanState.OnHeapIndexScanCompleted(succeeded);
    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new TimerLeakAnalyzer();
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) =>
        _scanState.MergeFrom(partials.OfType<TimerLeakAnalyzer>().Select(a => a._scanState));

    Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
        AnalysisContextBuilder.Build(context, analyzerOptions: null, heapObjectsOverride: new PrecomputedHeapObjectStream(_scanState.Candidates));

    private static readonly string[] OtherTimerNamespacePrefixes = ["System.Threading.", "System.Timers."];
    private static readonly string[] OtherTimerTokens = ["Timer"];
    private static readonly string[] ClrInternalTimerTypes =
    [
        "System.Threading.TimerQueue",
        "System.Threading.TimerThread",
    ];

    private static readonly string[] PeriodFieldNames = ["_period", "m_period"];
    private static readonly string[] TimerCallbackFieldNames = ["_timerCallback", "m_timerCallback"];

    // Only System.Threading.TimerQueueTimer exposes the underlying `_period`; buckets are fixed
    // categories (not sorted by count) so the table reads left-to-right from tightest to loosest.
    private static readonly string[] IntervalBucketLabels = ["< 100 ms", "100 ms – 1 s", "> 1 s", "Infinite"];

    private static readonly RootPathSearchOptions RootPathOptions = new(
        MaxCandidateNodes: 5_000, MaxCandidateDepth: 8, MaxRootExpansionDepth: 12, LargeFanoutThreshold: 100);

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    internal static bool IsCandidateType(string typeName) => ClassifyType(typeName) != TimerObjectCategory.None;

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");
        IHeapSegmentQuery segments = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapObjects}' capability.");
        IHeapRootPathQuery rootPaths = context.HeapRootPaths
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRootPaths}' capability.");

        LastResult = await Analyze(fields, segments, objectLookup, rootPaths, context.HeapObjects, context, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
    /// <c>infra.timer</c> observation per <see cref="TimerLeakDomainResult.ByType"/> entry —
    /// bounded to distinct timer-shaped type names. Subject resolved via
    /// <see cref="IHeapObjectLookup"/> from each type's already-tracked <c>SampleAddress</c>
    /// (real <see cref="Sdk.Identity.TypeRef"/>, not a canonicalized-from-string reconstruction).
    /// </summary>
    private static async ValueTask<TimerLeakDomainResult> Analyze(
        IDumpObjectFieldQuery fields, IHeapSegmentQuery segments, IHeapObjectLookup objectLookup, IHeapRootPathQuery rootPaths, IHeapObjectStream? candidates,
        AnalysisContext observationContext, CancellationToken cancellationToken)
    {
        if (candidates is null)
            return Empty();

        var typeStats = new Dictionary<string, (int Count, ulong Bytes, ulong SampleAddress, TimerObjectCategory Category)>(StringComparer.Ordinal);
        int lessThan100Ms = 0, between100MsAnd1s = 0, moreThan1s = 0, infinite = 0;

        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
            TimerObjectCategory category = ClassifyType(candidate.TypeDisplayName);

            typeStats.TryGetValue(candidate.TypeDisplayName, out var ts);
            ulong sampleAddress = ts.SampleAddress == 0 ? candidate.Address : ts.SampleAddress;
            typeStats[candidate.TypeDisplayName] = (ts.Count + 1, ts.Bytes + candidate.Size, sampleAddress, category);

            if (category == TimerObjectCategory.TimerQueueTimer)
            {
                long periodMs = TryReadPeriod(fields, candidate.Address);
                if (periodMs < 0) infinite++;
                else if (periodMs < 100) lessThan100Ms++;
                else if (periodMs < 1_000) between100MsAnd1s++;
                else moreThan1s++;
            }
        }

        if (typeStats.Count == 0)
            return Empty();

        int threadingTimerCount = 0, timersTimerCount = 0, timerQueueTimerCount = 0, timerHolderCount = 0, periodicTimerCount = 0, otherTimerCount = 0;
        ulong totalBytes = 0;
        var byType = new List<TimerObjectTypeSummary>(typeStats.Count);

        foreach (var kv in typeStats)
        {
            var ts = kv.Value;
            switch (ts.Category)
            {
                case TimerObjectCategory.ThreadingTimer: threadingTimerCount += ts.Count; break;
                case TimerObjectCategory.TimersTimer: timersTimerCount += ts.Count; break;
                case TimerObjectCategory.TimerQueueTimer: timerQueueTimerCount += ts.Count; break;
                case TimerObjectCategory.TimerHolder: timerHolderCount += ts.Count; break;
                case TimerObjectCategory.PeriodicTimer: periodicTimerCount += ts.Count; break;
                case TimerObjectCategory.OtherTimer: otherTimerCount += ts.Count; break;
            }
            totalBytes += ts.Bytes;
            byType.Add(new TimerObjectTypeSummary(kv.Key, ts.Count, ts.Bytes));
        }

        byType.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        var observationCapabilities = new HashSet<Capability> { CapabilityVocabulary.HeapObjects };
        foreach (TimerObjectTypeSummary summary in byType)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!typeStats.TryGetValue(summary.TypeName, out var ts) || !objectLookup.TryGetObject(ts.SampleAddress, out HeapObjectRef sample))
                continue;

            await observationContext.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.timer",
                Subjects = [sample.Type],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(summary.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(summary.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    AnalyzerKey = "Timer Leak Analysis",
                    CapabilitiesUsed = observationCapabilities,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }

        PopulateEvidence(fields, segments, objectLookup, rootPaths, byType, typeStats);

        IReadOnlyList<(string Bucket, int Count)> intervalHistogram =
        [
            (IntervalBucketLabels[0], lessThan100Ms),
            (IntervalBucketLabels[1], between100MsAnd1s),
            (IntervalBucketLabels[2], moreThan1s),
            (IntervalBucketLabels[3], infinite),
        ];

        int total = threadingTimerCount + timersTimerCount + timerQueueTimerCount + timerHolderCount + periodicTimerCount + otherTimerCount;

        return new TimerLeakDomainResult(
            TimersFound: total > 0,
            TotalTimers: total,
            LogicalTimerCount: timerQueueTimerCount,
            ThreadingTimerCount: threadingTimerCount,
            TimersTimerCount: timersTimerCount,
            TimerQueueTimerCount: timerQueueTimerCount,
            TimerHolderCount: timerHolderCount,
            PeriodicTimerCount: periodicTimerCount,
            OtherTimerCount: otherTimerCount,
            TotalBytes: totalBytes,
            ByType: byType,
            IntervalHistogram: intervalHistogram);
    }

    // For each timer type, asks the root-path capability for a GC root path to a representative
    // instance (the first candidate of that type encountered during the main scan) — the
    // resulting root kind/field feeds the leak narrative (finding text, evidence) attached to
    // that type's summary.
    private static void PopulateEvidence(
        IDumpObjectFieldQuery fields, IHeapSegmentQuery segments, IHeapObjectLookup objectLookup, IHeapRootPathQuery rootPaths,
        List<TimerObjectTypeSummary> byType, Dictionary<string, (int Count, ulong Bytes, ulong SampleAddress, TimerObjectCategory Category)> typeStats)
    {
        for (int i = 0; i < byType.Count; i++)
        {
            TimerObjectTypeSummary summary = byType[i];
            if (!typeStats.TryGetValue(summary.TypeName, out var ts) || ts.SampleAddress == 0)
                continue;

            TimerStateSnapshot? snapshot = TrySampleTimerState(fields, segments, objectLookup, ts.SampleAddress, summary.TypeName);

            bool found = rootPaths.TryFindRootPath(ts.SampleAddress, RootPathOptions, out string? rootPath, out bool searchTruncated);

            byType[i] = summary with
            {
                Evidence = new Evidence(
                    summary.TotalBytes,
                    found ? rootPath : null,
                    searchTruncated,
                    [new EvidenceSignal("InstanceCount", "Instances of this timer type", summary.Count)]),
                Samples = snapshot != null ? [snapshot] : null
            };
        }
    }

    // Only System.Threading.TimerQueueTimer exposes a period/callback-owner worth reporting — the
    // other timer wrapper types don't hold that state directly on the object read here.
    private static TimerStateSnapshot? TrySampleTimerState(
        IDumpObjectFieldQuery fields, IHeapSegmentQuery segments, IHeapObjectLookup objectLookup, ulong address, string typeName)
    {
        if (!typeName.Equals("System.Threading.TimerQueueTimer", StringComparison.Ordinal))
            return null;

        long periodMs = TryReadPeriod(fields, address);
        string? callbackOwnerType = TryReadCallbackOwner(fields, objectLookup, address);
        CoreGenerationTag generation = ToCoreGenerationTag(segments.GetGeneration(address));

        return new TimerStateSnapshot(address, generation, periodMs, callbackOwnerType);
    }

    // TimerQueueTimer._period/m_period is genuinely uint on every runtime decompiled (.NET
    // Framework through .NET 10) -- uint.MaxValue is the real "not set"/infinite sentinel, read
    // directly rather than relying on a signed Int32 reinterpretation coincidence.
    private static long TryReadPeriod(IDumpObjectFieldQuery fields, ulong address)
    {
        foreach (string fieldName in PeriodFieldNames)
        {
            if (fields.TryReadField(address, fieldName, out FieldValue value) && value.Kind == FieldValueKind.UInt32)
            {
                uint raw = value.AsUInt32();
                return raw == uint.MaxValue ? -1 : raw;
            }
        }
        return -1;
    }

    private static string? TryReadCallbackOwner(IDumpObjectFieldQuery fields, IHeapObjectLookup objectLookup, ulong address)
    {
        foreach (string fieldName in TimerCallbackFieldNames)
        {
            if (!fields.TryReadField(address, fieldName, out FieldValue callbackValue) || callbackValue.Kind != FieldValueKind.ObjectReference)
                continue;

            if (fields.TryReadField(callbackValue.AsObjectAddress(), "_target", out FieldValue targetValue)
                && targetValue.Kind == FieldValueKind.ObjectReference
                && objectLookup.TryGetObject(targetValue.AsObjectAddress(), out HeapObjectRef targetObj))
            {
                return targetObj.TypeDisplayName;
            }
        }
        return null;
    }

    private static CoreGenerationTag ToCoreGenerationTag(HeapGenerationTag tag) => tag switch
    {
        HeapGenerationTag.Gen0 => CoreGenerationTag.Gen0,
        HeapGenerationTag.Gen1 => CoreGenerationTag.Gen1,
        HeapGenerationTag.Gen2 => CoreGenerationTag.Gen2,
        HeapGenerationTag.Loh => CoreGenerationTag.Loh,
        HeapGenerationTag.Poh => CoreGenerationTag.Poh,
        HeapGenerationTag.Frozen => CoreGenerationTag.Frozen,
        _ => CoreGenerationTag.Unknown,
    };

    internal static TimerObjectCategory ClassifyType(string typeName)
    {
        if (typeName.Equals("System.Threading.Timer", StringComparison.Ordinal))
            return TimerObjectCategory.ThreadingTimer;
        if (typeName.Equals("System.Timers.Timer", StringComparison.Ordinal))
            return TimerObjectCategory.TimersTimer;
        if (typeName.Equals("System.Threading.TimerQueueTimer", StringComparison.Ordinal))
            return TimerObjectCategory.TimerQueueTimer;
        if (typeName.Equals("System.Threading.TimerHolder", StringComparison.Ordinal))
            return TimerObjectCategory.TimerHolder;
        if (typeName.Equals("System.Threading.PeriodicTimer", StringComparison.Ordinal))
            return TimerObjectCategory.PeriodicTimer;

        if (IsKnownClrInternalTimerType(typeName))
            return TimerObjectCategory.None;

        if (TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, OtherTimerNamespacePrefixes, null, OtherTimerTokens))
            return TimerObjectCategory.OtherTimer;

        return TimerObjectCategory.None;
    }

    private static bool IsKnownClrInternalTimerType(string typeName)
    {
        for (int i = 0; i < ClrInternalTimerTypes.Length; i++)
        {
            if (typeName.Equals(ClrInternalTimerTypes[i], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static TimerLeakDomainResult Empty() => new(
        TimersFound: false,
        TotalTimers: 0,
        LogicalTimerCount: 0,
        ThreadingTimerCount: 0,
        TimersTimerCount: 0,
        TimerQueueTimerCount: 0,
        TimerHolderCount: 0,
        PeriodicTimerCount: 0,
        OtherTimerCount: 0,
        TotalBytes: 0,
        ByType: [],
        IntervalHistogram: []);

    public void Dispose() { }

    internal enum TimerObjectCategory
    {
        None,
        ThreadingTimer,
        TimersTimer,
        TimerQueueTimer,
        TimerHolder,
        PeriodicTimer,
        OtherTimer,
    }
}
