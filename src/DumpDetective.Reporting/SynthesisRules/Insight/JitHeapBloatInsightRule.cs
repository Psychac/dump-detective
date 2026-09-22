using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectJitHeapBloat</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class JitHeapBloatInsightRule : ISynthesisRule
{
    private const ulong ThresholdBytes = 500UL * 1024 * 1024; // 500 MB

    public string RuleId => "insight.jit-heap-bloat";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.jit-activity", "runtime.thread-triage"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? jit = null, threadTriage = null;
        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "runtime.jit-activity": jit = observation; break;
                case "runtime.thread-triage": threadTriage = observation; break;
            }
        }

        if (jit is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        ulong totalJitHeapBytes = (ulong)jit.Measures["jit-heap.bytes"].Value;
        if (totalJitHeapBytes < ThresholdBytes)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        // The per-analyzer JitFindingGenerator already warns on this; this rule only fires a
        // cross-cutting finding when thread data correlates (many threads -> JIT contention).
        long aliveThreadCount = (long)(threadTriage?.Measures["alive-thread.count"].Value ?? 0);
        if (aliveThreadCount <= 100)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long jitManagerCount = (long)jit.Measures["jit-manager.count"].Value;

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::jit-heap-bloat",
                Analyzer = "InsightEngine",
                Category = "Performance",
                Severity = Severity.Warning,
                Title = "Large JIT heap combined with high thread count may indicate JIT warm-up contention",
                Narrative = $"JIT code heap: {InsightByteFormat.FormatBytes(totalJitHeapBytes)} across " +
                            $"{jitManagerCount} JIT manager(s). " +
                            $"Alive threads: {aliveThreadCount:N0}. " +
                            "Concurrent JIT compilation under heavy load can increase latency spikes.",
                Recommendation = "Use ReadyToRun (dotnet publish -r ...) or NativeAOT to pre-compile hot paths. " +
                                 "Profile startup with dotnet-trace to identify cold JIT paths.",
                Tags = ["jit", "threads", "performance", "warm-up"],
                MetricValue = (double)totalJitHeapBytes,
                MetricUnit = "bytes",
                DerivedFrom = [jit.Id, threadTriage!.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
