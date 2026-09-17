using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Scans the managed heap for WCF channel objects (System.ServiceModel).
/// Detects faulted or leaked channels, which cause resource exhaustion and silent failures.
///
/// CommunicationState enum:
///   Created=0, Opening=1, Opened=2, Closing=3, Closed=4, Faulted=5
///
/// A faulted channel must be Abort()ed, not Close()d. Faulted channels on the heap are a
/// strong signal of missing error-handling in WCF proxy usage.
/// </summary>
/// <remarks>
/// Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), fifth
/// consumer of <c>TypedResourceCandidateStreamingLegacyAdapter</c>. Retyping resolved the open
/// items this analyzer was deferred for, plus a third, more severe bug found by writing the first
/// ever unit test for <see cref="IsFactoryType"/> against a realistic type name — all verified
/// against the real .NET Framework <c>System.ServiceModel.dll</c>/<c>System.dll</c> (decompiled
/// from the local GAC, v4.0.0.0) rather than guessed:
///
/// 1. The pre-retyping <c>StateElementTypes</c>'s <c>ClrElementType.Object</c> branch (added for
///    a hypothetical boxed <c>CommunicationState</c>) is dropped, not reproduced.
///    <c>CommunicationObject.state</c> is declared <c>private CommunicationState state;</c>, and
///    <c>CommunicationState</c> has no explicit underlying-type declarator, so it's a plain
///    <c>Int32</c>-backed enum — never an <c>object</c> field for the real type, making that
///    branch unreachable dead code. It was also implemented incorrectly had it ever fired: the
///    pre-retyping <c>TryReadIntField</c> called <c>field.Read&lt;int&gt;</c> unconditionally
///    regardless of element type, which for a genuine boxed-object field would read the field's
///    raw pointer bytes, not the boxed value.
///
/// 2. Remote-address extraction's final step — <c>uriObj.AsString()</c> falling back to
///    <c>uriObj.ToString()</c> — never actually worked. <c>EndpointAddress.uri</c> is
///    <c>System.Uri</c>, not <c>string</c>, so <c>ClrObject.AsString()</c> (only valid on real
///    <c>String</c> objects) always threw, and the catch fell to <c>ClrObject.ToString()</c>'s
///    override, which returns <c>"{Address:x} {Type.Name}"</c> — a debug string like
///    <c>"7f8a1234 System.Uri"</c>, never the real URL. Fixed by chasing one field further into
///    the internal field that actually holds the fully-formed URI text — which is named
///    differently depending on which runtime built the dump: <c>m_String</c> in .NET Framework's
///    <c>System.dll</c>, <c>_string</c> in modern .NET's <c>System.Private.Uri.dll</c> (WCF client
///    packages run on both; both decompiled from the real assemblies on this machine to confirm).
///
/// 3. <see cref="IsFactoryType"/> matched essentially every nested channel instance, not just
///    actual factories — pre-existing in the original, uncovered because it had no unit test
///    against a realistic nested type name (every example elsewhere in this file already used the
///    realistic "Factory+Channel" shape). Consequence in the original: <c>OnHeapEntry</c>'s
///    factory check ran before candidate-type classification and returned early on a match, so a
///    misclassified nested channel instance never reached state-bucket classification at all —
///    silently dropping real (including Faulted) channels from every count for the type-name shape
///    the codebase's own examples describe as typical. Fixed by excluding any type name containing
///    <c>'+'</c> (a factory is never a nested type; a channel declared inside one always is).
///
/// Also stopped computing the remote address for every channel candidate (the pre-retyping
/// analyzer's own per-instance sampler always did, even though only Faulted channels ever surface
/// it in <see cref="WcfChannelDomainResult.TopFaultedChannels"/>) — chased only once a channel is
/// confirmed Faulted, an output-preserving perf simplification, not a behavior change.
///
/// Factory count no longer needs its own scan-participant candidate stream: it's read straight off
/// <see cref="IHeapTypeStatisticsQuery.GetTypeStatistics"/> (backed by the same TypeAggregates
/// index the pre-retyping analyzer's own <c>TypedResourceCandidateScanner.DiscoverCandidates</c>
/// read from), summed over factory-classified type names — avoids extending the shared adapter
/// base for a second, count-only candidate set.
///
/// Preserves one subtle pre-retyping distinction: a channel candidate whose state field couldn't
/// be read at all (no known field name present) still counts toward <c>ByType.TotalCount</c> but
/// contributes to no state bucket — different from an out-of-range/corrupted state value, which
/// counts toward <see cref="WcfChannelTypeSummary.InvalidStateCount"/>.
/// </remarks>
[AnalyzerModule(key: "wcf-channel", displayName: "WCF Channel Analysis", order: 430, tags: ["infra", "network"])]
[RequiresCapability(
    CapabilityVocabulary.DumpObjectFields,
    CapabilityVocabulary.HeapDominators,
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapTypes)]
[OptionalCapability(CapabilityVocabulary.TraceCustomEvents)]
public sealed class WcfChannelAnalyzer :
    IAnalyzer, IProducesAnalyzerDomainResult, ITypedResourceCandidateSource,
    IParallelHeapIndexScanParticipant, ICustomizesAnalysisContext
{
    private readonly TypedResourceHeapIndexScanState _scanState = new();

    public string Name => "WCF Channel Analysis";
    public string Category => "Infrastructure";

    // A genuine (non-nested) factory type name also satisfies the static IsCandidateType's
    // "Channel" token match (e.g. "TcpChannelFactory" contains "Channel") -- excluded here so
    // factory instances flow only through this analyzer's independent HeapTypeStatistics-based
    // FactoryCount, never into the per-object channel-state candidate stream.
    bool ITypedResourceCandidateSource.IsCandidateType(string typeName) => IsCandidateType(typeName) && !IsFactoryType(typeName);

    void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _scanState.BeforeHeapIndexScan(this, context);
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => _scanState.OnHeapEntry(in entry);
    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanState.OnHeapIndexScanCompleted(succeeded);
    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new WcfChannelAnalyzer();
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) =>
        _scanState.MergeFrom(partials.OfType<WcfChannelAnalyzer>().Select(a => a._scanState));

    Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
        AnalysisContextBuilder.Build(context, analyzerOptions: null, heapObjectsOverride: new PrecomputedHeapObjectStream(_scanState.Candidates));

    // CommunicationState enum values
    private const int StateOpening = 1;
    private const int StateOpened = 2;
    private const int StateClosing = 3;
    private const int StateClosed = 4;
    private const int StateFaulted = 5;

    // Types to match: in System.ServiceModel namespace, ending with "Channel" or well-known
    // base/proxy types.
    private static readonly string[] WcfNamespacePrefixes = ["System.ServiceModel."];
    private static readonly string[] WcfContainsTokens = ["Channel", "ClientBase", "CommunicationObject"];
    private static readonly string[] FactoryNamespaces = ["System.ServiceModel."];
    private static readonly string[] FactoryContainsTokens = ["ChannelFactory"];

    private static readonly string[] StateFieldNames = ["_state", "state", "communicationState"];
    private static readonly string[] RemoteAddressFieldNames = ["_remoteAddress", "_via", "remoteAddress", "via"];
    private static readonly string[] UriFieldNames = ["_uri", "uri", "_address", "address"];
    private static readonly string[] UriInternalStringFieldNames = ["m_String", "_string"];

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
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, WcfNamespacePrefixes, ".ServiceChannel", WcfContainsTokens);

    // A real factory instance's type name never contains '+' (factories are top-level classes,
    // e.g. "System.ServiceModel.Channels.TcpChannelFactory"). A nested channel class declared
    // inside a factory (the realistic, common shape -- see ClassifyBindingHint's remarks, e.g.
    // "TcpChannelFactory+ClientFramingDuplexSessionChannel") embeds its enclosing factory's full
    // name before the '+', which also contains the "ChannelFactory" token -- so without this
    // exclusion, essentially every nested channel instance would misclassify as a factory. Found
    // and fixed during retyping (not a regression introduced by it: the pre-retyping analyzer had
    // the same bug, just never unit-tested against a realistic nested channel type name) -- the
    // consequence was severe, since a MethodTable misclassified as a factory short-circuits out of
    // state-bucket classification entirely in OnHeapEntry, silently excluding real channels
    // (including Faulted ones, the analyzer's headline signal) from every count.
    internal static bool IsFactoryType(string typeName) =>
        !typeName.Contains('+', StringComparison.Ordinal)
        && TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, FactoryNamespaces, ".ChannelFactory", FactoryContainsTokens);

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");
        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");

        LastResult = await Analyze(fields, typeStatistics, context.HeapObjects, context.HeapDominators, context, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
    /// <c>infra.wcf-channel</c> observation per faulted channel — the same anomaly-relevant subset
    /// <see cref="WcfChannelDomainResult.TopFaultedChannels"/> already selects, emitted inline right
    /// after <c>topFaulted.Add</c> where <see cref="HeapObjectRef.Type"/> and the already-resolved
    /// retained-bytes value are both still in scope.
    /// </summary>
    private static async ValueTask<WcfChannelDomainResult> Analyze(
        IDumpObjectFieldQuery fields, IHeapTypeStatisticsQuery typeStatistics, IHeapObjectStream? candidates, IHeapDominatorQuery? dominators,
        AnalysisContext observationContext, CancellationToken cancellationToken)
    {
        int factoryCount = 0;
        foreach (KeyValuePair<string, HeapTypeStatistics> kv in typeStatistics.GetTypeStatistics())
        {
            if (IsFactoryType(kv.Key))
                factoryCount += (int)Math.Min(kv.Value.InstanceCount, int.MaxValue);
        }

        if (candidates is null)
            return Empty(factoryCount);

        var observationCapabilities = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapTypes };
        var typeStats = new Dictionary<string, (int Total, int Opening, int Opened, int Faulted, int Closing, int Closed, int Other, int InvalidState, ulong Bytes)>(StringComparer.Ordinal);
        var topFaulted = new List<WcfChannelSnapshot>();

        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            int stateVal = TryReadState(fields, candidate.Address);

            typeStats.TryGetValue(candidate.TypeDisplayName, out var ts);
            int opening = ts.Opening, opened = ts.Opened, faulted = ts.Faulted, closing = ts.Closing, closed = ts.Closed, other = ts.Other, invalidState = ts.InvalidState;

            if (stateVal >= 0)
            {
                if (stateVal == StateOpening) opening++;
                else if (stateVal == StateOpened) opened++;
                else if (stateVal == StateFaulted) faulted++;
                else if (stateVal == StateClosing) closing++;
                else if (stateVal == StateClosed) closed++;
                else if (IsValidCommunicationState(stateVal)) other++; // Created (0) — valid, just uncommon to observe on heap
                else invalidState++; // out-of-range/corrupted state read — do not silently fold into Other

                if (stateVal == StateFaulted)
                {
                    string? remoteAddress = TryExtractRemoteAddress(fields, candidate.Address);
                    var snap = new WcfChannelSnapshot(candidate.TypeDisplayName, candidate.Address, MapCommunicationState(stateVal), stateVal, remoteAddress);
                    ulong retained = 0;
                    bool hasRetained = dominators is not null && dominators.TryGetRetainedSize(candidate.Address, out retained);
                    if (hasRetained)
                        snap = snap with { RetainedBytes = retained };
                    topFaulted.Add(snap);

                    var measures = new Dictionary<string, Measure>();
                    if (hasRetained)
                        measures["retained.bytes"] = new Measure(retained, MeasureUnit.Bytes, MeasureSemantics.Absolute);

                    await observationContext.Observations.EmitAsync(new Observation
                    {
                        Id = ObservationId.NewId(),
                        ObservationType = "infra.wcf-channel",
                        Subjects = [candidate.Type],
                        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                        Measures = measures,
                        Provenance = new Provenance
                        {
                            Artifact = observationContext.ArtifactId!.Value,
                            AnalyzerKey = "WCF Channel Analysis",
                            CapabilitiesUsed = observationCapabilities,
                            Fidelity = hasRetained ? FidelityLevel.Full : FidelityLevel.Degraded,
                        },
                        Confidence = hasRetained ? 1.0 : 0.7,
                    }, cancellationToken);
                }
            }

            typeStats[candidate.TypeDisplayName] = (ts.Total + 1, opening, opened, faulted, closing, closed, other, invalidState, ts.Bytes + candidate.Size);
        }

        if (typeStats.Count == 0)
            return Empty(factoryCount);

        int totalChannels = 0, totalOpening = 0, totalOpened = 0, totalFaulted = 0, totalClosing = 0, totalClosed = 0, totalOther = 0, totalInvalidState = 0;
        int totalDuplex = 0, totalSession = 0;
        ulong totalBytes = 0;
        var byType = new List<WcfChannelTypeSummary>(typeStats.Count);
        foreach (var kv in typeStats)
        {
            var ts = kv.Value;
            byType.Add(new WcfChannelTypeSummary(kv.Key, ts.Total, ts.Opening, ts.Opened, ts.Faulted, ts.Closing, ts.Closed, ts.Other, ts.Bytes, ClassifyBindingHint(kv.Key), ts.InvalidState));
            totalChannels += ts.Total;
            totalOpening += ts.Opening;
            totalOpened += ts.Opened;
            totalFaulted += ts.Faulted;
            totalClosing += ts.Closing;
            totalClosed += ts.Closed;
            totalOther += ts.Other;
            totalInvalidState += ts.InvalidState;
            totalBytes += ts.Bytes;
            if (IsDuplexChannelType(kv.Key)) totalDuplex += ts.Total;
            if (IsSessionChannelType(kv.Key)) totalSession += ts.Total;
        }

        byType.Sort(static (a, b) => b.TotalCount.CompareTo(a.TotalCount));

        return new WcfChannelDomainResult(
            WcfPresent: totalChannels > 0 || factoryCount > 0,
            TotalChannels: totalChannels,
            OpeningChannels: totalOpening,
            OpenedChannels: totalOpened,
            FaultedChannels: totalFaulted,
            ClosingChannels: totalClosing,
            ClosedChannels: totalClosed,
            OtherChannels: totalOther,
            ByType: byType,
            TopFaultedChannels: topFaulted,
            FactoryCount: factoryCount,
            TotalBytes: totalBytes,
            InvalidStateCount: totalInvalidState,
            DuplexChannelCount: totalDuplex,
            SessionChannelCount: totalSession);
    }

    private static int TryReadState(IDumpObjectFieldQuery fields, ulong address)
    {
        foreach (string fieldName in StateFieldNames)
        {
            if (fields.TryReadField(address, fieldName, out FieldValue value) && value.Kind == FieldValueKind.Int32)
                return value.AsInt32();
        }
        return -1;
    }

    private static string? TryExtractRemoteAddress(IDumpObjectFieldQuery fields, ulong channelAddress)
    {
        foreach (string fieldName in RemoteAddressFieldNames)
        {
            if (!fields.TryReadField(channelAddress, fieldName, out FieldValue endpointValue) || endpointValue.Kind != FieldValueKind.ObjectReference)
                continue;

            string? uri = TryExtractUriFromEndpointAddress(fields, endpointValue.AsObjectAddress());
            if (uri is not null)
                return uri;
        }
        return null;
    }

    private static string? TryExtractUriFromEndpointAddress(IDumpObjectFieldQuery fields, ulong endpointAddress)
    {
        foreach (string fieldName in UriFieldNames)
        {
            if (!fields.TryReadField(endpointAddress, fieldName, out FieldValue uriFieldValue))
                continue;

            if (uriFieldValue.Kind == FieldValueKind.String)
                return uriFieldValue.AsString();

            if (uriFieldValue.Kind != FieldValueKind.ObjectReference)
                continue;

            ulong uriAddress = uriFieldValue.AsObjectAddress();

            // This field commonly resolves to System.Uri, not a bare string -- its absolute-string
            // form lives in one of its own internal fields, whose name changed across runtimes:
            // "m_String" in .NET Framework's System.dll, "_string" in modern .NET's
            // System.Private.Uri.dll (both verified by decompiling the real assemblies -- WCF
            // client packages run on both). A lookalike type whose field is directly a string
            // object is covered by TryGetStringValue first.
            if (fields.TryGetStringValue(uriAddress, out string? direct) && !string.IsNullOrEmpty(direct))
                return direct;

            foreach (string uriInternalFieldName in UriInternalStringFieldNames)
            {
                if (fields.TryReadField(uriAddress, uriInternalFieldName, out FieldValue internalString) && internalString.Kind == FieldValueKind.String)
                    return internalString.AsString();
            }
        }
        return null;
    }

    // Dumps carry no binding configuration, only channel objects, so this infers binding from the
    // channel type name's declaring-type prefix (nested channel classes carry their enclosing
    // ChannelFactory's name, e.g. "TcpChannelFactory+ClientFramingDuplexSessionChannel" or
    // "NamedPipeChannelFactory+PipeConnectionChannel"). net.tcp and net.pipe both use
    // FramingDuplexSessionChannel under the hood, so when the heap has only a bare channel type
    // with no factory-name prefix, neither token matches and this correctly reports Unknown rather
    // than guessing. "Security..." wrapper channel classes used for WS-* message security are
    // emitted for wsHttpBinding far more often than basicHttpBinding (which has no message-security
    // wrapper), so the bare "Security" token is treated as a WsHttp signal. Order matters:
    // NamedPipe/Tcp are checked first since a tcp channel can also carry a security wrapper.
    internal static WcfBindingHint ClassifyBindingHint(string typeName)
    {
        if (typeName.Contains("NamedPipe", StringComparison.Ordinal))
            return WcfBindingHint.NamedPipe;
        if (typeName.Contains("Tcp", StringComparison.Ordinal))
            return WcfBindingHint.NetTcp;
        if (typeName.Contains("Security", StringComparison.Ordinal))
            return WcfBindingHint.WsHttp;
        if (typeName.Contains("Http", StringComparison.Ordinal))
            return WcfBindingHint.Basic;
        return WcfBindingHint.Unknown;
    }

    // Runtime channel objects are concrete classes, not the ISessionChannel/IDuplexChannel
    // interfaces themselves, but System.ServiceModel's channel class names consistently encode
    // both shape tokens (e.g. "ClientFramingDuplexSessionChannel" is both duplex and
    // session-based), so a type-name token match is the same cheap, no-ClrType-introspection
    // approach already used by ClassifyBindingHint.
    internal static bool IsDuplexChannelType(string typeName) =>
        typeName.Contains("Duplex", StringComparison.Ordinal);

    internal static bool IsSessionChannelType(string typeName) =>
        typeName.Contains("Session", StringComparison.Ordinal);

    private static string MapCommunicationState(int state) => state switch
    {
        0 => "Created",
        1 => "Opening",
        2 => "Opened",
        3 => "Closing",
        4 => "Closed",
        5 => "Faulted",
        _ => "Unknown",
    };

    // CommunicationState only defines 0 (Created) through 5 (Faulted). A value outside that range
    // means the "_state"/"state"/"communicationState" field probe matched a differently-laid-out
    // field on a lookalike type, or memory is corrupted -- either way it is not a real channel
    // state and must not be folded into the Other bucket alongside legitimate (if uncommon)
    // Created-state channels.
    internal static bool IsValidCommunicationState(int stateVal) => stateVal is >= 0 and <= StateFaulted;

    private static WcfChannelDomainResult Empty(int factoryCount) =>
        new(factoryCount > 0, 0, 0, 0, 0, 0, 0, 0, [], [], factoryCount, 0, 0, 0, 0);

    public void Dispose() { }
}
