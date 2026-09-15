namespace DumpDetective.Analysis.Models;

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

