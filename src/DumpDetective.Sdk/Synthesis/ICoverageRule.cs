using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// Reads observations and states how far an analysis got, as <see cref="CoverageRecord"/>s. Separate from
/// <see cref="ISynthesisRule"/> because a limit on an analysis is not a claim about the artifact: the status
/// follows the facts (any limit hit is <see cref="CoverageStatus.Partial"/>) and no threshold is applied to it
/// (docs/ReportStructure/m3-coverage-plan.md D2).
/// </summary>
public interface ICoverageRule
{
    string RuleId { get; }

    int Version { get; }

    ObservationQuery Query { get; }

    /// <summary>Records for the limits found; empty when the analysis ran to completion. The engine stamps <see cref="CoverageRecord.Rule"/>.</summary>
    IReadOnlyList<CoverageRecord> Evaluate(ObservationMatchSet matched, SynthesisContext context);
}
