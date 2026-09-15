namespace DumpDetective.Analysis.Models;

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

