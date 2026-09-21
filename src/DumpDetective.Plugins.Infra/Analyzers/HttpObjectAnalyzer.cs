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
/// Scans the managed heap for HTTP-related objects (HttpClient, HttpWebRequest/Response,
/// HttpMessageHandler subclasses, ServicePoint, IHttpClientFactory handler-tracking entries).
///
/// Common misuse patterns detected:
/// - Multiple HttpClient instances: should be singletons (HttpClientFactory pattern).
/// - HttpWebResponse objects: indicate responses not disposed, holding sockets.
/// - ServicePoint accumulation: can exhaust the system-level connection table.
/// </summary>
/// <remarks>
/// Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), fourth real
/// consumer of <c>TypedResourceCandidateStreamingLegacyAdapter</c>. Field-value extraction moves
/// entirely off the scan-participant path into <see cref="AnalyzeAsync"/>, same shape as the SQL
/// batch. Per-instance HttpClient generation breakdown (Gen0/Gen1/Gen2) comes from
/// <see cref="IHeapSegmentQuery.GetGeneration"/> instead of the pre-retyping analyzer's free
/// <c>HeapEntry.Generation</c> field (an extra capability call per HttpClient candidate — a small
/// population, since HttpClient is matched by exact type name, so there's normally exactly one
/// candidate MethodTable). HttpMessageHandler module attribution comes from
/// <see cref="HeapTypeStatistics.ModuleName"/> instead of a direct <c>TypeAggregateNameResolver</c>
/// call, since that's already resolved the same way for every type.
/// </remarks>
[AnalyzerModule(key: "http-object", displayName: "Http Object Analysis", order: 440, tags: ["infra", "network"])]
[RequiresCapability(
    CapabilityVocabulary.DumpObjectFields,
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapSegments,
    CapabilityVocabulary.HeapTypes)]
[OptionalCapability(CapabilityVocabulary.TraceHttpEvents)]
public sealed class HttpObjectAnalyzer :
    IAnalyzer, IProducesAnalyzerDomainResult, ITypedResourceCandidateSource,
    IParallelHeapIndexScanParticipant, ICustomizesAnalysisContext
{
    private readonly TypedResourceHeapIndexScanState _scanState = new();

    public string Name => "HTTP Object Analysis";
    public string Category => "Infrastructure";

    public bool IsCandidateType(string typeName) => ClassifyType(typeName) != HttpObjectCategory.None;

    void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _scanState.BeforeHeapIndexScan(this, context);
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => _scanState.OnHeapEntry(in entry);
    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanState.OnHeapIndexScanCompleted(succeeded);
    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new HttpObjectAnalyzer();
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) =>
        _scanState.MergeFrom(partials.OfType<HttpObjectAnalyzer>().Select(a => a._scanState));

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

        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");
        IHeapSegmentQuery segments = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");

        LastResult = await Analyze(fields, segments, typeStatistics, context.HeapObjects, context, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
    /// <c>infra.http-object</c> observation per instance sampled into
    /// <see cref="HttpObjectDomainResult.TopHttpInstances"/> (HttpClient/HttpWebRequest/
    /// ServicePoint/handler-tracking entries) — emitted via <see cref="EmitHttpObservationAsync"/>
    /// right after each <c>topSamples.Add</c>, where <see cref="HeapObjectRef.Type"/> is still in
    /// scope. One shared observation type across all sub-kinds; raw shallow size only.
    /// </summary>
    private static async ValueTask<HttpObjectDomainResult> Analyze(
        IDumpObjectFieldQuery fields, IHeapSegmentQuery segments, IHeapTypeStatisticsQuery typeStatistics, IHeapObjectStream? candidates,
        AnalysisContext observationContext, CancellationToken cancellationToken)
    {
        if (candidates is null)
            return Empty();

        var observationCapabilities = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects };
        var typeStats = new Dictionary<string, (long HttpClient, long HttpWebRequest, long HttpWebResponse, long HttpMessageHandler, long ServicePoint, long ActiveHandlerTrackingEntry, long ExpiredHandlerTrackingEntry, ulong Bytes)>(StringComparer.Ordinal);
        var topSamples = new List<HttpInstanceSnapshot>();
        long httpClientGen0 = 0, httpClientGen1 = 0, httpClientGen2 = 0;

        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpObjectCategory category = ClassifyType(candidate.TypeDisplayName);
            if (category == HttpObjectCategory.None) continue;

            typeStats.TryGetValue(candidate.TypeDisplayName, out var ts);
            long httpClient = ts.HttpClient, httpWebRequest = ts.HttpWebRequest, httpWebResponse = ts.HttpWebResponse;
            long httpMessageHandler = ts.HttpMessageHandler, servicePoint = ts.ServicePoint;
            long activeHandlerTrackingEntry = ts.ActiveHandlerTrackingEntry, expiredHandlerTrackingEntry = ts.ExpiredHandlerTrackingEntry;

            switch (category)
            {
                case HttpObjectCategory.HttpClient:
                    httpClient++;
                    int generationOrdinal;
                    switch (segments.GetGeneration(candidate.Address))
                    {
                        case HeapGenerationTag.Gen0: httpClientGen0++; generationOrdinal = 0; break;
                        case HeapGenerationTag.Gen1: httpClientGen1++; generationOrdinal = 1; break;
                        default: httpClientGen2++; generationOrdinal = 2; break;
                    }
                    HttpInstanceSnapshot? clientSnap = TrySampleHttpClient(fields, candidate.Address, candidate.TypeDisplayName);
                    if (clientSnap is not null)
                    {
                        topSamples.Add(clientSnap);
                        await EmitHttpObservationAsync(observationContext, candidate, observationCapabilities, cancellationToken, generationOrdinal: generationOrdinal);
                    }
                    break;
                case HttpObjectCategory.HttpWebRequest:
                    httpWebRequest++;
                    HttpInstanceSnapshot? requestSnap = TrySampleHttpWebRequest(fields, candidate.Address, candidate.TypeDisplayName);
                    if (requestSnap is not null)
                    {
                        topSamples.Add(requestSnap);
                        await EmitHttpObservationAsync(observationContext, candidate, observationCapabilities, cancellationToken);
                    }
                    break;
                case HttpObjectCategory.HttpWebResponse:
                    httpWebResponse++;
                    break;
                case HttpObjectCategory.HttpMessageHandler:
                    httpMessageHandler++;
                    break;
                case HttpObjectCategory.ServicePoint:
                    servicePoint++;
                    HttpInstanceSnapshot? spSnap = TrySampleServicePoint(fields, candidate.Address, candidate.TypeDisplayName);
                    if (spSnap is not null)
                    {
                        topSamples.Add(spSnap);
                        await EmitHttpObservationAsync(observationContext, candidate, observationCapabilities, cancellationToken, connectionLimit: spSnap.ConnectionLimit);
                    }
                    break;
                case HttpObjectCategory.ActiveHandlerTrackingEntry:
                    activeHandlerTrackingEntry++;
                    HttpInstanceSnapshot? activeSnap = TrySampleHandlerTrackingEntry(fields, candidate.Address, candidate.TypeDisplayName, "ActiveHandlerTrackingEntry");
                    if (activeSnap is not null)
                    {
                        topSamples.Add(activeSnap);
                        await EmitHttpObservationAsync(observationContext, candidate, observationCapabilities, cancellationToken);
                    }
                    break;
                case HttpObjectCategory.ExpiredHandlerTrackingEntry:
                    expiredHandlerTrackingEntry++;
                    HttpInstanceSnapshot? expiredSnap = TrySampleHandlerTrackingEntry(fields, candidate.Address, candidate.TypeDisplayName, "ExpiredHandlerTrackingEntry");
                    if (expiredSnap is not null)
                    {
                        topSamples.Add(expiredSnap);
                        await EmitHttpObservationAsync(observationContext, candidate, observationCapabilities, cancellationToken);
                    }
                    break;
            }

            typeStats[candidate.TypeDisplayName] = (httpClient, httpWebRequest, httpWebResponse, httpMessageHandler, servicePoint, activeHandlerTrackingEntry, expiredHandlerTrackingEntry, ts.Bytes + candidate.Size);
        }

        if (typeStats.Count == 0)
            return Empty();

        long totalHttpClient = 0, totalHttpWebRequest = 0, totalHttpWebResponse = 0, totalHttpMessageHandler = 0, totalServicePoint = 0, totalActiveHandlerTrackingEntry = 0, totalExpiredHandlerTrackingEntry = 0;
        ulong totalBytes = 0;
        var byType = new List<HttpObjectTypeSummary>(typeStats.Count);
        var handlerModuleTotals = new Dictionary<string, (long Count, ulong Bytes)>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, (long HttpClient, long HttpWebRequest, long HttpWebResponse, long HttpMessageHandler, long ServicePoint, long ActiveHandlerTrackingEntry, long ExpiredHandlerTrackingEntry, ulong Bytes)> kv in typeStats)
        {
            var ts = kv.Value;
            long typeTotal = ts.HttpClient + ts.HttpWebRequest + ts.HttpWebResponse + ts.HttpMessageHandler
                + ts.ServicePoint + ts.ActiveHandlerTrackingEntry + ts.ExpiredHandlerTrackingEntry;
            byType.Add(new HttpObjectTypeSummary(kv.Key, typeTotal, ts.Bytes));

            totalHttpClient += ts.HttpClient;
            totalHttpWebRequest += ts.HttpWebRequest;
            totalHttpWebResponse += ts.HttpWebResponse;
            totalHttpMessageHandler += ts.HttpMessageHandler;
            totalServicePoint += ts.ServicePoint;
            totalActiveHandlerTrackingEntry += ts.ActiveHandlerTrackingEntry;
            totalExpiredHandlerTrackingEntry += ts.ExpiredHandlerTrackingEntry;
            totalBytes += ts.Bytes;

            // Attribute HttpMessageHandler subclasses (Polly/logging/auth/application handlers) by
            // owning module, resolved once per distinct handler type, not per instance.
            if (ts.HttpMessageHandler > 0 && typeStatistics.GetTypeStatistics().TryGetValue(kv.Key, out HeapTypeStatistics stats))
            {
                handlerModuleTotals.TryGetValue(stats.ModuleName, out var existing);
                handlerModuleTotals[stats.ModuleName] = (existing.Count + ts.HttpMessageHandler, existing.Bytes + ts.Bytes);
            }
        }

        byType.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        var handlerModules = new List<HttpHandlerModuleSummary>(handlerModuleTotals.Count);
        foreach (KeyValuePair<string, (long Count, ulong Bytes)> kv in handlerModuleTotals)
            handlerModules.Add(new HttpHandlerModuleSummary(kv.Key, kv.Value.Count, kv.Value.Bytes));
        handlerModules.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        long total = totalHttpClient + totalHttpWebRequest + totalHttpWebResponse
            + totalHttpMessageHandler + totalServicePoint
            + totalActiveHandlerTrackingEntry + totalExpiredHandlerTrackingEntry;

        // Per-module population — bounded to distinct handler-owning-module count, not per
        // instance. Backs the old finding generator's "largest contributor" module narrative,
        // which HttpMessageHandler instances (never individually observed — see below) can't.
        // Emitted in handlerModules' own already-sorted-descending order (matches ArraySynthesisRule's
        // trust-emission-order structure), so a synthesis rule can trust index 0 as "the largest".
        foreach (HttpHandlerModuleSummary module in handlerModules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await observationContext.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.http-handler-module",
                Subjects = [new ModuleRef { SimpleName = module.ModuleName, Fidelity = MatchFidelity.Exact }],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(module.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    AnalyzerKey = "HTTP Object Analysis",
                    CapabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes },
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }

        // Whole-run scalar — no entity subject, same "no natural per-entity anchor" pattern as
        // gc.generation-composition. HttpWebResponse and HttpMessageHandler instances are counted
        // but never individually observed (no per-instance field extraction exists for either
        // category), so their counts can't be reconstructed any other way. The other 11 measures
        // (added Phase 5 step 4 for HttpObjectTrendComparer's headline metrics, Bucket D of
        // docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md) were already
        // accumulated as locals here too, just never promoted. handler-client.ratio is a plain
        // derived ratio (HttpMessageHandlerCount / HttpClientCount, same formula as
        // HttpObjectTrendComparer's own HandlerClientRatio), not a classifier composite.
        await observationContext.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "infra.http-object-population",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["total.count"] = new Measure(total, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["http-client.count"] = new Measure(totalHttpClient, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["http-web-request.count"] = new Measure(totalHttpWebRequest, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["http-web-response.count"] = new Measure(totalHttpWebResponse, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["http-message-handler.count"] = new Measure(totalHttpMessageHandler, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["service-point.count"] = new Measure(totalServicePoint, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["active-handler-tracking-entry.count"] = new Measure(totalActiveHandlerTrackingEntry, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["expired-handler-tracking-entry.count"] = new Measure(totalExpiredHandlerTrackingEntry, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["http-client-gen0.count"] = new Measure(httpClientGen0, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["http-client-gen1.count"] = new Measure(httpClientGen1, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["http-client-gen2.count"] = new Measure(httpClientGen2, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["handler-client.ratio"] = new Measure(
                    totalHttpClient > 0 ? totalHttpMessageHandler / (double)totalHttpClient : 0.0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                ["total.bytes"] = new Measure(totalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            },
            Provenance = new Provenance
            {
                Artifact = observationContext.ArtifactId!.Value,
                AnalyzerKey = "HTTP Object Analysis",
                CapabilitiesUsed = observationCapabilities,
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);

        return new HttpObjectDomainResult(
            HttpObjectsFound: total > 0,
            TotalHttpObjects: total,
            HttpClientCount: totalHttpClient,
            HttpWebRequestCount: totalHttpWebRequest,
            HttpWebResponseCount: totalHttpWebResponse,
            HttpMessageHandlerCount: totalHttpMessageHandler,
            ServicePointCount: totalServicePoint,
            ActiveHandlerTrackingEntryCount: totalActiveHandlerTrackingEntry,
            ExpiredHandlerTrackingEntryCount: totalExpiredHandlerTrackingEntry,
            HttpClientGen0Count: httpClientGen0,
            HttpClientGen1Count: httpClientGen1,
            HttpClientGen2Count: httpClientGen2,
            TotalBytes: totalBytes,
            ByType: byType,
            TopHttpInstances: topSamples,
            HandlerModules: handlerModules);
    }

    /// <summary><paramref name="generationOrdinal"/> (HttpClient only) and
    /// <paramref name="connectionLimit"/> (ServicePoint only) back the old finding generator's
    /// Gen0/Gen2 reuse-vs-churn narrative and low-ConnectionLimit-queuing narrative respectively
    /// (Phase 5 step 2) — both omitted, not zeroed, for every other category, the same "presence
    /// means resolved" convention <c>shallow.bytes</c>'s sibling optional measures elsewhere use.</summary>
    private static ValueTask EmitHttpObservationAsync(
        AnalysisContext observationContext, HeapObjectRef candidate, IReadOnlySet<Capability> observationCapabilities, CancellationToken cancellationToken,
        int? generationOrdinal = null, int? connectionLimit = null)
    {
        var measures = new Dictionary<string, Measure>
        {
            ["shallow.bytes"] = new Measure(candidate.Size, MeasureUnit.Bytes, MeasureSemantics.Absolute),
        };
        if (generationOrdinal is int generation)
            measures["generation.ordinal"] = new Measure(generation, MeasureUnit.Custom, MeasureSemantics.Absolute);
        if (connectionLimit is int limit)
            measures["connection-limit"] = new Measure(limit, MeasureUnit.Count, MeasureSemantics.Absolute);

        return observationContext.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "infra.http-object",
            Subjects = [candidate.Type],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = measures,
            Provenance = new Provenance
            {
                Artifact = observationContext.ArtifactId!.Value,
                AnalyzerKey = "HTTP Object Analysis",
                CapabilitiesUsed = observationCapabilities,
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);
    }

    private static HttpInstanceSnapshot? TrySampleHttpClient(IDumpObjectFieldQuery fields, ulong address, string typeName)
    {
        string? baseAddress = null;
        long timeoutMilliseconds = -1;

        if (fields.TryReadField(address, "_baseAddress", out FieldValue baseAddressValue)
            && baseAddressValue.Kind == FieldValueKind.ObjectReference
            && fields.TryGetStringValue(baseAddressValue.AsObjectAddress(), out string? uri))
        {
            baseAddress = uri;
        }

        // _timeout is a TimeSpan's internal ticks field (long). Unlike the pre-retyping analyzer,
        // which called Read<long> unconditionally once the field was found, this checks the field's
        // shape first (matches every other Tier-2 read site in this batch) -- a documented, slightly
        // more defensive divergence, not a behavior change for the real System.TimeSpan field.
        if (fields.TryReadField(address, "_timeout", out FieldValue timeoutValue) && timeoutValue.Kind == FieldValueKind.Int64)
        {
            long ticks = timeoutValue.AsInt64();
            if (ticks >= 0)
                timeoutMilliseconds = ticks / TimeSpan.TicksPerMillisecond;
        }

        return new HttpInstanceSnapshot("HttpClient", typeName, address, baseAddress, timeoutMilliseconds);
    }

    // HttpWebRequest's private field layout differs by runtime, so field names are tried in order
    // rather than assumed:
    // .NET 5+ (HttpWebRequest is a compatibility shim over HttpClient): _requestUri (Uri),
    //   pending-response signalled by _beginGetResponseCalled && !_endGetResponseCalled.
    // .NET Framework (original implementation): _Uri (Uri), pending-response signalled by
    //   m_RequestSubmitted (request sent) with _HttpResponse still null (no response yet).
    private static readonly string[] RequestUriFieldNames = ["_requestUri", "_Uri"];

    private static HttpInstanceSnapshot? TrySampleHttpWebRequest(IDumpObjectFieldQuery fields, ulong address, string typeName)
    {
        string? requestUri = null;
        bool responsePending = false;

        foreach (string fieldName in RequestUriFieldNames)
        {
            if (fields.TryReadField(address, fieldName, out FieldValue uriValue)
                && uriValue.Kind == FieldValueKind.ObjectReference
                && fields.TryGetStringValue(uriValue.AsObjectAddress(), out string? uri))
            {
                requestUri = uri;
                break;
            }
        }

        bool hasBegin = fields.TryReadField(address, "_beginGetResponseCalled", out FieldValue beginValue) && beginValue.Kind == FieldValueKind.Bool;
        bool hasEnd = fields.TryReadField(address, "_endGetResponseCalled", out FieldValue endValue) && endValue.Kind == FieldValueKind.Bool;
        if (hasBegin && hasEnd)
        {
            responsePending = beginValue.AsBool() && !endValue.AsBool();
        }
        else if (fields.TryReadField(address, "m_RequestSubmitted", out FieldValue submittedValue) && submittedValue.Kind == FieldValueKind.Bool
            && fields.TryReadField(address, "_HttpResponse", out FieldValue httpResponseValue))
        {
            bool responseValid = httpResponseValue.Kind == FieldValueKind.ObjectReference;
            responsePending = submittedValue.AsBool() && !responseValid;
        }

        return new HttpInstanceSnapshot("HttpWebRequest", typeName, address, requestUri, ResponsePending: responsePending);
    }

    // ActiveHandlerTrackingEntry.Name / ExpiredHandlerTrackingEntry.Name are both auto-properties
    // compiled to the same backing-field name across every version checked (6.0.0/8.0.0/9.0.5):
    // "<Name>k__BackingField". It's the logical client name passed to
    // IHttpClientFactory.CreateClient(name) -- a key signal for spotting a named client rotating
    // through handlers.
    private static HttpInstanceSnapshot? TrySampleHandlerTrackingEntry(IDumpObjectFieldQuery fields, ulong address, string typeName, string category)
    {
        string? clientName = null;
        if (fields.TryReadField(address, "<Name>k__BackingField", out FieldValue nameValue)
            && nameValue.Kind == FieldValueKind.ObjectReference
            && fields.TryGetStringValue(nameValue.AsObjectAddress(), out string? name))
        {
            clientName = name;
        }

        return new HttpInstanceSnapshot(category, typeName, address, ClientName: clientName);
    }

    // HttpWebRequest's own URI field names differ .NET Framework vs .NET 5+; ServicePoint's
    // connection-limit field similarly differs by runtime ("_connectionLimit" vs "m_ConnectionLimit").
    private static readonly string[] ConnectionLimitFieldNames = ["_connectionLimit", "m_ConnectionLimit"];

    private static HttpInstanceSnapshot? TrySampleServicePoint(IDumpObjectFieldQuery fields, ulong address, string typeName)
    {
        int? connectionLimit = null;
        foreach (string fieldName in ConnectionLimitFieldNames)
        {
            if (fields.TryReadField(address, fieldName, out FieldValue value) && value.Kind == FieldValueKind.Int32)
            {
                connectionLimit = value.AsInt32();
                break;
            }
        }

        return new HttpInstanceSnapshot("ServicePoint", typeName, address, ConnectionLimit: connectionLimit);
    }

    private static readonly string[] HttpNamespacePrefixes = ["System.Net.Http."];
    private static readonly string[] HttpMessageHandlerTokens = ["HttpMessageHandler"];

    // Direct or subclass of HttpMessageHandler in System.Net.Http
    private static bool IsHttpMessageHandler(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, HttpNamespacePrefixes, "Handler", HttpMessageHandlerTokens);

    internal static HttpObjectCategory ClassifyType(string typeName)
    {
        if (typeName.Equals("System.Net.Http.HttpClient", StringComparison.Ordinal))
            return HttpObjectCategory.HttpClient;
        if (typeName.Equals("System.Net.HttpWebRequest", StringComparison.Ordinal))
            return HttpObjectCategory.HttpWebRequest;
        if (typeName.Equals("System.Net.HttpWebResponse", StringComparison.Ordinal))
            return HttpObjectCategory.HttpWebResponse;
        if (typeName.Equals("System.Net.ServicePoint", StringComparison.Ordinal))
            return HttpObjectCategory.ServicePoint;
        // HttpMessageHandler subclasses: covers SocketsHttpHandler, DelegatingHandler, etc.
        if (IsHttpMessageHandler(typeName))
            return HttpObjectCategory.HttpMessageHandler;
        // IHttpClientFactory's internal handler-rotation bookkeeping types. Both top-level,
        // non-nested, internal sealed classes, verified stable across 6.0.0/8.0.0/9.0.5
        // (netstandard2.0/net461/net6+/net9+ TFMs).
        if (typeName.Equals("Microsoft.Extensions.Http.ActiveHandlerTrackingEntry", StringComparison.Ordinal))
            return HttpObjectCategory.ActiveHandlerTrackingEntry;
        if (typeName.Equals("Microsoft.Extensions.Http.ExpiredHandlerTrackingEntry", StringComparison.Ordinal))
            return HttpObjectCategory.ExpiredHandlerTrackingEntry;
        return HttpObjectCategory.None;
    }

    internal enum HttpObjectCategory { None, HttpClient, HttpWebRequest, HttpWebResponse, HttpMessageHandler, ServicePoint, ActiveHandlerTrackingEntry, ExpiredHandlerTrackingEntry }

    private static HttpObjectDomainResult Empty() => new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], []);

    public void Dispose() { }
}
