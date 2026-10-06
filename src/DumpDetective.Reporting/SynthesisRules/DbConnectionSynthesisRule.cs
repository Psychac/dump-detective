using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>DbConnectionFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>OpenConnections</c>/<c>Gen2OpenConnections</c> are reconstructed by counting/filtering
/// matched <c>infra.db-connection</c> observations rather than duplicated as measures — same
/// "don't duplicate what counting already gives you" choice earlier Infra rules made.
/// <c>Gen0OpenConnections</c> isn't reconstructed at all: no finding reads it. The root-path note
/// for the "largest Gen2 connection" is read from
/// <see cref="EvidenceRef.Description"/> the same way <c>SqlConnectionPoolSynthesisRule</c> reads
/// the anonymised connection-string label — no home among <c>Subjects</c>' typed <c>EntityRef</c>
/// kinds for either.
/// </remarks>
internal sealed class DbConnectionSynthesisRule : ISynthesisRule
{
    private const int TotalConnectionsWarningThreshold = 50;
    private const int TotalConnectionsCriticalThreshold = 200;
    private const int OpenConnectionsWarningThreshold = 20;
    private const int BrokenConnectionsWarningThreshold = 5;
    private const int Gen2OpenConnectionsThreshold = 5;
    private const int MaxTypeBreakdownEntries = 3;

    private const string AnalyzerName = "DB Connection Analysis";

    public string RuleId => "db-connection.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.db-connection", "infra.db-connection-type-population", "infra.db-connection-population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        long openConnections = 0;
        long gen2OpenConnections = 0;
        var openIds = new List<ObservationId>();
        var gen2Ids = new List<ObservationId>();
        Observation? bestGen2RootPathSource = null;
        ulong bestGen2RetainedBytes = 0;
        var typeCounts = new List<(string TypeName, long Count, ObservationId Id)>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "infra.db-connection":
                {
                    openConnections++;
                    openIds.Add(observation.Id);

                    int generation = (int)observation.Measures["generation.ordinal"].Value;
                    if (generation == 2)
                    {
                        gen2Ids.Add(observation.Id);
                        gen2OpenConnections++;

                        if (observation.Evidence.Count > 0 && observation.Evidence[0].Description is { Length: > 0 })
                        {
                            ulong retained = observation.Measures.TryGetValue("retained.bytes", out Measure retainedMeasure)
                                ? (ulong)retainedMeasure.Value
                                : 0;
                            if (bestGen2RootPathSource is null || retained > bestGen2RetainedBytes)
                            {
                                bestGen2RootPathSource = observation;
                                bestGen2RetainedBytes = retained;
                            }
                        }
                    }
                    break;
                }
                case "infra.db-connection-type-population":
                {
                    string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
                    long count = (long)observation.Measures["instance.count"].Value;
                    typeCounts.Add((typeName, count, observation.Id));
                    break;
                }
                case "infra.db-connection-population":
                    population = observation;
                    break;
            }
        }

        if (population is null) return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long totalConnections = (long)population.Measures["total.count"].Value;
        long closedConnections = (long)population.Measures["closed.count"].Value;
        long brokenConnections = (long)population.Measures["broken.count"].Value;
        long otherConnections = (long)population.Measures["other.count"].Value;
        long unknownStateConnections = (long)population.Measures["unknown-state.count"].Value;

        var findings = new List<Claim>(4);
        var populationIds = new List<ObservationId> { population.Id };

        // ── Connection count finding ─────────────────────────────────────────────
        if (totalConnections >= TotalConnectionsWarningThreshold)
        {
            Severity sev = totalConnections >= TotalConnectionsCriticalThreshold ? Severity.Critical : Severity.Warning;
            string typeBreakdown = BuildTypeBreakdown(typeCounts);

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("connection-count"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(totalConnections),
                    Reference = ClaimParts.Budget(
                        ClaimParts.Count(sev == Severity.Critical ? TotalConnectionsCriticalThreshold : TotalConnectionsWarningThreshold),
                        sev == Severity.Critical ? "critical threshold" : "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "DB connection objects on the managed heap" },
                },
                Support =
                [
                    new EvidenceStatement(populationIds, $"{totalConnections:N0} connection objects: {openConnections:N0} open, {closedConnections:N0} closed, {brokenConnections:N0} broken."),
                    .. typeCounts.Count > 0
                        ? [new EvidenceStatement(typeCounts.Select(t => t.Id).ToList(), "Breaks the connection objects down by type.")]
                        : Array.Empty<EvidenceStatement>(),
                ],
                Counter = closedConnections > openConnections
                    ? Counter.Of(new EvidenceStatement(populationIds, $"{closedConnections:N0} connections are closed, more than the {openConnections:N0} open, so most are not holding a pool slot."))
                    : Counter.None("Connection objects on the heap include ones awaiting collection; nothing observed shows how long they have lived."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -live -stat -type Connection",
                        Test = $"If far fewer than {totalConnections:N0} connection objects are live, the count includes garbage awaiting collection.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the connection string sets Max Pool Size above the reported count, the pool has headroom and this is not exhaustion.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = sev,
                Title = $"{totalConnections:N0} DB connection objects on managed heap",
                Narrative = $"Total: {totalConnections:N0} connection objects. " +
                            $"Open: {openConnections:N0}, Closed: {closedConnections:N0}, " +
                            $"Broken: {brokenConnections:N0}, " +
                            $"Other (connecting/executing): {otherConnections:N0}, " +
                            $"Unknown state: {unknownStateConnections:N0}. " +
                            $"Types: {typeBreakdown}.",
                Recommendation = "Connection objects on the heap after use indicate missing Dispose(). " +
                                  "Always wrap ADO.NET connections in a using statement or try/finally with Dispose(). " +
                                  "Check Max Pool Size in the connection string (default 100). " +
                                  "Excess open connections will exhaust the connection pool and cause timeouts.",
                Tags = ["infrastructure", "connections", "pool", "adonet"],
                MetricValue = totalConnections,
                MetricUnit = "connections",
                DerivedFrom = [.. populationIds, .. typeCounts.Select(t => t.Id)],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Open connections finding ─────────────────────────────────────────────
        if (openConnections >= OpenConnectionsWarningThreshold)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("open-connections"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Retains,
                    Measure = ClaimParts.Count(openConnections),
                    Reference = ClaimParts.Budget(ClaimParts.Count(OpenConnectionsWarningThreshold), "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "DB connections in the Open state" },
                },
                Support =
                [
                    new EvidenceStatement(openIds, "Each is a connection object in the Open state."),
                    new EvidenceStatement(populationIds, $"{openConnections:N0} of {totalConnections:N0} connection objects are open."),
                ],
                Counter = Counter.None("An open connection may be serving an in-flight request; state at capture time does not show it is idle."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!clrstack -all",
                        Test = "If each open connection is referenced from an in-flight request frame, the connections are in use, not leaked.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the open count falls back, the connections were in-flight work.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = Severity.Warning,
                Title = $"{openConnections:N0} DB connections in Open state",
                Narrative = $"{openConnections:N0} connections are in the Open state out of {totalConnections:N0} total. " +
                            "Open connections that are not actively used indicate leaks from missing Dispose() calls.",
                Recommendation = "Each open connection occupies a connection pool slot and a server-side socket. " +
                                  "Use 'using var conn = new SqlConnection(...)' pattern. " +
                                  "Consider connection pooling metrics: current pool size, wait time, and timeout rate.",
                Tags = ["infrastructure", "connections", "open", "leak"],
                MetricValue = openConnections,
                MetricUnit = "open connections",
                DerivedFrom = [.. openIds, .. populationIds],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Broken connections finding ───────────────────────────────────────────
        if (brokenConnections >= BrokenConnectionsWarningThreshold)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("broken-connections"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Faults,
                    Measure = ClaimParts.Count(brokenConnections),
                    Reference = ClaimParts.Budget(ClaimParts.Count(BrokenConnectionsWarningThreshold), "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "DB connections in the Broken state" },
                },
                Support = [new EvidenceStatement(populationIds, $"{brokenConnections:N0} of {totalConnections:N0} connection objects are broken.")],
                Counter = Counter.None("The pool discards a broken connection on its next use, so a few are expected after a network interruption."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump later; if the broken connections have been evicted and not replaced, they were from a one-off interruption.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the broken connections are caught by retry logic that reopens them, the failures are handled, not accumulating.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = Severity.Warning,
                Title = $"{brokenConnections:N0} DB connections in Broken state",
                Narrative = $"{brokenConnections:N0} connections are in the Broken state out of {totalConnections:N0} total. " +
                            "Broken connections indicate a failed attempt to use or communicate with the database server.",
                Recommendation = "Broken connections prevent pool recycling and can accumulate over time. " +
                                  "Investigate server-side failures (network issues, server restarts, timeout errors). " +
                                  "Ensure proper error handling and connection retry logic. " +
                                  "Monitor database server health and availability.",
                Tags = ["infrastructure", "connections", "broken", "pool"],
                MetricValue = brokenConnections,
                MetricUnit = "broken connections",
                DerivedFrom = populationIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Gen2 (long-lived) open connections finding ───────────────────────────
        if (gen2OpenConnections >= Gen2OpenConnectionsThreshold)
        {
            string? rootPathNote = bestGen2RootPathSource?.Evidence[0].Description;

            var derivedFrom = new List<ObservationId>(gen2Ids);
            if (bestGen2RootPathSource is not null) derivedFrom.Add(bestGen2RootPathSource.Id);

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("gen2-open-connections"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Leaks,
                    Measure = ClaimParts.Count(gen2OpenConnections),
                    Reference = ClaimParts.Budget(ClaimParts.Count(Gen2OpenConnectionsThreshold), "Gen2 open-connection threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "open DB connections in Generation 2", Mechanism = "long-lived objects" },
                },
                Support = [new EvidenceStatement(gen2Ids, $"{gen2OpenConnections:N0} open connections have survived into Generation 2.")],
                Counter = Counter.None("A connection held open on purpose, such as a dedicated listener, is long-lived without being leaked."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gcroot <connection address>",
                        Test = "On a sampled Gen2 connection: if its root is a deliberately long-lived owner such as a service singleton, the connection is held by design.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = $"Capture a second dump later; if the {gen2OpenConnections:N0} connections are the same ones and the count does not grow, they are a fixed set, not a leak.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = Severity.Critical,
                Title = $"{gen2OpenConnections:N0} Gen2 (long-lived) open DB connections",
                Narrative = $"{gen2OpenConnections:N0} open connections are in Generation 2 (long-lived objects). " +
                            "Gen2 objects are rarely collected, indicating these connections have been open for a long time and are likely leaked." +
                            (rootPathNote is null ? "" : $" Retention path for the largest one: {rootPathNote}"),
                Recommendation = "Gen2 open connections are strong evidence of connection pool leaks. " +
                                  "Review connection lifecycle: ensure all opened connections are properly closed in using blocks or try/finally. " +
                                  "Check for long-running operations that hold connections open unnecessarily. " +
                                  "Monitor for accumulated connection leaks in application restart cycles.",
                Tags = ["infrastructure", "connections", "leak", "gen2", "long-lived"],
                MetricValue = gen2OpenConnections,
                MetricUnit = "gen2 open connections",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    /// <summary>Trusts matched-observation order rather than re-sorting — the real analyzer's own
    /// <c>ByType</c> is already sorted descending by <c>TotalCount</c> before
    /// <c>EmitObservationsAsync</c>'s emission loop runs over it (same already-sorted-before-
    /// emission structure <c>ArraySynthesisRule</c> relies on), and the old generator's own
    /// <c>BuildTypeBreakdown</c> trusts that order too rather than re-ranking.</summary>
    private static string BuildTypeBreakdown(List<(string TypeName, long Count, ObservationId Id)> byType)
    {
        if (byType.Count == 0) return "(unknown)";

        var sb = new System.Text.StringBuilder();
        int shown = 0;
        for (int i = 0; i < byType.Count && shown < MaxTypeBreakdownEntries; i++)
        {
            if (shown > 0) sb.Append(", ");
            sb.Append($"{byType[i].TypeName.Split('.')[^1]} ×{byType[i].Count:N0}");
            shown++;
        }
        if (byType.Count > MaxTypeBreakdownEntries) sb.Append($" (+{byType.Count - MaxTypeBreakdownEntries} more)");
        return sb.ToString();
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
