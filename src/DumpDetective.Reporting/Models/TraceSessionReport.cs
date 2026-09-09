using DumpDetective.Sdk.Observations;

namespace DumpDetective.Reporting.Models;

/// <summary>
/// The trace-only <c>report.json</c> shape — deliberately not the full session-report schema v3
/// docs/refactor/modularity/phase-8-sinks-and-ui.md describes (<c>sources[]</c>, <c>timeline</c>,
/// <c>capabilityReport</c>, findings with <c>ConfidenceBreakdown</c>). That schema needs a real
/// session/artifact model (Phase 4) and a synthesis engine (Phase 5), neither of which exist under
/// the § 8 minimum-viable path this report ships under. This is the proportionate slice of
/// "report.json unconditional... so a UI has a contract" (§ 8 step 6) for what actually exists
/// today: one trace artifact's raw observations.
/// </summary>
/// <remarks>
/// Lives here, not in <c>DumpDetective.Cli</c>, for the same reason <see cref="AnalysisReportDocument"/>
/// does — this project is the one that owns report-shape contracts; <c>DumpDetective.Cli</c> owns
/// deciding where a report ends up on disk and writing it (<c>TraceReportWriter</c>, the same split
/// <c>ReportOutputWriter</c> already follows for the dump side).
/// </remarks>
internal sealed record TraceSessionReport(
    string TracePath,
    DateTime GeneratedAtUtc,
    IReadOnlyList<Observation> Observations);
