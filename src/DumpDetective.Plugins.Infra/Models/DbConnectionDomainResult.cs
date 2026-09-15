namespace DumpDetective.Analysis.Models;

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

