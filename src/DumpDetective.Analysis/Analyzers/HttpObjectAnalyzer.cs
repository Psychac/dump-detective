using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
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
public sealed class HttpObjectAnalyzer : IAnalyzer
{
    public string Name => "HTTP Object Analysis";
    public string Category => "Infrastructure";

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

    private static bool IsHttpMessageHandler(string typeName)
    {
        // Direct or subclass of HttpMessageHandler in System.Net.Http
        return typeName.StartsWith("System.Net.Http.", StringComparison.Ordinal)
            && (typeName.EndsWith("Handler", StringComparison.Ordinal)
                || typeName.Contains("HttpMessageHandler", StringComparison.Ordinal));
    }

    private enum HttpObjectCategory { None, HttpClient, HttpWebRequest, HttpWebResponse, HttpMessageHandler, ServicePoint }

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            Analyze(context.Heap, context.Cache, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(
        ClrHeap? heap,
        IHeapAnalysisCache? cache,
        CancellationToken cancellationToken)
    {
        if (heap is null)
            return Empty();

        // ── Step 1: Identify matching MTs from TypeAggregates ─────────────────
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
        if (cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            typeAggregates = idx?.TypeAggregates;

        // MT → (TypeName, Category, count, bytes) — populated from TypeAggregates
        var candidateMts = new Dictionary<ulong, (string TypeName, HttpObjectCategory Category, long Count, ulong Bytes)>(16);

        if (typeAggregates is not null)
        {
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClrType? clrType = heap.GetTypeByMethodTable(kv.Key);
                if (clrType?.Name is not string fullName) continue;
                HttpObjectCategory cat = ClassifyType(fullName);
                if (cat == HttpObjectCategory.None) continue;
                candidateMts[kv.Key] = (fullName, cat, kv.Value.Count, kv.Value.TotalSize);
            }
        }
        else
        {
            // Fallback: full heap scan to discover types
            var seenMts = new HashSet<ulong>();
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!obj.IsValid || obj.Type is null) continue;
                ulong mt = obj.Type.MethodTable;
                if (!seenMts.Add(mt)) continue; // only check each MT once
                string typeName = obj.Type.Name ?? string.Empty;
                HttpObjectCategory cat = ClassifyType(typeName);
                if (cat == HttpObjectCategory.None) continue;
                candidateMts[mt] = (typeName, cat, 0, 0);
            }
        }

        if (candidateMts.Count == 0)
            return Empty();

        // ── Step 2: Build per-category and per-type summaries ─────────────────
        int httpClientCount = 0, httpWebRequestCount = 0, httpWebResponseCount = 0;
        int httpMessageHandlerCount = 0, servicePointCount = 0;
        ulong totalBytes = 0;

        var byType = new List<HttpObjectTypeSummary>(candidateMts.Count);

        foreach (KeyValuePair<ulong, (string TypeName, HttpObjectCategory Category, long Count, ulong Bytes)> kv in candidateMts)
        {
            int count = (int)Math.Min(kv.Value.Count, int.MaxValue);
            ulong bytes = kv.Value.Bytes;

            switch (kv.Value.Category)
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
