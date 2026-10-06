using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>SqlConnectionPoolFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// No new observation type or measure needed — <c>infra.sql-connection-pool</c> already backs
/// every numeric field the old generator reads (<c>current.size</c>/<c>max.size</c> reconstruct
/// utilisation exactly the way <c>SqlConnectionPoolAnalyzer.UtilizationPercent</c> does; pool
/// count and near-capacity count are both reconstructed by counting/filtering the matched
/// observations). The one gap — the anonymised connection string label the "worst pool" narrative
/// needs — was carried as <see cref="EvidenceRef.Description"/> on the observation itself (Phase 5
/// step 2), not a new measure: it identifies a specific pool instance's target server/database, not
/// a fact with a natural home among <c>Subjects</c>' typed <c>EntityRef</c> kinds.
/// </remarks>
internal sealed class SqlConnectionPoolSynthesisRule : ISynthesisRule
{
    private const double NearCapacityUtilizationPct = 80.0;
    private const double CriticalUtilizationPct = 95.0;

    private const string AnalyzerName = "SQL Connection Pool Analysis";

    public string RuleId => "sql-connection-pool.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.sql-connection-pool"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        int totalPools = 0;
        int poolsNearCapacity = 0;
        Observation? worst = null;
        double worstPct = -1;

        foreach (Observation observation in matched.Matched)
        {
            totalPools++;

            double pct = UtilizationPercent(observation);
            if (pct >= NearCapacityUtilizationPct) poolsNearCapacity++;
            if (pct > worstPct)
            {
                worstPct = pct;
                worst = observation;
            }
        }

        if (totalPools == 0 || poolsNearCapacity == 0 || worst is null || worstPct < 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long currentSize = (long)worst.Measures["current.size"].Value;
        long maxPoolSize = worst.Measures.TryGetValue("max.size", out Measure maxMeasure) ? (long)maxMeasure.Value : -1;
        string poolLabel = worst.Evidence.Count > 0 && worst.Evidence[0].Description is { Length: > 0 } description
            ? description
            : "(unknown server/database)";

        Severity sev = worstPct >= CriticalUtilizationPct ? Severity.Critical : Severity.Warning;

        var finding = new Claim
        {
            Fingerprint = Fingerprint("near-capacity"),
            Analyzer = AnalyzerName,
            Category = "Infrastructure",
            Severity = sev,
            Title = $"{poolsNearCapacity:N0} SQL connection pool(s) near capacity",
            Narrative = $"Highest utilisation pool is at {worstPct:F1}% ({currentSize:N0}/{maxPoolSize:N0} " +
                        $"connections) — {poolLabel}. {poolsNearCapacity:N0} of {totalPools:N0} discovered " +
                        "pools are at or above 80% of Max Pool Size.",
            Recommendation = "This is read directly from the connection pool manager's own counters, not estimated. " +
                              "Increase Max Pool Size in the connection string if legitimate concurrent demand requires " +
                              "it, or investigate connection leaks (missing Dispose()) feeding this specific pool.",
            Tags = ["infrastructure", "connections", "pool", "pool-exhaustion"],
            MetricValue = worstPct,
            MetricUnit = "% utilised",
            DerivedFrom = [worst.Id],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
            Assertion = BuildAssertion(sev, worstPct, poolLabel),
            Support = [new EvidenceStatement([worst.Id], $"The most utilised pool has {currentSize:N0} of {maxPoolSize:N0} connections in use.")],
            Counter = Counter.None("Utilisation is a point-in-time read of the pool counters; nothing observed shows whether it is transient."),
            FalsifiedBy = BuildFalsifiedBy(worstPct, maxPoolSize),
        };

        return ValueTask.FromResult<IReadOnlyList<Claim>>([finding]);
    }

    /// <summary>Mirrors <c>SqlConnectionPoolAnalyzer.UtilizationPercent</c> exactly: -1 (never "the
    /// worst") when <c>max.size</c> wasn't observed at all — the analyzer omits that measure key
    /// entirely rather than emitting a zero when <c>MaxPoolSize</c> couldn't be read.</summary>
    private static double UtilizationPercent(Observation pool) =>
        pool.Measures.TryGetValue("max.size", out Measure maxMeasure) && maxMeasure.Value > 0
            ? 100.0 * pool.Measures["current.size"].Value / maxMeasure.Value
            : -1;

    private static Assertion BuildAssertion(Severity severity, double worstPercent, string poolLabel)
    {
        bool critical = severity == Severity.Critical;
        return new Assertion
        {
            Predicate = AssertionPredicate.Exceeds,
            Measure = ClaimParts.Percent(worstPercent),
            Reference = ClaimParts.Budget(
                ClaimParts.Percent(critical ? CriticalUtilizationPct : NearCapacityUtilizationPct),
                critical ? "critical utilisation threshold" : "near-capacity threshold"),
            Qualifiers = new AssertionQualifiers { Location = $"pool {poolLabel}" },
        };
    }

    private static IReadOnlyList<FalsificationTest> BuildFalsifiedBy(double worstPercent, long maxPoolSize) =>
    [
        new FalsificationTest
        {
            Kind = FalsificationKind.Capture,
            Test = $"Capture a second dump under comparable load; if this pool falls below {NearCapacityUtilizationPct:F0}% from {worstPercent:F1}%, the reading was a spike, not sustained saturation.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.CodeInspection,
            Test = $"If the connection string for this pool sets Max Pool Size to anything other than {maxPoolSize:N0}, the utilisation percentage is wrong.",
        },
    ];

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
