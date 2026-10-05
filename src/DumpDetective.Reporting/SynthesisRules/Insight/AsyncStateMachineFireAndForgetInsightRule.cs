using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectAsyncStateMachineFireAndForget</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// <c>AsyncStateMachineDomainResult.SuspendedMethodMap</c> groups by <c>(DeclaringType, Method)</c>
/// — coarser than <c>type.async-state-machine</c>'s own per-(state-machine-type, method) grouping.
/// No new observation was needed: <c>type.async-state-machine</c>'s second subject (a
/// <c>MethodRef</c>, added specifically so a consumer could reconstruct this exact grouping — see
/// its own remarks) is grouped by <c>MethodRef.JoinKey</c> here, summing <c>instance.count</c>
/// across every state-machine-type sharing the same declaring method.
/// </remarks>
internal sealed class AsyncStateMachineFireAndForgetInsightRule : ISynthesisRule
{
    private const int FireAndForgetThreshold = 100;

    public string RuleId => "insight.async-state-machine-fire-and-forget";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.async-state-machine", "type.async-state-machine-population", "heap.memory-composition"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null, memory = null;
        var perMethod = new List<Observation>();
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "type.async-state-machine": perMethod.Add(observation); break;
                case "type.async-state-machine-population": population = observation; break;
                case "heap.memory-composition": memory = observation; break;
            }
        }

        if (perMethod.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        // Group by (DeclaringType, Method) — the same key SuspendedMethodMap used.
        var byMethod = new Dictionary<string, (MethodRef Method, long SuspendedCount, List<ObservationId> Ids)>(StringComparer.Ordinal);
        foreach (Observation observation in perMethod)
        {
            var method = (MethodRef)observation.Subjects[1];
            long instanceCount = (long)observation.Measures["instance.count"].Value;

            if (byMethod.TryGetValue(method.JoinKey, out var existing))
                byMethod[method.JoinKey] = (existing.Method, existing.SuspendedCount + instanceCount, [.. existing.Ids, observation.Id]);
            else
                byMethod[method.JoinKey] = (method, instanceCount, [observation.Id]);
        }

        (MethodRef Method, long SuspendedCount, List<ObservationId> Ids)? worst = null;
        foreach (var entry in byMethod.Values)
        {
            if (worst is null || entry.SuspendedCount > worst.Value.SuspendedCount)
                worst = entry;
        }

        if (worst is null || worst.Value.SuspendedCount < FireAndForgetThreshold)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long totalStateMachines = (long)(population?.Measures["total.count"].Value ?? 0);
        ulong totalStateMachineBytes = (ulong)(population?.Measures["total.bytes"].Value ?? 0);

        string memNote = string.Empty;
        if (memory is not null)
        {
            double totalBytes = memory.Measures["total.bytes"].Value;
            if (totalBytes > 0)
            {
                double smPct = totalStateMachineBytes * 100.0 / totalBytes;
                if (smPct >= 1.0)
                    memNote = $" State machines account for {smPct:F1}% of total managed heap.";
            }
        }

        var derivedFrom = new List<ObservationId>(worst.Value.Ids);
        if (population is not null) derivedFrom.Add(population.Id);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::async-state-machine-fire-and-forget",
                Analyzer = "InsightEngine",
                Category = "Async",
                Severity = Severity.Warning,
                Title = "Likely fire-and-forget async accumulation detected",
                Narrative = $"{worst.Value.SuspendedCount:N0} suspended state machine instances for " +
                            $"'{worst.Value.Method.DeclaringType.CanonicalName}.{worst.Value.Method.Name}' — potential fire-and-forget pattern. " +
                            $"Total state machines: {totalStateMachines:N0} " +
                            $"({InsightByteFormat.FormatBytes(totalStateMachineBytes)}).{memNote}",
                Recommendation = "Ensure async methods are always awaited. " +
                                 "Use structured concurrency (Task.WhenAll / CancellationToken propagation) " +
                                 "to prevent unbounded accumulation of suspended state machines.",
                Tags = ["async", "state-machine", "fire-and-forget", "memory-leak"],
                MetricValue = worst.Value.SuspendedCount,
                MetricUnit = "instances",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
