using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Scans the managed heap for HTTP-related objects (HttpClient, HttpWebRequest/Response,
/// HttpMessageHandler subclasses, ServicePoint).
///
/// Common misuse patterns detected:
///   - Multiple HttpClient instances: should be singletons (HttpClientFactory pattern).
///   - HttpWebResponse objects: indicate responses not disposed, holding sockets.
///   - ServicePoint accumulation: can exhaust the system-level connection table.
/// </summary>
public sealed class HttpObjectAnalyzer : IAnalyzer, IHeapIndexScanParticipant, ITypedResourceCandidateSource, ITypedResourceInstanceSampler<HttpInstanceSnapshot>
{
    public string Name => "HTTP Object Analysis";
    public string Category => "Infrastructure";

    public bool IsCandidateType(string typeName) => ClassifyType(typeName) != HttpObjectCategory.None;

    // Classifies a type name into one of the HTTP object categories.
    private static HttpObjectCategory ClassifyType(string typeName)
    {
        if (typeName.Equals("System.Net.Http.HttpClient", StringComparison.Ordinal))
            return HttpObjectCategory.HttpClient;
        if (typeName.Equals("System.Net.HttpWebRequest", StringComparison.Ordinal))
            return HttpObjectCategory.HttpWebRequest;
        if (typeName.Equals("System.Net.HttpWebResponse", StringComparison.Ordinal))
            return HttpObjectCategory.HttpWebResponse;
        if (typeName.Equals("System.Net.ServicePoint", StringComparison.Ordinal))
            return HttpObjectCategory.ServicePoint;
        // HttpMessageHandler and subclasses: covers SocketsHttpHandler, DelegatingHandler, etc.
        if (IsHttpMessageHandler(typeName))
            return HttpObjectCategory.HttpMessageHandler;
        // IHttpClientFactory's internal handler-rotation bookkeeping types. Both are top-level,
        // non-nested, internal sealed classes — confirmed by decompiling Microsoft.Extensions.Http
        // 6.0.0/8.0.0/9.0.5 (netstandard2.0 and net461/net6+/net9+ TFMs all share this shape).
        if (typeName.Equals("Microsoft.Extensions.Http.ActiveHandlerTrackingEntry", StringComparison.Ordinal))
            return HttpObjectCategory.ActiveHandlerTrackingEntry;
        if (typeName.Equals("Microsoft.Extensions.Http.ExpiredHandlerTrackingEntry", StringComparison.Ordinal))
            return HttpObjectCategory.ExpiredHandlerTrackingEntry;
        return HttpObjectCategory.None;
    }

    private static readonly string[] HttpNamespacePrefixes = ["System.Net.Http."];
    private static readonly string[] HttpMessageHandlerTokens = ["HttpMessageHandler"];

    private static bool IsHttpMessageHandler(string typeName) =>
        // Direct or subclass of HttpMessageHandler in System.Net.Http
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, HttpNamespacePrefixes, "Handler", HttpMessageHandlerTokens);

    private static readonly string[] HttpClientBaseAddressFieldNames = ["_baseAddress"];
    private static readonly string[] HttpClientTimeoutFieldNames = ["_timeout"];

    HttpInstanceSnapshot? ITypedResourceInstanceSampler<HttpInstanceSnapshot>.TrySample(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        if (typeName.Equals("System.Net.Http.HttpClient", StringComparison.Ordinal))
            return TrySampleHttpClient(heap, in entry, typeName);
        if (typeName.Equals("System.Net.HttpWebRequest", StringComparison.Ordinal))
            return TrySampleHttpWebRequest(heap, in entry, typeName);
        if (typeName.Equals("Microsoft.Extensions.Http.ActiveHandlerTrackingEntry", StringComparison.Ordinal))
            return TrySampleHandlerTrackingEntry(heap, in entry, typeName, "ActiveHandlerTrackingEntry");
        if (typeName.Equals("Microsoft.Extensions.Http.ExpiredHandlerTrackingEntry", StringComparison.Ordinal))
            return TrySampleHandlerTrackingEntry(heap, in entry, typeName, "ExpiredHandlerTrackingEntry");
        if (typeName.Equals("System.Net.ServicePoint", StringComparison.Ordinal))
            return TrySampleServicePoint(heap, in entry, typeName);
        return null;
    }

    private static HttpInstanceSnapshot? TrySampleHttpClient(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        string? baseAddress = null;
        long timeoutMilliseconds = -1;

        try
        {
            var obj = heap.GetObject(entry.Address);
            if (!obj.IsValid || obj.Type == null)
                return null;

            // Try to read _baseAddress (Uri field)
            var baseAddressField = obj.Type.GetFieldByName("_baseAddress");
            if (baseAddressField != null)
            {
                var baseAddressObj = baseAddressField.ReadObject(entry.Address, interior: false);
                if (baseAddressObj.IsValid && baseAddressObj.AsString() is string uri)
                {
                    baseAddress = uri;
                }
            }

            // Try to read _timeout (TimeSpan ticks as long)
            var timeoutField = obj.Type.GetFieldByName("_timeout");
            if (timeoutField != null)
            {
                long ticks = timeoutField.Read<long>(entry.Address, interior: false);
                if (ticks >= 0)
                {
                    // Convert ticks to milliseconds
                    timeoutMilliseconds = ticks / TimeSpan.TicksPerMillisecond;
                }
            }
        }
        catch
        {
            // Silently ignore errors reading fields; we have what we could get
        }

        return new HttpInstanceSnapshot("HttpClient", typeName, entry.Address, baseAddress, timeoutMilliseconds);
    }

    // HttpWebRequest's private field layout differs by runtime, so field names are tried in
    // order rather than assumed:
    //   .NET 5+ (HttpWebRequest is a compatibility shim over HttpClient): _requestUri (Uri),
    //     pending-response signalled by _beginGetResponseCalled && !_endGetResponseCalled.
    //   .NET Framework (original implementation): _Uri (Uri), pending-response signalled by
    //     m_RequestSubmitted (request sent) with _HttpResponse still null (no response yet).
    private static readonly string[] RequestUriFieldNames = ["_requestUri", "_Uri"];

    private static HttpInstanceSnapshot? TrySampleHttpWebRequest(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        string? requestUri = null;
        bool responsePending = false;

        try
        {
            var obj = heap.GetObject(entry.Address);
            if (!obj.IsValid || obj.Type == null)
                return null;

            foreach (string fieldName in RequestUriFieldNames)
            {
                var requestUriField = obj.Type.GetFieldByName(fieldName);
                if (requestUriField == null)
                    continue;

                var requestUriObj = requestUriField.ReadObject(entry.Address, interior: false);
                if (requestUriObj.IsValid && requestUriObj.AsString() is string uri)
                {
                    requestUri = uri;
                    break;
                }
            }

            var beginGetResponseField = obj.Type.GetFieldByName("_beginGetResponseCalled");
            var endGetResponseField = obj.Type.GetFieldByName("_endGetResponseCalled");
            if (beginGetResponseField != null && endGetResponseField != null)
            {
                bool began = beginGetResponseField.Read<bool>(entry.Address, interior: false);
                bool ended = endGetResponseField.Read<bool>(entry.Address, interior: false);
                responsePending = began && !ended;
            }
            else
            {
                var requestSubmittedField = obj.Type.GetFieldByName("m_RequestSubmitted");
                var httpResponseField = obj.Type.GetFieldByName("_HttpResponse");
                if (requestSubmittedField != null && httpResponseField != null)
                {
                    bool submitted = requestSubmittedField.Read<bool>(entry.Address, interior: false);
                    var httpResponseObj = httpResponseField.ReadObject(entry.Address, interior: false);
                    responsePending = submitted && !httpResponseObj.IsValid;
                }
            }
        }
        catch
        {
            // Silently ignore errors reading fields; we have what we could get
        }

        return new HttpInstanceSnapshot("HttpWebRequest", typeName, entry.Address, requestUri, ResponsePending: responsePending);
    }

    // ActiveHandlerTrackingEntry.Name / ExpiredHandlerTrackingEntry.Name are both auto-properties
    // compiled to the same backing-field name across the versions checked (6.0.0/8.0.0/9.0.5):
    // "<Name>k__BackingField". It's the logical client name passed to
    // IHttpClientFactory.CreateClient(name) — the key signal for spotting which named client is
    // rotating handlers.
    private static HttpInstanceSnapshot? TrySampleHandlerTrackingEntry(ClrHeap heap, in HeapEntry entry, string typeName, string category)
    {
        string? clientName = null;

        try
        {
            var obj = heap.GetObject(entry.Address);
            if (!obj.IsValid || obj.Type == null)
                return null;

            var nameField = obj.Type.GetFieldByName("<Name>k__BackingField");
            if (nameField != null)
            {
                var nameObj = nameField.ReadObject(entry.Address, interior: false);
                if (nameObj.IsValid && nameObj.AsString() is string name)
                {
                    clientName = name;
                }
            }
        }
        catch
        {
            // Silently ignore errors reading fields; we have what we could get
        }

        return new HttpInstanceSnapshot(category, typeName, entry.Address, ClientName: clientName);
    }

    // Field name differs by runtime, same drift pattern as HttpWebRequest's URI field:
    // .NET 5+ uses "_connectionLimit"; .NET Framework uses "m_ConnectionLimit". A ServicePoint
    // with a low limit (e.g. the historical default of 2) is the actual bottleneck signal — the
    // ServicePoint *count* alone doesn't say whether any of them are constraining throughput.
    private static readonly string[] ConnectionLimitFieldNames = ["_connectionLimit", "m_ConnectionLimit"];

    private static HttpInstanceSnapshot? TrySampleServicePoint(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        int? connectionLimit = null;

        try
        {
            var obj = heap.GetObject(entry.Address);
            if (!obj.IsValid || obj.Type == null)
                return null;

            foreach (string fieldName in ConnectionLimitFieldNames)
            {
                var field = obj.Type.GetFieldByName(fieldName);
                if (field == null)
                    continue;

                connectionLimit = field.Read<int>(entry.Address, interior: false);
                break;
            }
        }
        catch
        {
            // Silently ignore errors reading fields; we have what we could get
        }

        return new HttpInstanceSnapshot("ServicePoint", typeName, entry.Address, ConnectionLimit: connectionLimit);
    }

    private enum HttpObjectCategory { None, HttpClient, HttpWebRequest, HttpWebResponse, HttpMessageHandler, ServicePoint, ActiveHandlerTrackingEntry, ExpiredHandlerTrackingEntry }

    // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
    // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
    // OnHeapEntry; consumed by AnalyzeAsync once the shared index scan has completed.
    private ClrHeap? _heap;
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private Dictionary<ulong, (string Name, long HttpClient, long HttpWebRequest, long HttpWebResponse, long HttpMessageHandler, long ServicePoint, long ActiveHandlerTrackingEntry, long ExpiredHandlerTrackingEntry, ulong Bytes)>? _typeStats;
    private InstanceStateSampler<HttpInstanceSnapshot>? _sampler;
    private bool _scanSucceeded;

    // HttpClient generation breakdown (analyzer-level, not per-type: HttpClient is matched by
    // exact type name, so there's normally exactly one candidate MethodTable). Gen2 confirms
    // long-lived reuse; Gen0/Gen1 confirms per-request allocation churn. entry.Generation comes
    // free from the disk-backed index — no extra ClrMD reads.
    private long _httpClientGen0;
    private long _httpClientGen1;
    private long _httpClientGen2;

    /// <summary>
    /// Resolves candidate HTTP-type MethodTables and pre-seeds per-type counters from
    /// TypeAggregates, exactly mirroring the historical single-shot "Step 1 + pre-seed" logic.
    /// </summary>
    void IHeapIndexScanParticipant.BeforeHeapIndexScan(AnalysisContext context)
    {
        _scanSucceeded = false;
        _heap = context.Heap;
        _httpClientGen0 = 0;
        _httpClientGen1 = 0;
        _httpClientGen2 = 0;

        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> candidateMts =
            TypedResourceScanDriver.DiscoverCandidates(this, context.Heap, context.Cache);
        _candidateMts = candidateMts;

        var typeStats = new Dictionary<ulong, (string Name, long HttpClient, long HttpWebRequest, long HttpWebResponse, long HttpMessageHandler, long ServicePoint, long ActiveHandlerTrackingEntry, long ExpiredHandlerTrackingEntry, ulong Bytes)>(candidateMts.Count);
        foreach (KeyValuePair<ulong, (string TypeName, long Count, ulong Bytes)> kv in candidateMts)
        {
            // Pre-seed from TypeAggregates when available (no heap access needed for counts)
            typeStats[kv.Key] = (kv.Value.TypeName, 0, 0, 0, 0, 0, 0, 0, kv.Value.Bytes);
        }

        _typeStats = typeStats;
        _sampler = TypedResourceScanDriver.CreateSampler(this);
    }

    /// <summary>
    /// Called once per disk-backed index entry, in address order, during the shared heap-index
    /// scan pass. Mirrors the historical fast-path loop body.
    /// Explicit interface implementation because <see cref="HeapEntry"/> is internal and this
    /// class is public — an implicit implementation would leak the internal type as public API.
    /// </summary>
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
    {
        var candidateMts = _candidateMts;
        var typeStats = _typeStats;
        var sampler = _sampler;

        if (candidateMts is null || typeStats is null)
            return;

        if (!candidateMts.ContainsKey(entry.MethodTable))
            return;

        if (!typeStats.TryGetValue(entry.MethodTable, out var ts))
            return;

        string typeName = ts.Name;
        var category = ClassifyType(typeName);

        long httpClient = ts.HttpClient;
        long httpWebRequest = ts.HttpWebRequest;
        long httpWebResponse = ts.HttpWebResponse;
        long httpMessageHandler = ts.HttpMessageHandler;
        long servicePoint = ts.ServicePoint;
        long activeHandlerTrackingEntry = ts.ActiveHandlerTrackingEntry;
        long expiredHandlerTrackingEntry = ts.ExpiredHandlerTrackingEntry;

        switch (category)
        {
            case HttpObjectCategory.HttpClient:
                httpClient++;
                if (entry.Generation == 0) _httpClientGen0++;
                else if (entry.Generation == 1) _httpClientGen1++;
                else if (entry.Generation >= 2) _httpClientGen2++;
                // Try to sample this HttpClient instance if sampler is available
                if (sampler is not null && _heap is not null)
                {
                    HttpInstanceSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, _heap, in entry, typeName);
                    if (snap is not null)
                        sampler.AddTopSample(snap);
                }
                break;
            case HttpObjectCategory.HttpWebRequest:
                httpWebRequest++;
                // Try to sample this HttpWebRequest instance if sampler is available
                if (sampler is not null && _heap is not null)
                {
                    HttpInstanceSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, _heap, in entry, typeName);
                    if (snap is not null)
                        sampler.AddTopSample(snap);
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
                if (sampler is not null && _heap is not null)
                {
                    HttpInstanceSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, _heap, in entry, typeName);
                    if (snap is not null)
                        sampler.AddTopSample(snap);
                }
                break;
            case HttpObjectCategory.ActiveHandlerTrackingEntry:
                activeHandlerTrackingEntry++;
                if (sampler is not null && _heap is not null)
                {
                    HttpInstanceSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, _heap, in entry, typeName);
                    if (snap is not null)
                        sampler.AddTopSample(snap);
                }
                break;
            case HttpObjectCategory.ExpiredHandlerTrackingEntry:
                expiredHandlerTrackingEntry++;
                if (sampler is not null && _heap is not null)
                {
                    HttpInstanceSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, _heap, in entry, typeName);
                    if (snap is not null)
                        sampler.AddTopSample(snap);
                }
                break;
        }

        typeStats[entry.MethodTable] = (typeName, httpClient, httpWebRequest, httpWebResponse, httpMessageHandler, servicePoint, activeHandlerTrackingEntry, expiredHandlerTrackingEntry, ts.Bytes);
    }

    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded)
    {
        _scanSucceeded = succeeded;
    }

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(BuildResult().Stamp(this));
    }

    private HttpObjectDomainResult BuildResult()
    {
        if (_typeStats is null || !_scanSucceeded || _typeStats.Count == 0)
            return Empty();

        long totalHttpClient = 0, totalHttpWebRequest = 0, totalHttpWebResponse = 0;
        long totalHttpMessageHandler = 0, totalServicePoint = 0;
        long totalActiveHandlerTrackingEntry = 0, totalExpiredHandlerTrackingEntry = 0;
        ulong totalBytes = 0;

        var byType = new List<HttpObjectTypeSummary>(_typeStats.Count);
        var handlerModuleTotals = new Dictionary<string, (long Count, ulong Bytes)>();

        foreach (var kv in _typeStats)
        {
            var ts = kv.Value;
            long count = ts.HttpClient + ts.HttpWebRequest + ts.HttpWebResponse + ts.HttpMessageHandler + ts.ServicePoint
                       + ts.ActiveHandlerTrackingEntry + ts.ExpiredHandlerTrackingEntry;

            totalHttpClient += ts.HttpClient;
            totalHttpWebRequest += ts.HttpWebRequest;
            totalHttpWebResponse += ts.HttpWebResponse;
            totalHttpMessageHandler += ts.HttpMessageHandler;
            totalServicePoint += ts.ServicePoint;
            totalActiveHandlerTrackingEntry += ts.ActiveHandlerTrackingEntry;
            totalExpiredHandlerTrackingEntry += ts.ExpiredHandlerTrackingEntry;
            totalBytes += ts.Bytes;

            byType.Add(new HttpObjectTypeSummary(ts.Name, count, ts.Bytes));

            // Resolved once per distinct handler type (bounded by distinct HttpMessageHandler
            // subclasses seen, not per instance) to distinguish Polly/logging/auth/application
            // handlers by owning module.
            if (ts.HttpMessageHandler > 0 && _heap is not null)
            {
                string moduleName = TypeAggregateNameResolver.ResolveModuleName(_heap, kv.Key, sampleAddress: 0);
                handlerModuleTotals.TryGetValue(moduleName, out var existing);
                handlerModuleTotals[moduleName] = (existing.Count + ts.HttpMessageHandler, existing.Bytes + ts.Bytes);
            }
        }

        byType.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        var handlerModules = new List<HttpHandlerModuleSummary>(handlerModuleTotals.Count);
        foreach (var kv in handlerModuleTotals)
            handlerModules.Add(new HttpHandlerModuleSummary(kv.Key, kv.Value.Count, kv.Value.Bytes));
        handlerModules.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        long total = totalHttpClient + totalHttpWebRequest + totalHttpWebResponse
                   + totalHttpMessageHandler + totalServicePoint
                   + totalActiveHandlerTrackingEntry + totalExpiredHandlerTrackingEntry;

        return new HttpObjectDomainResult(
            HttpObjectsFound:                    total > 0,
            TotalHttpObjects:                    total,
            HttpClientCount:                     totalHttpClient,
            HttpWebRequestCount:                 totalHttpWebRequest,
            HttpWebResponseCount:                totalHttpWebResponse,
            HttpMessageHandlerCount:             totalHttpMessageHandler,
            ServicePointCount:                   totalServicePoint,
            ActiveHandlerTrackingEntryCount:     totalActiveHandlerTrackingEntry,
            ExpiredHandlerTrackingEntryCount:    totalExpiredHandlerTrackingEntry,
            HttpClientGen0Count:                 _httpClientGen0,
            HttpClientGen1Count:                 _httpClientGen1,
            HttpClientGen2Count:                 _httpClientGen2,
            TotalBytes:                          totalBytes,
            ByType:                              byType,
            TopHttpInstances:                    _sampler?.TopSamples ?? [],
            HandlerModules:                      handlerModules);
    }

    private static HttpObjectDomainResult Empty() =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], []);
}
