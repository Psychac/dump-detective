using DumpDetective.Sdk.Identity;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectDbConnectionLeak</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <c>DbConnectionDomainResult.ConnectionsFound</c> is derived here as <c>total.count > 0</c> — the
/// same "presence flag ~ population-count > 0" shape other whole-run observations already use
/// (e.g. <c>WcfChannelDomainResult.WcfPresent</c>).
/// </remarks>
internal sealed class DbConnectionLeakInsightRule : ISynthesisRule
{
    public string RuleId => "insight.db-connection-leak";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.db-connection-population", "runtime.exception-type"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? dbConn = null;
        var exceptionTypes = new List<Observation>();
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "infra.db-connection-population")
                dbConn = observation;
            else if (observation.ObservationType == "runtime.exception-type")
                exceptionTypes.Add(observation);
        }

        if (dbConn is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long totalConnections = (long)dbConn.Measures["total.count"].Value;
        if (totalConnections == 0 || totalConnections < 10)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long openConnections = (long)dbConn.Measures["open.count"].Value;
        if (openConnections < 10)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long timeoutCount = 0;
        var derivedFrom = new List<ObservationId> { dbConn.Id };
        foreach (Observation exceptionType in exceptionTypes)
        {
            string typeName = ((TypeRef)exceptionType.Subjects[0]).CanonicalName;
            if (!typeName.Contains("Timeout", StringComparison.OrdinalIgnoreCase) &&
                typeName != "System.InvalidOperationException") // "Timeout expired waiting for a pool"
                continue;

            timeoutCount += (long)exceptionType.Measures["instance.count"].Value;
            derivedFrom.Add(exceptionType.Id);
        }

        if (timeoutCount < 5)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::db-connection-leak",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(openConnections),
                    Qualifiers = new AssertionQualifiers { Location = "open DB connections", Mechanism = $"{timeoutCount:N0} timeout or invalid-operation exceptions on the heap" },
                },
                Support =
                [
                    new EvidenceStatement([dbConn.Id], $"{openConnections:N0} of {totalConnections:N0} DB connection objects are open."),
                    new EvidenceStatement(derivedFrom.GetRange(1, derivedFrom.Count - 1), $"{timeoutCount:N0} timeout or InvalidOperation exception objects are on the heap."),
                ],
                Counter = Counter.None("Timeout exceptions have many causes besides pool exhaustion, such as a slow query or an unreachable server, and InvalidOperationException is not specific to pools."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!do <exception address>",
                        Test = "On a sampled exception: if its message does not mention the connection pool, the timeouts are not pool exhaustion.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the connection string sets Max Pool Size above the open count, the pool has headroom and is not exhausted.",
                    },
                ],
                Analyzer = "InsightEngine",
                Category = "Infrastructure",
                Severity = Severity.Critical,
                Title = "DB connection pool exhaustion suspected",
                Narrative = $"{openConnections:N0} open connections on heap combined with " +
                            $"{timeoutCount:N0} timeout/InvalidOperation exception(s) strongly indicates " +
                            "connection pool exhaustion.",
                Recommendation = "Verify Max Pool Size in the connection string. " +
                                 "Ensure all SqlConnection objects are disposed (use 'using'). " +
                                 "Check for long-running transactions holding connections open. " +
                                 "Consider connection pool monitoring via Performance Counters.",
                Tags = ["infrastructure", "connections", "timeout", "pool-exhaustion"],
                MetricValue = openConnections,
                MetricUnit = "open connections",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
