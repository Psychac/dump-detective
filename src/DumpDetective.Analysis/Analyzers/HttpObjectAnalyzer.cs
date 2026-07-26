using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

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
public sealed class HttpObjectAnalyzer : IAnalyzer, ITypedResourceCandidateSource
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

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            Analyze(context.Heap, context.Cache, cancellationToken).Stamp(this));
    }

    private AnalyzerDomainResult Analyze(
        ClrHeap? heap,
        IHeapAnalysisCache? cache,
        CancellationToken cancellationToken)
    {
        if (heap is null)
            return Empty();

        // ── Step 1: Identify matching MTs via the shared candidate-type scanner ──
        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> candidates =
            TypedResourceScanDriver.DiscoverCandidates(this, heap, cache, cancellationToken);

        if (candidates.Count == 0)
            return Empty();

        // ── Step 2: Build per-category and per-type summaries ─────────────────
        int httpClientCount = 0, httpWebRequestCount = 0, httpWebResponseCount = 0;
        int httpMessageHandlerCount = 0, servicePointCount = 0;
        ulong totalBytes = 0;

        var byType = new List<HttpObjectTypeSummary>(candidates.Count);

        foreach (KeyValuePair<ulong, (string TypeName, long Count, ulong Bytes)> kv in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(kv.Value.Count, int.MaxValue);
            ulong bytes = kv.Value.Bytes;

            switch (ClassifyType(kv.Value.TypeName))
            {
                case HttpObjectCategory.HttpClient:        httpClientCount        += count; break;
                case HttpObjectCategory.HttpWebRequest:    httpWebRequestCount    += count; break;
                case HttpObjectCategory.HttpWebResponse:   httpWebResponseCount   += count; break;
                case HttpObjectCategory.HttpMessageHandler: httpMessageHandlerCount += count; break;
                case HttpObjectCategory.ServicePoint:      servicePointCount      += count; break;
            }

            totalBytes += bytes;
            byType.Add(new HttpObjectTypeSummary(kv.Value.TypeName, count, bytes));
        }

        byType.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        int total = httpClientCount + httpWebRequestCount + httpWebResponseCount
                  + httpMessageHandlerCount + servicePointCount;

        return new HttpObjectDomainResult(
            HttpObjectsFound:         total > 0,
            TotalHttpObjects:         total,
            HttpClientCount:          httpClientCount,
            HttpWebRequestCount:      httpWebRequestCount,
            HttpWebResponseCount:     httpWebResponseCount,
            HttpMessageHandlerCount:  httpMessageHandlerCount,
            ServicePointCount:        servicePointCount,
            TotalBytes:               totalBytes,
            ByType:                   byType);
    }

    private static HttpObjectDomainResult Empty() =>
        new(false, 0, 0, 0, 0, 0, 0, 0, []);
}
