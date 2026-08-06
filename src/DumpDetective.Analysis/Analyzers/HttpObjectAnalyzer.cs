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
public sealed class HttpObjectAnalyzer : IAnalyzer, IHeapIndexScanParticipant, ITypedResourceCandidateSource
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
        return HttpObjectCategory.None;
    }

    private static readonly string[] HttpNamespacePrefixes = ["System.Net.Http."];
    private static readonly string[] HttpMessageHandlerTokens = ["HttpMessageHandler"];

    private static bool IsHttpMessageHandler(string typeName) =>
        // Direct or subclass of HttpMessageHandler in System.Net.Http
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, HttpNamespacePrefixes, "Handler", HttpMessageHandlerTokens);

    private enum HttpObjectCategory { None, HttpClient, HttpWebRequest, HttpWebResponse, HttpMessageHandler, ServicePoint }

    // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
    // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
    // OnHeapEntry; consumed by AnalyzeAsync once the shared index scan has completed.
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private Dictionary<ulong, (string Name, int HttpClient, int HttpWebRequest, int HttpWebResponse, int HttpMessageHandler, int ServicePoint, ulong Bytes)>? _typeStats;
    private bool _scanSucceeded;

    /// <summary>
    /// Resolves candidate HTTP-type MethodTables and pre-seeds per-type counters from
    /// TypeAggregates, exactly mirroring the historical single-shot "Step 1 + pre-seed" logic.
    /// </summary>
    void IHeapIndexScanParticipant.BeforeHeapIndexScan(AnalysisContext context)
    {
        _scanSucceeded = false;

        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> candidateMts =
            TypedResourceScanDriver.DiscoverCandidates(this, context.Heap, context.Cache);
        _candidateMts = candidateMts;

        var typeStats = new Dictionary<ulong, (string Name, int HttpClient, int HttpWebRequest, int HttpWebResponse, int HttpMessageHandler, int ServicePoint, ulong Bytes)>(candidateMts.Count);
        foreach (KeyValuePair<ulong, (string TypeName, long Count, ulong Bytes)> kv in candidateMts)
        {
            // Pre-seed from TypeAggregates when available (no heap access needed for counts)
            int total = (int)Math.Min(kv.Value.Count, int.MaxValue);
            typeStats[kv.Key] = (kv.Value.TypeName, 0, 0, 0, 0, 0, kv.Value.Bytes);
        }

        _typeStats = typeStats;
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

        if (candidateMts is null || typeStats is null)
            return;

        if (!candidateMts.ContainsKey(entry.MethodTable))
            return;

        if (!typeStats.TryGetValue(entry.MethodTable, out var ts))
            return;

        string typeName = ts.Name;
        var category = ClassifyType(typeName);

        int httpClient = ts.HttpClient;
        int httpWebRequest = ts.HttpWebRequest;
        int httpWebResponse = ts.HttpWebResponse;
        int httpMessageHandler = ts.HttpMessageHandler;
        int servicePoint = ts.ServicePoint;

        switch (category)
        {
            case HttpObjectCategory.HttpClient:
                httpClient++;
                break;
            case HttpObjectCategory.HttpWebRequest:
                httpWebRequest++;
                break;
            case HttpObjectCategory.HttpWebResponse:
                httpWebResponse++;
                break;
            case HttpObjectCategory.HttpMessageHandler:
                httpMessageHandler++;
                break;
            case HttpObjectCategory.ServicePoint:
                servicePoint++;
                break;
        }

        typeStats[entry.MethodTable] = (typeName, httpClient, httpWebRequest, httpWebResponse, httpMessageHandler, servicePoint, ts.Bytes);
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

        int totalHttpClient = 0, totalHttpWebRequest = 0, totalHttpWebResponse = 0;
        int totalHttpMessageHandler = 0, totalServicePoint = 0;
        ulong totalBytes = 0;

        var byType = new List<HttpObjectTypeSummary>(_typeStats.Count);

        foreach (var kv in _typeStats)
        {
            var ts = kv.Value;
            int count = ts.HttpClient + ts.HttpWebRequest + ts.HttpWebResponse + ts.HttpMessageHandler + ts.ServicePoint;

            totalHttpClient += ts.HttpClient;
            totalHttpWebRequest += ts.HttpWebRequest;
            totalHttpWebResponse += ts.HttpWebResponse;
            totalHttpMessageHandler += ts.HttpMessageHandler;
            totalServicePoint += ts.ServicePoint;
            totalBytes += ts.Bytes;

            byType.Add(new HttpObjectTypeSummary(ts.Name, count, ts.Bytes));
        }

        byType.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        int total = totalHttpClient + totalHttpWebRequest + totalHttpWebResponse
                  + totalHttpMessageHandler + totalServicePoint;

        return new HttpObjectDomainResult(
            HttpObjectsFound:         total > 0,
            TotalHttpObjects:         total,
            HttpClientCount:          totalHttpClient,
            HttpWebRequestCount:      totalHttpWebRequest,
            HttpWebResponseCount:     totalHttpWebResponse,
            HttpMessageHandlerCount:  totalHttpMessageHandler,
            ServicePointCount:        totalServicePoint,
            TotalBytes:               totalBytes,
            ByType:                   byType);
    }

    private static HttpObjectDomainResult Empty() =>
        new(false, 0, 0, 0, 0, 0, 0, 0, []);
}
