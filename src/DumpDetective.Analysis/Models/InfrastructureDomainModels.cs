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
    int BrokenCount,
    int OtherCount,
    int UnknownStateCount,
    ulong TotalBytes);

/// <summary>
/// Lightweight snapshot of a single DB connection object, capped per scan.
/// </summary>
internal sealed record DbConnectionSnapshot(
    string TypeName,
    ulong Address,
    string StateLabel,
    int StateValue,
    string? AnonymisedConnectionString = null,
    sbyte Generation = -1);

/// <summary>
/// Summary of connections grouped by pool (server/database).
/// </summary>
internal sealed record PoolSummary(
    string PoolIdentifier,
    int OpenConnections,
    int TotalConnections);

/// <summary>
/// Domain result produced by <c>DbConnectionAnalyzer</c>.
/// Reports the presence, count and open/broken state of ADO.NET connection objects on the heap.
/// </summary>
internal sealed record DbConnectionDomainResult(
    bool ConnectionsFound,
    int TotalConnections,
    int OpenConnections,
    int ClosedConnections,
    int BrokenConnections,
    int OtherConnections,
    int UnknownStateConnections,
    int Gen2OpenConnections,
    int Gen0OpenConnections,
    IReadOnlyList<DbConnectionTypeSummary> ByType,
    IReadOnlyList<DbConnectionSnapshot> TopOpenConnections,
    IReadOnlyList<PoolSummary> TopPools,
    bool StateScanCapped) : DumpDetective.Core.Models.AnalyzerDomainResult;

// ── SQL Transaction ───────────────────────────────────────────────────────────

/// <summary>
/// Per-type count/state summary for SQL transaction objects found on the heap.
/// </summary>
internal sealed record SqlTransactionTypeSummary(
    string TypeName,
    int TotalCount,
    int DisposedCount,
    int ActiveCount,
    int OtherCount,
    ulong TotalBytes);

/// <summary>
/// Lightweight snapshot of a single SQL transaction object.
/// </summary>
internal sealed record SqlTransactionSnapshot(
    string TypeName,
    ulong Address,
    string StateLabel,
    int StateValue);

/// <summary>
/// Domain result for SQL transaction analysis.
/// Reports orphaned or long-held transactions that prevent pool return.
/// </summary>
internal sealed record SqlTransactionDomainResult(
    bool TransactionsFound,
    int TotalTransactions,
    int DisposedCount,
    int ActiveCount,
    int OtherCount,
    IReadOnlyList<SqlTransactionTypeSummary> ByType,
    IReadOnlyList<SqlTransactionSnapshot> TopActiveTransactions,
    bool StateScanCapped) : DumpDetective.Core.Models.AnalyzerDomainResult;

// ── SQL Command ───────────────────────────────────────────────────────────────

/// <summary>
/// Per-type count summary for SQL command objects found on the heap.
/// </summary>
internal sealed record SqlCommandTypeSummary(
    string TypeName,
    int TotalCount,
    int DisposedCount,
    int ActiveCount,
    ulong TotalBytes);

/// <summary>
/// Domain result for SQL command analysis.
/// Reports outstanding commands that may hold connection resources.
/// </summary>
internal sealed record SqlCommandDomainResult(
    bool CommandsFound,
    int TotalCommands,
    int DisposedCount,
    int ActiveCount,
    IReadOnlyList<SqlCommandTypeSummary> ByType,
    bool StateScanCapped) : DumpDetective.Core.Models.AnalyzerDomainResult;

// ── WCF Channel ───────────────────────────────────────────────────────────────

/// <summary>
/// Per-type count/state summary for a WCF channel type found on the heap.
/// </summary>
internal sealed record WcfChannelTypeSummary(
    string TypeName,
    int TotalCount,
    int OpeningCount,
    int OpenedCount,
    int FaultedCount,
    int ClosingCount,
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
    int StateValue,
    string? RemoteAddress = null);

/// <summary>
/// Domain result produced by <c>WcfChannelAnalyzer</c>.
/// </summary>
internal sealed record WcfChannelDomainResult(
    bool WcfPresent,
    int TotalChannels,
    int OpeningChannels,
    int OpenedChannels,
    int FaultedChannels,
    int ClosingChannels,
    int ClosedChannels,
    int OtherChannels,
    IReadOnlyList<WcfChannelTypeSummary> ByType,
    IReadOnlyList<WcfChannelSnapshot> TopFaultedChannels,
    bool StateScanCapped,
    int FactoryCount = 0,
    ulong TotalBytes = 0) : DumpDetective.Core.Models.AnalyzerDomainResult;

// ── HTTP Objects ──────────────────────────────────────────────────────────────

/// <summary>
/// Per-type count/size summary for an HTTP-related type found on the heap.
/// </summary>
internal sealed record HttpObjectTypeSummary(
    string TypeName,
    int Count,
    ulong TotalBytes);

/// <summary>
/// Lightweight snapshot of a single HttpClient object, capped per scan.
/// </summary>
internal sealed record HttpClientSnapshot(
    string TypeName,
    ulong Address,
    string? BaseAddress = null,
    long TimeoutMilliseconds = -1);

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
    IReadOnlyList<HttpObjectTypeSummary> ByType,
    IReadOnlyList<HttpClientSnapshot> TopHttpClients,
    bool InstanceScanCapped) : DumpDetective.Core.Models.AnalyzerDomainResult;

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
    int LogicalTimerCount,
    int ThreadingTimerCount,
    int TimersTimerCount,
    int TimerQueueTimerCount,
    int TimerHolderCount,
    int PeriodicTimerCount,
    int OtherTimerCount,
    ulong TotalBytes,
    IReadOnlyList<TimerObjectTypeSummary> ByType) : DumpDetective.Core.Models.AnalyzerDomainResult;
