namespace DumpDetective.Analysis.Models;

/// <summary>
/// Per-type count/size summary for an HTTP-related type found on the heap.
/// </summary>
internal sealed record HttpObjectTypeSummary(
    string TypeName,
    long Count,
    ulong TotalBytes);

/// <summary>
/// Per-module count/size summary for HttpMessageHandler subclasses, distinguishing e.g. Polly
/// pipelines, logging/auth DelegatingHandlers, and application-layer handlers by owning module —
/// resolved once per distinct handler type, not per instance.
/// </summary>
internal sealed record HttpHandlerModuleSummary(
    string ModuleName,
    long Count,
    ulong TotalBytes);

/// <summary>
/// Lightweight snapshot of a single HttpClient, HttpWebRequest, ServicePoint, or
/// IHttpClientFactory handler tracking entry object. <see cref="Uri"/> holds the base address
/// (HttpClient) or request URI (HttpWebRequest); <see cref="TimeoutMilliseconds"/>,
/// <see cref="ResponsePending"/>, <see cref="ClientName"/>, and <see cref="ConnectionLimit"/> are
/// populated only for the category they apply to.
/// </summary>
internal sealed record HttpInstanceSnapshot(
    string Category,
    string TypeName,
    ulong Address,
    string? Uri = null,
    long TimeoutMilliseconds = -1,
    bool ResponsePending = false,
    string? ClientName = null,
    int? ConnectionLimit = null);

/// <summary>
/// Domain result produced by <c>HttpObjectAnalyzer</c>.
/// </summary>
internal sealed record HttpObjectDomainResult(
    bool HttpObjectsFound,
    long TotalHttpObjects,
    long HttpClientCount,
    long HttpWebRequestCount,
    long HttpWebResponseCount,
    long HttpMessageHandlerCount,
    long ServicePointCount,
    long ActiveHandlerTrackingEntryCount,
    long ExpiredHandlerTrackingEntryCount,
    long HttpClientGen0Count,
    long HttpClientGen1Count,
    long HttpClientGen2Count,
    ulong TotalBytes,
    IReadOnlyList<HttpObjectTypeSummary> ByType,
    IReadOnlyList<HttpInstanceSnapshot> TopHttpInstances,
    IReadOnlyList<HttpHandlerModuleSummary> HandlerModules) : DumpDetective.Core.Models.AnalyzerDomainResult;

