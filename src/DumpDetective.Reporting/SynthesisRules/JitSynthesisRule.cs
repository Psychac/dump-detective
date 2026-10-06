using DumpDetective.Sdk.Identity;
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

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
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
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

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

        var signals = new List<Claim>(6);

        // JIT heap bloat.
        if (totalJitHeapBytes > JitHeapBloatThreshold)
        {
            signals.Add(new Claim
            {
                Fingerprint = Fingerprint("heap-bloat"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Bytes(totalJitHeapBytes),
                    Reference = ClaimParts.Budget(ClaimParts.Bytes(JitHeapBloatThreshold), "heap-bloat threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "the JIT code heap" },
                },
                Support = [new EvidenceStatement([activity.Id], $"The JIT code heap totals {FormatHelper.FormatBytes(totalJitHeapBytes)} across {jitManagerCount} JIT manager(s).")],
                Counter = Counter.None("A large JIT heap is normal for a large codebase; one snapshot does not show it is growing."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -loader",
                        Test = $"If the code heap sizes in !eeheap -loader total far less than {FormatHelper.FormatBytes(totalJitHeapBytes)}, the figure is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the JIT heap is the same size, it is a stable footprint, not growth.",
                    },
                ],
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
                signals.Add(new Claim
                {
                    Fingerprint = Fingerprint("unmanaged-frame-ratio"),
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Exceeds,
                        Measure = ClaimParts.Percent(unmanagedRatio * 100.0),
                        Reference = ClaimParts.Budget(ClaimParts.Percent(HighUnmanagedFrameRatio * 100.0), "unmanaged-frame threshold"),
                        Qualifiers = new AssertionQualifiers { Location = "runtime and internal frames on thread stacks" },
                    },
                    Support = [new EvidenceStatement([activity.Id], $"{unmanagedFrameCount:N0} of {totalFrames:N0} stack frames are runtime or internal frames.")],
                    Counter = Counter.None("Runtime-internal frames sit on every thread parked in a wait, so the share does not separate interop work from idle waiting."),
                    FalsifiedBy =
                    [
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Windbg,
                            Command = "!eestack",
                            Test = "If most of the runtime frames belong to threads parked in wait or sleep, the share reflects idle threads, not interop activity.",
                        },
                    ],
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
            signals.Add(new Claim
            {
                Fingerprint = Fingerprint("tiered-compilation"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(tieredMethodCount),
                    Qualifiers = new AssertionQualifiers { Location = "methods with more than one native code address", Mechanism = "tiered recompilation" },
                },
                Support = [new EvidenceStatement([activity.Id], $"About {tieredMethodCount:N0} stack-visible methods show multiple native code addresses.")],
                Counter = Counter.None("Tiered recompilation is expected runtime behaviour and asserts no problem, so there is nothing to weaken."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If TieredCompilation is disabled in the runtime configuration, the multiple code addresses have another cause.",
                    },
                ],
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

            signals.Add(new Claim
            {
                Fingerprint = Fingerprint("large-methods"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Bytes(topSize),
                    Reference = ClaimParts.Budget(ClaimParts.Bytes(largeMethodThresholdBytes), "large-method threshold"),
                    Qualifiers = new AssertionQualifiers { Location = $"the largest method on stacks, {largeMethodCount} over the threshold" },
                },
                Support = [new EvidenceStatement([activity.Id], $"The largest method on stacks is '{topSignature}' at {FormatHelper.FormatBytes(topSize)} of native code.")],
                Counter = Counter.None("Large methods are often generated code, such as serializers or parsers, that cannot reasonably be split."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpmd <method descriptor>",
                        Test = $"If the code size of '{topSignature}' in !dumpmd is far from {FormatHelper.FormatBytes(topSize)}, the size is wrong.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the method is machine-generated, refactoring it is not an option.",
                    },
                ],
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
            signals.Add(new Claim
            {
                Fingerprint = Fingerprint("dynamic-codegen"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(dynamicMethodFrameCount),
                    Qualifiers = new AssertionQualifiers { Location = "active frames in dynamic modules", Mechanism = "DynamicMethod, emitted types or compiled expression trees" },
                },
                Support = [new EvidenceStatement([activity.Id], $"{dynamicMethodFrameCount:N0} active frames resolve to a dynamic module.")],
                Counter = Counter.None("Frameworks cache dynamic code; frames on stacks do not show it is regenerated repeatedly."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the dynamic methods are the same ones and the JIT heap is flat, the code is cached.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the expression trees or emitted types are compiled once and stored, they are not regenerated per call.",
                    },
                ],
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
            signals.Add(new Claim
            {
                Fingerprint = Fingerprint("deep-stack"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Subject = new ThreadRef { OsThreadId = maxThreadFrameDepthOSThreadId, Fidelity = MatchFidelity.Exact },
                    Measure = ClaimParts.Count(maxThreadFrameDepth),
                    Reference = ClaimParts.Budget(ClaimParts.Count(DeepStackFrameThreshold), "deep-stack threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "the deepest thread stack" },
                },
                Support = [new EvidenceStatement([activity.Id], $"OS thread {maxThreadFrameDepthOSThreadId} has {maxThreadFrameDepth:N0} frames.")],
                Counter = Counter.None("Legitimate recursion, such as a tree walk, produces deep stacks; depth alone does not show unbounded recursion."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!clrstack",
                        Test = $"On OS thread {maxThreadFrameDepthOSThreadId}: if the frames show a bounded walk with different methods at each level, not one repeating cycle, it is not runaway recursion.",
                    },
                ],
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

        var findings = new List<Claim>(1 + signals.Count)
        {
            new()
            {
                Fingerprint = Fingerprint("overview"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Composes,
                    Measure = ClaimParts.Bytes(totalJitHeapBytes),
                    Qualifiers = new AssertionQualifiers { Location = "JIT code heap and stack activity" },
                },
                Support = [new EvidenceStatement([activity.Id], $"{FormatHelper.FormatBytes(totalJitHeapBytes)} JIT heap; {activeMethodsOnStacks:N0} managed and {unmanagedFrameCount:N0} runtime frames on stacks.")],
                Counter = Counter.None("This describes JIT activity and asserts no problem, so there is nothing to weaken."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -loader",
                        Test = $"If the code heap total in !eeheap -loader is far from {FormatHelper.FormatBytes(totalJitHeapBytes)}, the overview is wrong.",
                    },
                ],
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

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static int SeverityRank(Severity severity) => severity switch
    {
        Severity.Critical => 3,
        Severity.Warning => 2,
        _ => 1,
    };

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
