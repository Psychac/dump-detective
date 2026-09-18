using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>SqlCommandFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>ActiveCount</c> is reconstructed by counting matched <c>infra.sql-command</c> observations
/// (one per active command) rather than duplicated as a measure on the new
/// <c>infra.sql-command-population</c> observation — same "don't duplicate what counting already
/// gives you" choice <c>AsyncTaskSynthesisRule</c>/<c>SqlConnectionPoolSynthesisRule</c> made.
/// </remarks>
internal sealed class SqlCommandSynthesisRule : ISynthesisRule
{
    private const int ActiveCountWarningThreshold = 100;
    private const int ActiveCountCriticalThreshold = 1_000;

    private const string AnalyzerName = "SQL Command Analysis";

    public string RuleId => "sql-command.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.sql-command", "infra.sql-command-population"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        long activeCount = 0;
        var activeIds = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "infra.sql-command")
            {
                activeCount++;
                activeIds.Add(observation.Id);
            }
            else if (observation.ObservationType == "infra.sql-command-population")
            {
                population = observation;
            }
        }

        if (population is null || activeCount < ActiveCountWarningThreshold)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long totalCommands = (long)population.Measures["total.count"].Value;
        long disposedCount = (long)population.Measures["disposed.count"].Value;

        Severity severity = activeCount >= ActiveCountCriticalThreshold ? Severity.Critical : Severity.Warning;

        var derivedFrom = new List<ObservationId>(activeIds) { population.Id };

        var finding = new Finding
        {
            Fingerprint = Fingerprint("outstanding-commands"),
            Analyzer = AnalyzerName,
            Category = "Infrastructure",
            Severity = severity,
            Title = $"{activeCount:N0} SQL command objects still reference a connection",
            Narrative = $"{activeCount:N0} of {totalCommands:N0} command objects on the heap still " +
                        $"reference a connection object. Detached (no connection reference): {disposedCount:N0}.",
            Recommendation = "A large number of command objects wired to connections usually indicates commands are " +
                              "being cached or held onto by a long-lived owner rather than created per-call and disposed. " +
                              "Review command lifetime: prefer creating SqlCommand/IDbCommand instances scoped to a " +
                              "single execution and disposing them, or explicitly clear parameters/connection references " +
                              "when reusing a command instance across calls.",
            Tags = ["infrastructure", "connections", "command", "adonet"],
            MetricValue = activeCount,
            MetricUnit = "outstanding commands",
            DerivedFrom = derivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        };

        return ValueTask.FromResult<IReadOnlyList<Finding>>([finding]);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
