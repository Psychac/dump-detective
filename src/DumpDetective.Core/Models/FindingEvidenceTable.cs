namespace DumpDetective.Core.Models;

/// <summary>
/// Structured tabular evidence a finding can attach alongside its prose <c>Evidence</c> string.
/// Producers (any <c>IFindingGenerator</c> or <see cref="InsightFinding"/>-emitting correlation)
/// own the row data; renderers (e.g. the Reporting layer) only map it into their own table
/// representation. Kept in Core so cross-analyzer correlations (which live in the Analysis layer,
/// below Reporting) can attach one without a layering violation.
/// </summary>
public sealed record FindingEvidenceTable(
    string Title,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<object?>> Rows);
