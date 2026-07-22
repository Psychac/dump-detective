namespace DumpDetective.Analysis.Models;

// ── DB Connection Pool ─────────────────────────────────────────────────────────

/// <summary>
/// Per-type count/state summary for a DB connection type found on the heap.
/// </summary>
internal sealed record DbConnectionTypeSummary(
    string TypeName,
    int TotalCount,
    int OpenCount,
    int ClosedCount,
    int OtherCount,
    ulong TotalBytes);

/// <summary>
/// Lightweight snapshot of a single DB connection object, capped per scan.
/// </summary>
internal sealed record DbConnectionSnapshot(
    string TypeName,
    ulong Address,
    string StateLabel,
    int StateValue);

/// <summary>
/// Domain result produced by <c>DbConnectionAnalyzer</c>.
/// Reports the presence, count and open/broken state of ADO.NET connection objects on the heap.
/// </summary>
internal sealed record DbConnectionDomainResult(
    bool ConnectionsFound,
    int TotalConnections,
    int OpenConnections,
    int ClosedConnections,
    int OtherConnections,
    IReadOnlyList<DbConnectionTypeSummary> ByType,
    IReadOnlyList<DbConnectionSnapshot> TopOpenConnections,
    bool StateScanCapped) : DumpDetective.Core.Models.AnalyzerDomainResult;

// ── WCF Channel ───────────────────────────────────────────────────────────────

/// <summary>
/// Per-type count/state summary for a WCF channel type found on the heap.
/// </summary>
internal sealed record WcfChannelTypeSummary(
    string TypeName,
    int TotalCount,
    int OpenedCount,
    int FaultedCount,
    int ClosedCount,
    int OtherCount,
    ulong TotalBytes);

/// <summary>
/// Lightweight snapshot of a single WCF channel object.
/// </summary>
internal sealed record WcfChannelSnapshot(
    string TypeName,
    ulong Address,
    string StateLabel,
    int StateValue);

/// <summary>
/// Domain result produced by <c>WcfChannelAnalyzer</c>.
/// </summary>
internal sealed record WcfChannelDomainResult(
    bool WcfPresent,
    int TotalChannels,
    int OpenedChannels,
    int FaultedChannels,
    int ClosedChannels,
    int OtherChannels,
    IReadOnlyList<WcfChannelTypeSummary> ByType,
    IReadOnlyList<WcfChannelSnapshot> TopFaultedChannels,
    bool StateScanCapped) : DumpDetective.Core.Models.AnalyzerDomainResult;

// ── HTTP Objects ──────────────────────────────────────────────────────────────

/// <summary>
/// Per-type count/size summary for an HTTP-related type found on the heap.
/// </summary>
internal sealed record HttpObjectTypeSummary(
    string TypeName,
    int Count,
    ulong TotalBytes);

/// <summary>
/// Domain result produced by <c>HttpObjectAnalyzer</c>.
/// </summary>
internal sealed record HttpObjectDomainResult(
    bool HttpObjectsFound,
    int TotalHttpObjects,
    int HttpClientCount,
    int HttpWebRequestCount,
    int HttpWebResponseCount,
    int HttpMessageHandlerCount,
    int ServicePointCount,
    ulong TotalBytes,
    IReadOnlyList<HttpObjectTypeSummary> ByType) : DumpDetective.Core.Models.AnalyzerDomainResult;

// ── Timer Objects ───────────────────────────────────────────────────────────

/// <summary>
/// Per-type count/size summary for timer-related objects found on the heap.
/// </summary>
internal sealed record TimerObjectTypeSummary(
    string TypeName,
    int Count,
    ulong TotalBytes,
    Evidence? Evidence = null);

/// <summary>
/// Domain result produced by <c>TimerLeakAnalyzer</c>.
/// </summary>
internal sealed record TimerLeakDomainResult(
    bool TimersFound,
    int TotalTimers,
    int ThreadingTimerCount,
    int TimersTimerCount,
    int TimerQueueTimerCount,
    int TimerHolderCount,
    int OtherTimerCount,
    ulong TotalBytes,
    IReadOnlyList<TimerObjectTypeSummary> ByType) : DumpDetective.Core.Models.AnalyzerDomainResult;
