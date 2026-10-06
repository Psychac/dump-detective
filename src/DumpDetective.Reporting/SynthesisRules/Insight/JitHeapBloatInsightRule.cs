using DumpDetective.Reporting.SynthesisRules;
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

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
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
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        ulong totalJitHeapBytes = (ulong)jit.Measures["jit-heap.bytes"].Value;
        if (totalJitHeapBytes < ThresholdBytes)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        // The per-analyzer JitFindingGenerator already warns on this; this rule only fires a
        // cross-cutting finding when thread data correlates (many threads -> JIT contention).
        long aliveThreadCount = (long)(threadTriage?.Measures["alive-thread.count"].Value ?? 0);
        if (aliveThreadCount <= 100)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long jitManagerCount = (long)jit.Measures["jit-manager.count"].Value;

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::jit-heap-bloat",
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Bytes(totalJitHeapBytes),
                    Reference = ClaimParts.Budget(ClaimParts.Bytes(ThresholdBytes), "JIT heap threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "the JIT code heap", Mechanism = $"{aliveThreadCount:N0} alive threads compiling concurrently" },
                },
                Support =
                [
                    new EvidenceStatement([jit.Id], $"The JIT code heap holds {InsightByteFormat.FormatBytes(totalJitHeapBytes)} across {jitManagerCount} JIT manager(s)."),
                    new EvidenceStatement([threadTriage!.Id], $"{aliveThreadCount:N0} threads are alive, enough for concurrent compilation to contend."),
                ],
                Counter = Counter.None("A large JIT heap and many threads are both normal for a large busy service; their co-occurrence does not show compilation contention."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -loader",
                        Test = $"If the code heap sizes in !eeheap -loader total far less than {InsightByteFormat.FormatBytes(totalJitHeapBytes)}, the JIT footprint is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump after warm-up; if latency is flat and the JIT heap has stopped growing, there is no warm-up contention.",
                    },
                ],
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
