using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>SqlTransactionFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Same shape as <c>SqlCommandSynthesisRule</c>: <c>ActiveCount</c> is reconstructed by counting
/// matched <c>infra.sql-transaction</c> observations rather than duplicated as a measure on the
/// new <c>infra.sql-transaction-population</c> observation.
/// </remarks>
internal sealed class SqlTransactionSynthesisRule : ISynthesisRule
{
    private const int ActiveCountWarningThreshold = 5;
    private const int ActiveCountCriticalThreshold = 25;

    private const string AnalyzerName = "SQL Transaction Analysis";

    public string RuleId => "sql-transaction.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.sql-transaction", "infra.sql-transaction-population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        long activeCount = 0;
        var activeIds = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "infra.sql-transaction")
            {
                activeCount++;
                activeIds.Add(observation.Id);
            }
            else if (observation.ObservationType == "infra.sql-transaction-population")
            {
                population = observation;
            }
        }

        if (population is null || activeCount < ActiveCountWarningThreshold)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long totalTransactions = (long)population.Measures["total.count"].Value;
        long disposedCount = (long)population.Measures["disposed.count"].Value;
        long otherCount = (long)population.Measures["other.count"].Value;

        Severity severity = activeCount >= ActiveCountCriticalThreshold ? Severity.Critical : Severity.Warning;

        var derivedFrom = new List<ObservationId>(activeIds) { population.Id };

        var finding = new Claim
        {
            Fingerprint = Fingerprint("active-transactions"),
            Analyzer = AnalyzerName,
            Category = "Infrastructure",
            Severity = severity,
            Title = $"{activeCount:N0} SQL transaction objects still hold an open connection",
            Narrative = $"{activeCount:N0} of {totalTransactions:N0} transaction objects on the heap still " +
                        "reference an owning connection (not yet Committed/Rolled back/Disposed). " +
                        $"Disposed: {disposedCount:N0}, Other: {otherCount:N0}.",
            Recommendation = "A transaction object that is still on the heap and references its connection prevents that " +
                              "connection from returning to the pool, even if the connection object itself looks idle. " +
                              "Always wrap SqlTransaction/IDbTransaction in a using statement, and Commit or Rollback as " +
                              "soon as the unit of work completes — do not hold transactions open across unrelated calls " +
                              "or await points.",
            Tags = ["infrastructure", "connections", "transaction", "pool"],
            MetricValue = activeCount,
            MetricUnit = "active transactions",
            DerivedFrom = derivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
            Assertion = BuildAssertion(severity, activeCount),
            Support =
            [
                new EvidenceStatement(activeIds, "Each is a transaction object that still references an owning connection."),
                new EvidenceStatement([population.Id], "Supplies the total, disposed and other transaction counts."),
            ],
            Counter = BuildCounter(population, activeCount, disposedCount + otherCount),
            FalsifiedBy = BuildFalsifiedBy(totalTransactions),
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
            Qualifiers = new AssertionQualifiers { Location = "SQL transaction objects still holding an open connection" },
        };
    }

    private static Counter BuildCounter(Observation population, long activeCount, long notHoldingCount) =>
        notHoldingCount > activeCount
            ? Counter.Of(new EvidenceStatement(
                [population.Id],
                $"{notHoldingCount:N0} transaction objects are not holding a connection, more than the {activeCount:N0} that are, so holding one is not the norm."))
            : Counter.None("Nothing observed separates transactions still in flight from transactions abandoned; only the reference to a connection is visible.");

    private static IReadOnlyList<FalsificationTest> BuildFalsifiedBy(long totalTransactions) =>
    [
        new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!dumpheap -live -stat -type SqlTransaction",
            Test = $"If far fewer than the reported {totalTransactions:N0} transaction objects are live, the count includes garbage awaiting collection, not open transactions.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!gcroot <transaction address>",
            Test = "On a sampled transaction: if its root is an in-flight request frame, it is a transaction legitimately in progress, not an abandoned one.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.CodeInspection,
            Test = "If every code path commits or rolls back inside a using block, remaining instances are in-flight work, not leaks.",
        },
    ];

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
