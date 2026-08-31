using DumpDetective.Core.Enums;

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
    sbyte Generation = -1,
    /// <summary>Exact dominator-tree retained bytes for this connection (§9,
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md). Null when the exact
    /// tree wasn't available.</summary>
    ulong? RetainedBytes = null,
    /// <summary>R12 (docs/analysis/phase1/DbConnectionAnalyzer-audit.md): formatted GC root
    /// retention path ("<c>RootKind: Type@0xAddr -> ... -> Type@0xAddr</c>"), matching WinDbg/SOS's
    /// <c>!gcroot</c> workflow. Only computed for a bounded subset of Gen2 (long-lived, likely
    /// leaked) open connections — see <c>DbConnectionAnalyzer.MaxRootPathEnrichment</c>. Null when
    /// not attempted (non-Gen2, outside the enrichment cap, or no reachable graph index available)
    /// or when no path was found.</summary>
    string? RootPath = null,
    /// <summary>True when the root-path search for this connection hit a search-space limit before
    /// concluding no path exists — a null <see cref="RootPath"/> alongside <c>true</c> here means
    /// "inconclusive", not "unreachable".</summary>
    bool RootPathSearchTruncated = false);

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
    IReadOnlyList<PoolSummary> TopPools) : DumpDetective.Core.Models.AnalyzerDomainResult;

// ── SQL Connection Pool (runtime pool-manager objects) ─────────────────────────

/// <summary>
/// Snapshot of a single ADO.NET connection-pool manager object
/// (<c>System.Data.ProviderBase.DbConnectionPool</c> / <c>Microsoft.Data.ProviderBase.DbConnectionPool</c>
/// — shared by both the legacy and current SqlClient packages; there is no per-provider pool
/// subclass to match by name). <see cref="CurrentSize"/>/<see cref="MaxPoolSize"/> come directly
/// from the pool's live internal counters, not from sampling connection objects, so this is exact
/// pool-utilisation evidence rather than an estimate.
/// </summary>
internal sealed record SqlConnectionPoolSnapshot(
    string TypeName,
    ulong Address,
    int CurrentSize,
    int MaxPoolSize,
    int MinPoolSize,
    string? AnonymisedConnectionString);

/// <summary>
/// Domain result produced by <c>SqlConnectionPoolAnalyzer</c>. Reports exact current-size vs.
/// max-pool-size for each discovered SqlClient connection-pool manager object on the heap.
/// </summary>
internal sealed record SqlConnectionPoolDomainResult(
    bool PoolsFound,
    int TotalPools,
    int PoolsNearCapacity,
    IReadOnlyList<SqlConnectionPoolSnapshot> Pools) : DumpDetective.Core.Models.AnalyzerDomainResult;

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
/// Lightweight snapshot of a single SQL transaction object. <see cref="ConnectionAddress"/> is
/// the address of the owning connection object (read from the transaction's internal
/// <c>_connection</c> field) while the transaction is Active; used to correlate long-held
/// transactions against <c>DbConnectionDomainResult.TopOpenConnections</c>.
/// </summary>
internal sealed record SqlTransactionSnapshot(
    string TypeName,
    ulong Address,
    string StateLabel,
    int StateValue,
    ulong? ConnectionAddress = null);

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
    IReadOnlyList<SqlTransactionSnapshot> TopActiveTransactions) : DumpDetective.Core.Models.AnalyzerDomainResult;

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
/// Lightweight snapshot of a single SQL command object. <see cref="StateValue"/>/<see cref="StateLabel"/>
/// reflect whether the command's internal connection-reference field is still non-null — ADO.NET
/// providers do not reliably null this out on <c>Dispose()</c>, so "Active" means "still wired to a
/// connection object" rather than strictly "not yet disposed".
/// </summary>
internal sealed record SqlCommandSnapshot(
    string TypeName,
    ulong Address,
    string StateLabel,
    int StateValue);

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
    IReadOnlyList<SqlCommandSnapshot> TopActiveCommands) : DumpDetective.Core.Models.AnalyzerDomainResult;

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
    ulong TotalBytes,
    WcfBindingHint BindingHint = WcfBindingHint.Unknown,
    /// <summary>Count of channels whose read state value fell outside the valid
    /// CommunicationState range (0-5) — a field-probe mismatch or memory corruption signal,
    /// kept separate from <see cref="OtherCount"/> rather than silently folded into it.</summary>
    int InvalidStateCount = 0);

/// <summary>
/// Lightweight snapshot of a single WCF channel object.
/// </summary>
internal sealed record WcfChannelSnapshot(
    string TypeName,
    ulong Address,
    string StateLabel,
    int StateValue,
    string? RemoteAddress = null,
    /// <summary>Exact dominator-tree retained bytes for this channel (§9,
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md). Null when the exact
    /// tree wasn't available.</summary>
    ulong? RetainedBytes = null);

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
    int FactoryCount = 0,
    ulong TotalBytes = 0,
    /// <summary>Aggregate of <see cref="WcfChannelTypeSummary.InvalidStateCount"/> across
    /// <see cref="ByType"/>.</summary>
    int InvalidStateCount = 0,
    /// <summary>Total channel instances whose type name identifies a duplex-shaped channel
    /// (e.g. ClientFramingDuplexSessionChannel). A channel can be both duplex and session-based;
    /// this and <see cref="SessionChannelCount"/> are independent, overlapping classifications
    /// of <see cref="TotalChannels"/>, not a separate partition.</summary>
    int DuplexChannelCount = 0,
    /// <summary>Total channel instances whose type name identifies a session-based channel.</summary>
    int SessionChannelCount = 0) : DumpDetective.Core.Models.AnalyzerDomainResult;

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
    IReadOnlyList<HttpClientSnapshot> TopHttpClients) : DumpDetective.Core.Models.AnalyzerDomainResult;

// ── Timer Objects ───────────────────────────────────────────────────────────

/// <summary>
/// Sampled state from a single timer instance (period, callback owner).
/// </summary>
internal sealed record TimerStateSnapshot(
    ulong Address,
    DumpDetective.Core.Enums.GenerationTag Generation,
    long PeriodMs,
    string? CallbackOwnerType);

/// <summary>
/// Per-type count/size summary for timer-related objects found on the heap.
/// </summary>
internal sealed record TimerObjectTypeSummary(
    string TypeName,
    int Count,
    ulong TotalBytes,
    Evidence? Evidence = null,
    IReadOnlyList<TimerStateSnapshot>? Samples = null);

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
    IReadOnlyList<TimerObjectTypeSummary> ByType,
    IReadOnlyList<(string Bucket, int Count)> IntervalHistogram) : DumpDetective.Core.Models.AnalyzerDomainResult;
