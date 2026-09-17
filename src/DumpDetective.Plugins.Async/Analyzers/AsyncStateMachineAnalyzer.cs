using System.Text.RegularExpressions;

using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Async;

/// <summary>
/// Phase-2 analyzer covering §23.1 (state machine population), §23.2 (captured closure
/// analysis), and §23.3 (suspended method map).
/// </summary>
/// <remarks>
/// Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), built
/// on <c>TypedResourceCandidateStreamingLegacyAdapter</c> like the rest of the Tier-2 wave — but
/// unlike a typical wave member, this one's pre-retyping design ran an entirely separate,
/// hand-rolled <em>second</em> full heap-index pass (<c>EnumerateIndexedEntriesAsTuples</c>/
/// <c>LiveHeapEntries</c>) purely to build the suspend-state histogram, one field read per
/// state-machine instance. That pass is gone: <c>context.HeapObjects</c> already streams every
/// state-machine instance once, during the pipeline's one shared Phase-1 heap-index scan, so the
/// histogram is now built in the same pass as everything else — the single biggest win of this
/// retyping, not just a mechanical port.
///
/// Closes the two real capability gaps the retyping plan flagged as still open for this analyzer:
/// <see cref="IDumpObjectFieldQuery.TryGetFieldTypeName"/> (a field's <em>declared</em> type name,
/// needed for the <c>&lt;&gt;t__builder</c> struct field <see cref="TryReadField"/> can't represent
/// a value for) and <see cref="IDumpObjectFieldQuery.EnumerateObjectReferenceFields"/> (a field
/// *name*+target enumeration, needed to list captured closures by field name — nothing before this
/// let a retyped analyzer ask "what are this instance's reference fields called").
///
/// Captured-closure-byte reads stay bounded to one sample instance per type — the first streamed
/// instance encountered — preserving the pre-retyping analyzer's own explicit "one object access
/// per type" cost bound; only <c>&lt;&gt;1__state</c> gets read on every instance (needed for the
/// histogram, and no more expensive than the pre-retyping analyzer's own second pass did per
/// instance).
///
/// <b>One accepted, documented fidelity gap</b>: <see cref="StateMachineTypeProfile.ReferenceFieldCount"/>
/// now comes from <see cref="HeapTypeStatistics.ReferenceFields"/>, which is 0 whenever
/// <see cref="IHeapTypeStatisticsQuery.HasTypeShapeData"/> is <c>false</c> (no prebuilt per-type
/// shape cache) — unlike the pre-retyping analyzer's unconditional <c>ClrType.Fields</c> count.
/// Accepted per hard-need-basis: no Tier-2 primitive exists (or is needed elsewhere) for an
/// unconditional per-type reference-field count outside the shape cache.
/// </remarks>
[AnalyzerModule(key: "async-state-machine", displayName: "Async State Machine Analysis", order: 350, tags: ["async"])]
[RequiresCapability(
    CapabilityVocabulary.DumpObjectFields,
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapTypes)]
[OptionalCapability(CapabilityVocabulary.TraceThreadTimeline)]
public sealed class AsyncStateMachineAnalyzer :
    IAnalyzer, IProducesAnalyzerDomainResult, ITypedResourceCandidateSource,
    IParallelHeapIndexScanParticipant, ICustomizesAnalysisContext
{
    private readonly TypedResourceHeapIndexScanState _scanState = new();

    public string Name => "Async State Machine Analysis";
    public string Category => "Memory";

    public bool IsCandidateType(string typeName) => AsyncStateMachineNamePattern.Regex.IsMatch(typeName);

    void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _scanState.BeforeHeapIndexScan(this, context);
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => _scanState.OnHeapEntry(in entry);
    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanState.OnHeapIndexScanCompleted(succeeded);
    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new AsyncStateMachineAnalyzer();
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) =>
        _scanState.MergeFrom(partials.OfType<AsyncStateMachineAnalyzer>().Select(a => a._scanState));

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

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapTypes}' capability.");
        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.DumpObjectFields}' capability.");
        IHeapObjectLookup objectLookup = context.HeapObjectLookup
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.HeapObjects}' capability.");
        AsyncStateMachineAnalysisOptions options = context.AnalyzerOptions as AsyncStateMachineAnalysisOptions ?? new AsyncStateMachineAnalysisOptions();

        LastResult = await Analyze(typeStatistics, fields, objectLookup, context.HeapObjects, options, context, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): emits
    /// <c>type.async-state-machine</c> (one per type in <c>topTypes</c>) and
    /// <c>type.async-captured-closure</c> (one per type in <c>highCaptures</c>, when captured
    /// bytes &gt; 0) inline in the same loop these lists are already built in — bounded to
    /// distinct async-state-machine type names (already narrowly scoped by
    /// <see cref="IsCandidateType"/>'s regex, not the whole-heap type population). Subject resolved
    /// via <see cref="IHeapObjectLookup"/> from each type's own sample address.
    /// </summary>
    private static async ValueTask<AsyncStateMachineDomainResult> Analyze(
        IHeapTypeStatisticsQuery typeStatistics,
        IDumpObjectFieldQuery fields,
        IHeapObjectLookup objectLookup,
        IHeapObjectStream? candidates,
        AsyncStateMachineAnalysisOptions options,
        AnalysisContext observationContext,
        CancellationToken cancellationToken)
    {
        if (candidates is null)
            return Empty();

        IReadOnlyDictionary<string, HeapTypeStatistics> stats = typeStatistics.GetTypeStatistics();

        // ── Single pass over every streamed state-machine instance ──────────────
        var sampleAddressByType = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var histogramByType = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string typeName = candidate.TypeDisplayName;
            if (!stats.TryGetValue(typeName, out HeapTypeStatistics entry) || !entry.IsAsyncStateMachineType)
                continue;

            seenTypes.Add(typeName);
            if (!sampleAddressByType.ContainsKey(typeName))
                sampleAddressByType[typeName] = candidate.Address;

            if (fields.TryReadField(candidate.Address, "<>1__state", out FieldValue stateValue) && stateValue.Kind == FieldValueKind.Int32)
            {
                if (!histogramByType.TryGetValue(typeName, out Dictionary<int, int>? histogram))
                {
                    histogram = new Dictionary<int, int>(8);
                    histogramByType[typeName] = histogram;
                }
                int state = stateValue.AsInt32();
                histogram[state] = histogram.GetValueOrDefault(state) + 1;
            }
        }

        // ── Totals: every flagged type, independent of per-instance name parsing below ──
        long totalCount = 0;
        ulong totalBytes = 0;
        long totalGen2Count = 0;
        foreach (KeyValuePair<string, HeapTypeStatistics> kv in stats)
        {
            if (!kv.Value.IsAsyncStateMachineType) continue;
            totalCount += kv.Value.InstanceCount;
            totalBytes += kv.Value.TotalSize;
            totalGen2Count += kv.Value.Gen2Count;
        }

        if (totalCount == 0)
            return Empty();

        var topTypes = new List<StateMachineTypeProfile>(seenTypes.Count);
        var highCaptures = new List<HighCaptureStateMachine>(seenTypes.Count);
        var observationCapabilities = new HashSet<Capability> { CapabilityVocabulary.HeapTypes, CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects };

        foreach (string typeName in seenTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stats.TryGetValue(typeName, out HeapTypeStatistics entry))
                continue;

            (string methodName, string declaringType) = ParseMethodName(typeName);
            ulong sampleAddress = sampleAddressByType.GetValueOrDefault(typeName);

            bool isAsyncVoid = false;
            ulong capturedBytes = 0;
            var largeCaptures = new List<string>(4);

            if (sampleAddress != 0)
            {
                if (fields.TryGetFieldTypeName(sampleAddress, "<>t__builder", out string? builderTypeName) && builderTypeName is not null)
                    isAsyncVoid = builderTypeName.Contains("AsyncVoidMethodBuilder");

                foreach ((string fieldName, ulong targetAddress) in fields.EnumerateObjectReferenceFields(sampleAddress))
                {
                    if (targetAddress == 0 || !objectLookup.TryGetObject(targetAddress, out HeapObjectRef target))
                        continue;

                    capturedBytes += target.Size;
                    if (target.Size >= options.LargeCaptureThresholdBytes)
                        largeCaptures.Add($"{fieldName} ({target.TypeDisplayName}, {FormatHelper.FormatBytes(target.Size)})");
                }

                if (capturedBytes > 0)
                {
                    highCaptures.Add(new HighCaptureStateMachine(sampleAddress, typeName, capturedBytes, largeCaptures));

                    if (objectLookup.TryGetObject(sampleAddress, out HeapObjectRef captureSubject))
                    {
                        await observationContext.Observations.EmitAsync(new Observation
                        {
                            Id = ObservationId.NewId(),
                            ObservationType = "type.async-captured-closure",
                            Subjects = [captureSubject.Type],
                            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                            Measures = new Dictionary<string, Measure>
                            {
                                ["captured.bytes"] = new Measure(capturedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                            },
                            Provenance = new Provenance
                            {
                                Artifact = observationContext.ArtifactId!.Value,
                                AnalyzerKey = "Async State Machine Analysis",
                                CapabilitiesUsed = observationCapabilities,
                                Fidelity = FidelityLevel.Full,
                            },
                            Confidence = 1.0,
                        }, cancellationToken);
                    }
                }
            }

            (int dominantState, IReadOnlyList<(int State, int Count)> distribution) = BuildStateDistribution(histogramByType, typeName);

            double gen2Fraction = entry.InstanceCount > 0 ? entry.Gen2Count / (double)entry.InstanceCount : 0.0;

            topTypes.Add(new StateMachineTypeProfile(
                TypeName: typeName,
                OriginatingMethod: methodName,
                DeclaringType: declaringType,
                Count: (int)Math.Min(entry.InstanceCount, int.MaxValue),
                TotalBytes: entry.TotalSize,
                DominantState: dominantState,
                StateDistribution: distribution,
                ReferenceFieldCount: entry.ReferenceFields,
                Gen2Count: entry.Gen2Count,
                Gen2Fraction: gen2Fraction,
                IsAsyncVoid: isAsyncVoid));

            if (sampleAddress != 0 && objectLookup.TryGetObject(sampleAddress, out HeapObjectRef stateMachineSubject))
            {
                await observationContext.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "type.async-state-machine",
                    Subjects = [stateMachineSubject.Type],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["instance.count"] = new Measure(entry.InstanceCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["shallow.bytes"] = new Measure(entry.TotalSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                        ["dominant-state"] = new Measure(dominantState, MeasureUnit.Custom, MeasureSemantics.Absolute),
                        ["gen2.count"] = new Measure(entry.Gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                        ["gen2.fraction"] = new Measure(gen2Fraction, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                        ["is-async-void"] = new Measure(isAsyncVoid ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = observationContext.ArtifactId!.Value,
                        AnalyzerKey = "Async State Machine Analysis",
                        CapabilitiesUsed = observationCapabilities,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                }, cancellationToken);
            }
        }

        topTypes.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        highCaptures.Sort(static (a, b) => b.TotalCapturedRefBytes.CompareTo(a.TotalCapturedRefBytes));

        // ── SuspendedMethodMap: group by (DeclaringType, MethodName) ─────────────
        var methodMap = new Dictionary<(string DeclaringType, string Method), (long Count, ulong Bytes)>(16);
        foreach (StateMachineTypeProfile profile in topTypes)
        {
            var key = (profile.DeclaringType, profile.OriginatingMethod);
            if (methodMap.TryGetValue(key, out (long Count, ulong Bytes) existing))
                methodMap[key] = (existing.Count + profile.Count, existing.Bytes + profile.TotalBytes);
            else
                methodMap[key] = (profile.Count, profile.TotalBytes);
        }

        var suspendedMap = new List<SuspendedMethodEntry>(methodMap.Count);
        foreach (KeyValuePair<(string, string), (long Count, ulong Bytes)> kv in methodMap)
            suspendedMap.Add(new SuspendedMethodEntry(kv.Key.Item1, kv.Key.Item2, (int)Math.Min(kv.Value.Count, int.MaxValue), kv.Value.Bytes));
        suspendedMap.Sort(static (a, b) => b.SuspendedCount.CompareTo(a.SuspendedCount));

        return new AsyncStateMachineDomainResult(
            TotalStateMachines: (int)Math.Min(totalCount, int.MaxValue),
            TotalStateMachineBytes: totalBytes,
            TopStateMachineTypes: topTypes,
            TopByCapturedSize: highCaptures,
            SuspendedMethodMap: suspendedMap,
            TotalGen2Count: totalGen2Count);
    }

    // Extract <MethodName>d__N's method name/declaring type — same regex Phase 1's
    // IsAsyncStateMachineType flag and the shared adapter's IsCandidateType already trust as
    // ground truth (AsyncStateMachineNamePattern), so a type that reached this point already
    // matched it once; this only re-runs it to pull the capture groups out.
    private static (string MethodName, string DeclaringType) ParseMethodName(string typeName)
    {
        int angleOpen = typeName.LastIndexOf('<');
        if (angleOpen < 0)
            return (string.Empty, string.Empty);

        Match m;
        try
        {
            m = AsyncStateMachineNamePattern.Regex.Match(typeName, angleOpen);
        }
        catch (RegexMatchTimeoutException)
        {
            return (string.Empty, string.Empty);
        }

        if (!m.Success)
            return (string.Empty, string.Empty);

        string methodName = m.Groups[1].Value;
        string declaringType = angleOpen > 0 ? typeName[..(angleOpen - 1)] : string.Empty;
        return (methodName, declaringType);
    }

    private static (int DominantState, IReadOnlyList<(int State, int Count)> Distribution) BuildStateDistribution(
        Dictionary<string, Dictionary<int, int>> histogramByType, string typeName)
    {
        if (!histogramByType.TryGetValue(typeName, out Dictionary<int, int>? histogram) || histogram.Count == 0)
            return (0, []);

        var sorted = new List<(int State, int Count)>(histogram.Count);
        foreach (KeyValuePair<int, int> kv in histogram)
            sorted.Add((kv.Key, kv.Value));
        sorted.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        if (sorted.Count > 3)
            sorted.RemoveRange(3, sorted.Count - 3);

        return (sorted[0].State, sorted);
    }

    private static AsyncStateMachineDomainResult Empty() => new(0, 0, [], [], []);

    public void Dispose() { }
}
