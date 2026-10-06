using DumpDetective.Sdk.Identity;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectLongHeldTransactionOnOpenConnection</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// The held-reason this was originally deferred for — "needs checking whether the addresses are
/// even observable as stable identity, not assumed" — turned out to already be answered by the SDK:
/// <see cref="ObjectRef"/>'s identity is <c>(artifact, address)</c>, explicitly scoped to
/// within-artifact joins (never cross-artifact — see its own remarks). Both
/// <c>SqlTransactionAnalyzer</c> and <c>DbConnectionAnalyzer</c> always read the same dump in the
/// same run, so this is exactly the supported case, not the cross-dump case the SDK warns against.
/// Neither observation previously carried its own/referenced heap address at all —
/// <c>infra.sql-transaction</c> now carries a second <c>ObjectRef</c> subject (the referenced
/// connection's address, only present when read) alongside its existing <c>TypeRef</c>, and
/// <c>infra.db-connection</c> now carries a second <c>ObjectRef</c> subject (its own address) —
/// same multi-subject relation shape <c>lock.contested</c> already uses for type+owner.
/// </remarks>
internal sealed class LongHeldTransactionOnOpenConnectionInsightRule : ISynthesisRule
{
    private const int WarningCount = 3;
    private const int CriticalCount = 15;

    public string RuleId => "insight.long-held-transaction-on-open-connection";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.sql-transaction", "infra.db-connection"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        var openConnectionsByAddress = new Dictionary<ulong, ObservationId>();
        int openConnectionCount = 0;
        var transactions = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "infra.db-connection":
                    openConnectionCount++;
                    if (observation.Subjects.Count > 1 && observation.Subjects[1] is ObjectRef connRef)
                        openConnectionsByAddress[connRef.Address] = observation.Id;
                    break;
                case "infra.sql-transaction":
                    transactions.Add(observation);
                    break;
            }
        }

        if (transactions.Count == 0 || openConnectionCount == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        int correlatedCount = 0;
        var derivedFrom = new List<ObservationId>();
        foreach (Observation txn in transactions)
        {
            if (txn.Subjects.Count > 1 && txn.Subjects[1] is ObjectRef txnConnRef &&
                openConnectionsByAddress.TryGetValue(txnConnRef.Address, out ObservationId connObservationId))
            {
                correlatedCount++;
                derivedFrom.Add(txn.Id);
                derivedFrom.Add(connObservationId);
            }
        }

        if (correlatedCount < WarningCount)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        Severity severity = correlatedCount >= CriticalCount ? Severity.Critical : Severity.Warning;

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::long-held-transaction-on-open-connection",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Retains,
                    Measure = ClaimParts.Count(correlatedCount),
                    Reference = severity == Severity.Critical
                        ? ClaimParts.Budget(ClaimParts.Count(CriticalCount), "critical threshold")
                        : ClaimParts.Budget(ClaimParts.Count(WarningCount), "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "open DB connections referenced by a live transaction", Mechanism = "a connection cannot return to the pool until its transaction completes" },
                },
                Support = [new EvidenceStatement(derivedFrom, $"{correlatedCount:N0} of {openConnectionCount:N0} open connections are referenced by a transaction object.")],
                Counter = Counter.None("A transaction object on the heap may be in flight for a normal short request; the dump does not show how long it has been held."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!clrstack -all",
                        Test = "If each transaction is referenced from an in-flight request frame, the transactions are short-lived work, not held.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the same transactions are gone, they were in flight, not long-held.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Infrastructure",
                Severity = severity,
                Title = $"{correlatedCount:N0} open DB connections held by a live transaction",
                Narrative = $"{correlatedCount:N0} of {openConnectionCount:N0} open connections on heap are " +
                            "referenced by an Active SqlTransaction/IDbTransaction object — these connections " +
                            "cannot return to the pool until the transaction completes.",
                Recommendation = "Ensure transactions are committed or rolled back promptly, and disposed via " +
                                 "using statements, so their connections return to the pool. Long-held " +
                                 "transactions risk pool exhaustion under load.",
                Tags = ["sql", "transaction", "connection-pool", "pool-exhaustion", "cross-analyzer"],
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
