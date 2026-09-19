using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>JitFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Emits signal findings in the same order the old generator's own if-checks run (JIT heap bloat,
/// unmanaged frame ratio, tiered compilation, large methods, dynamic codegen, deep stack) — despite
/// a comment on the old generator claiming they're "sorted by severity descending, then priority
/// descending", the actual code never sorts <c>signals</c> before appending; that comment is stale,
/// not a behavior this migration should introduce. <c>TopLargestMethods</c> is not observed
/// per-entry (no <c>MethodRef</c> can be built from a pre-formatted signature string without regex-
/// drift risk, per <c>JitAnalyzer</c>'s own remarks) — only the single top method's sizes and an
/// evidence-carried signature back the large-methods finding.
/// </remarks>
internal sealed class JitSynthesisRule : ISynthesisRule
{
    private const ulong JitHeapBloatThreshold = 500 * 1024 * 1024;
    private const double HighUnmanagedFrameRatio = 0.30;
    private const int DeepStackFrameThreshold = 500;

    private const string AnalyzerName = "JIT Analysis";

    public string RuleId => "jit.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.jit-activity"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? activity = null;
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "runtime.jit-activity")
            {
                activity = observation;
                break;
            }
        }

        if (activity is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        ulong totalJitHeapBytes = (ulong)activity.Measures["jit-heap.bytes"].Value;
        long jitManagerCount = (long)activity.Measures["jit-manager.count"].Value;
        long activeMethodsOnStacks = (long)activity.Measures["active-methods.count"].Value;
        long tieredMethodCount = (long)activity.Measures["tiered-methods.count"].Value;
        long managedFrameCount = (long)activity.Measures["managed-frame.count"].Value;
        long unmanagedFrameCount = (long)activity.Measures["unmanaged-frame.count"].Value;
        long dynamicMethodFrameCount = (long)activity.Measures["dynamic-method-frame.count"].Value;
        long largeMethodCount = (long)activity.Measures["large-method.count"].Value;
        ulong largeMethodThresholdBytes = (ulong)activity.Measures["large-method.threshold-bytes"].Value;
        long maxThreadFrameDepth = (long)activity.Measures["max-thread-frame-depth.count"].Value;
        uint maxThreadFrameDepthOSThreadId = (uint)activity.Measures["max-thread-frame-depth.os-thread-id"].Value;

        var signals = new List<Finding>(6);

        // JIT heap bloat.
        if (totalJitHeapBytes > JitHeapBloatThreshold)
        {
            signals.Add(new Finding
            {
                Fingerprint = Fingerprint("heap-bloat"),
                Analyzer = AnalyzerName,
                Category = "Performance",
                Severity = Severity.Warning,
                Title = "JIT code heap unusually large",
                Narrative = $"JIT code heap totals {FormatHelper.FormatBytes(totalJitHeapBytes)} " +
                            $"across {jitManagerCount} JIT manager(s). " +
                            "This is above the 500 MB warning threshold.",
                Recommendation = "Profile with dotnet-trace / PerfView to identify hot code paths. " +
                                  "Consider ReadyToRun publication or NativeAOT to reduce JIT footprint.",
                Tags = ["jit", "native-heap", "performance"],
                MetricValue = totalJitHeapBytes,
                MetricUnit = "bytes",
                DerivedFrom = [activity.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // High unmanaged frame ratio.
        long totalFrames = managedFrameCount + unmanagedFrameCount;
        if (totalFrames > 0)
        {
            double unmanagedRatio = (double)unmanagedFrameCount / totalFrames;
            if (unmanagedRatio >= HighUnmanagedFrameRatio && unmanagedFrameCount > 50)
            {
                signals.Add(new Finding
                {
                    Fingerprint = Fingerprint("unmanaged-frame-ratio"),
                    Analyzer = AnalyzerName,
                    Category = "Performance",
                    Severity = Severity.Info,
                    Title = "High unmanaged/runtime frame ratio on thread stacks",
                    Narrative = $"{unmanagedFrameCount:N0} runtime/internal frames out of " +
                                $"{totalFrames:N0} total ({unmanagedRatio:P0}). " +
                                "Indicates significant P/Invoke, COM interop, or CLR internal activity.",
                    Recommendation = "Review P/Invoke usage patterns. Batch interop calls and " +
                                      "prefer Span<T>/Memory<T> over per-element marshalling.",
                    Tags = ["jit", "interop", "unmanaged"],
                    MetricValue = unmanagedRatio,
                    MetricUnit = "ratio",
                    DerivedFrom = [activity.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        // Tiered compilation.
        if (tieredMethodCount > 0)
        {
            signals.Add(new Finding
            {
                Fingerprint = Fingerprint("tiered-compilation"),
                Analyzer = AnalyzerName,
                Category = "Performance",
                Severity = Severity.Info,
                Title = "Tiered compilation activity detected",
                Narrative = $"~{tieredMethodCount:N0} method(s) (estimate, stack-visible methods only) " +
                            "observed with multiple native code addresses for the same method, " +
                            "indicating tiered recompilation (Tier0 → Tier1). This is expected behaviour.",
                Recommendation = "If startup-time JIT overhead is a concern, " +
                                  "consider ReadyToRun images (dotnet publish -r ... --self-contained).",
                Tags = ["jit", "tiered-compilation"],
                MetricValue = tieredMethodCount,
                MetricUnit = "methods",
                DerivedFrom = [activity.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // Large methods.
        if (largeMethodCount > 0 && activity.Evidence.Count > 0)
        {
            string topSignature = activity.Evidence[0].Description ?? "";
            ulong topHotSize = activity.Measures.TryGetValue("top-method.hot-size-bytes", out Measure hm) ? (ulong)hm.Value : 0;
            ulong topColdSize = activity.Measures.TryGetValue("top-method.cold-size-bytes", out Measure cm) ? (ulong)cm.Value : 0;
            ulong topSize = topHotSize + topColdSize;
            string thresholdDisplay = FormatHelper.FormatBytes(largeMethodThresholdBytes);

            signals.Add(new Finding
            {
                Fingerprint = Fingerprint("large-methods"),
                Analyzer = AnalyzerName,
                Category = "Performance",
                Severity = Severity.Info,
                Title = "Large JIT-compiled methods detected on thread stacks",
                Narrative = $"Largest method on stacks: '{topSignature}' " +
                            $"({FormatHelper.FormatBytes(topSize)} native code). " +
                            $"{largeMethodCount} method(s) exceed the {thresholdDisplay} threshold.",
                Recommendation = $"Refactor methods over {thresholdDisplay} native code — they prevent " +
                                  "inlining and stress the JIT register allocator.",
                Tags = ["jit", "code-size", "performance"],
                MetricValue = topSize,
                MetricUnit = "bytes",
                DerivedFrom = [activity.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // Dynamic codegen.
        if (dynamicMethodFrameCount > 0)
        {
            signals.Add(new Finding
            {
                Fingerprint = Fingerprint("dynamic-codegen"),
                Analyzer = AnalyzerName,
                Category = "Performance",
                Severity = Severity.Info,
                Title = "Dynamic codegen frames detected on thread stacks",
                Narrative = $"{dynamicMethodFrameCount:N0} active frame(s) resolve to a dynamic module " +
                            "(DynamicMethod, AssemblyBuilder-emitted types, or compiled LINQ expression trees).",
                Recommendation = "If the same dynamic method/expression is being generated and compiled " +
                                  "repeatedly rather than cached, this is a common source of unbounded JIT " +
                                  "code-heap growth.",
                Tags = ["jit", "dynamic-codegen", "reflection-emit"],
                MetricValue = dynamicMethodFrameCount,
                MetricUnit = "frames",
                DerivedFrom = [activity.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // Deep thread stack.
        if (maxThreadFrameDepth > DeepStackFrameThreshold)
        {
            signals.Add(new Finding
            {
                Fingerprint = Fingerprint("deep-stack"),
                Analyzer = AnalyzerName,
                Category = "Performance",
                Severity = Severity.Warning,
                Title = "Unusually deep thread stack detected",
                Narrative = $"OS thread {maxThreadFrameDepthOSThreadId} has {maxThreadFrameDepth:N0} frames " +
                            $"on its stack, exceeding the {DeepStackFrameThreshold:N0}-frame informational threshold.",
                Recommendation = "Inspect this thread's stack for unbounded recursion or a re-entrant call chain " +
                                  "(e.g. recursive event handlers, self-referencing visitor patterns).",
                Tags = ["jit", "stack-depth", "recursion"],
                MetricValue = maxThreadFrameDepth,
                MetricUnit = "frames",
                DerivedFrom = [activity.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        Severity summarySeverity = Severity.Info;
        for (int i = 0; i < signals.Count; i++)
        {
            if (SeverityRank(signals[i].Severity) > SeverityRank(summarySeverity))
                summarySeverity = signals[i].Severity;
        }

        var findings = new List<Finding>(1 + signals.Count)
        {
            new()
            {
                Fingerprint = Fingerprint("overview"),
                Analyzer = AnalyzerName,
                Category = "Performance",
                Severity = summarySeverity,
                Title = "JIT code heap overview",
                Narrative = $"Total JIT code heap: {FormatHelper.FormatBytes(totalJitHeapBytes)}, " +
                            $"{jitManagerCount} JIT manager(s). " +
                            $"Active method frames on stacks: {activeMethodsOnStacks:N0} managed, " +
                            $"{unmanagedFrameCount:N0} runtime/internal. " +
                            $"Tiered recompilations observed (estimate): {tieredMethodCount:N0}.",
                Recommendation = signals.Count > 0
                    ? "Review the detailed JIT signals below and validate codegen/interop hotspots."
                    : "JIT footprint is within expected range.",
                Tags = ["jit", "overview"],
                MetricValue = totalJitHeapBytes,
                MetricUnit = "bytes",
                DerivedFrom = [activity.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        };

        findings.AddRange(signals);

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static int SeverityRank(Severity severity) => severity switch
    {
        Severity.Critical => 3,
        Severity.Warning => 2,
        _ => 1,
    };

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
