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

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
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
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long totalCommands = (long)population.Measures["total.count"].Value;
        long disposedCount = (long)population.Measures["disposed.count"].Value;

        Severity severity = activeCount >= ActiveCountCriticalThreshold ? Severity.Critical : Severity.Warning;

        var derivedFrom = new List<ObservationId>(activeIds) { population.Id };

        var finding = new Claim
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
            Assertion = BuildAssertion(severity, activeCount),
            Support =
            [
                new EvidenceStatement(activeIds, "Each is a command object on the heap that still references a connection."),
                new EvidenceStatement([population.Id], "Supplies the total and detached command counts."),
            ],
            Counter = BuildCounter(population, activeCount, disposedCount),
            FalsifiedBy = BuildFalsifiedBy(totalCommands),
        };

        return ValueTask.FromResult<IReadOnlyList<Claim>>([finding]);
    }

    private static Assertion BuildAssertion(Severity severity, long activeCount)
    {
        bool critical = severity == Severity.Critical;
        return new Assertion
        {
            Predicate = AssertionPredicate.Retains,
            Measure = ClaimParts.Count(activeCount),
            Reference = ClaimParts.Budget(
                ClaimParts.Count(critical ? ActiveCountCriticalThreshold : ActiveCountWarningThreshold),
                critical ? "critical threshold" : "warning threshold"),
            Qualifiers = new AssertionQualifiers { Location = "SQL command objects still referencing a connection" },
        };
    }

    private static Counter BuildCounter(Observation population, long activeCount, long disposedCount) =>
        disposedCount > activeCount
            ? Counter.Of(new EvidenceStatement(
                [population.Id],
                $"{disposedCount:N0} command objects are already detached from their connection, more than the {activeCount:N0} still attached, so holding a connection is not the norm."))
            : Counter.None("Nothing observed separates commands cached on purpose from commands leaked; only the reference to a connection is visible.");

    private static IReadOnlyList<FalsificationTest> BuildFalsifiedBy(long totalCommands) =>
    [
        new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!dumpheap -live -stat -type SqlCommand",
            Test = $"If far fewer than the reported {totalCommands:N0} command objects are live, the count includes garbage awaiting collection, not retained commands.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!gcroot <command address>",
            Test = "On a sampled command: if its root is a short-lived request frame, not a long-lived owner such as a cache or static field, the commands are not being held.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.CodeInspection,
            Test = "If the owning code deliberately caches prepared commands, the retention is by design, not a leak.",
        },
    ];

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
