using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

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

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");
        IHeapSegmentQuery segments = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapTypeStatisticsQuery typeStatistics = context.HeapTypeStatistics
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapTypes}' capability.");

        LastResult = Analyze(fields, segments, typeStatistics, context.HeapObjects);
        return ValueTask.CompletedTask;
    }

    private static HttpObjectDomainResult Analyze(
        IDumpObjectFieldQuery fields, IHeapSegmentQuery segments, IHeapTypeStatisticsQuery typeStatistics, IHeapObjectStream? candidates)
    {
        if (candidates is null)
            return Empty();

        var typeStats = new Dictionary<string, (long HttpClient, long HttpWebRequest, long HttpWebResponse, long HttpMessageHandler, long ServicePoint, long ActiveHandlerTrackingEntry, long ExpiredHandlerTrackingEntry, ulong Bytes)>(StringComparer.Ordinal);
        var topSamples = new List<HttpInstanceSnapshot>();
        long httpClientGen0 = 0, httpClientGen1 = 0, httpClientGen2 = 0;

        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
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
                    switch (segments.GetGeneration(candidate.Address))
                    {
                        case HeapGenerationTag.Gen0: httpClientGen0++; break;
                        case HeapGenerationTag.Gen1: httpClientGen1++; break;
                        default: httpClientGen2++; break;
                    }
                    HttpInstanceSnapshot? clientSnap = TrySampleHttpClient(fields, candidate.Address, candidate.TypeDisplayName);
                    if (clientSnap is not null) topSamples.Add(clientSnap);
                    break;
                case HttpObjectCategory.HttpWebRequest:
                    httpWebRequest++;
                    HttpInstanceSnapshot? requestSnap = TrySampleHttpWebRequest(fields, candidate.Address, candidate.TypeDisplayName);
                    if (requestSnap is not null) topSamples.Add(requestSnap);
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
                    if (spSnap is not null) topSamples.Add(spSnap);
                    break;
                case HttpObjectCategory.ActiveHandlerTrackingEntry:
                    activeHandlerTrackingEntry++;
                    HttpInstanceSnapshot? activeSnap = TrySampleHandlerTrackingEntry(fields, candidate.Address, candidate.TypeDisplayName, "ActiveHandlerTrackingEntry");
                    if (activeSnap is not null) topSamples.Add(activeSnap);
                    break;
                case HttpObjectCategory.ExpiredHandlerTrackingEntry:
                    expiredHandlerTrackingEntry++;
                    HttpInstanceSnapshot? expiredSnap = TrySampleHandlerTrackingEntry(fields, candidate.Address, candidate.TypeDisplayName, "ExpiredHandlerTrackingEntry");
                    if (expiredSnap is not null) topSamples.Add(expiredSnap);
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
