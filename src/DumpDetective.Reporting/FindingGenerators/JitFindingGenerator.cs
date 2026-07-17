using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class JitFindingGenerator : IFindingGenerator
{
    private const ulong JitHeapBloatThreshold = 500 * 1024 * 1024; // 500 MB
    private const double HighUnmanagedFrameRatio = 0.30;            // 30 %
    private readonly record struct JitSignal(
        FindingSeverity Severity,
        int Priority,
        string Title,
        string Evidence,
        string Recommendation,
        string[] Tags,
        double MetricValue,
        string MetricUnit);

    public string AnalyzerName => "JIT Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is JitDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not JitDomainResult r) return [];

        var signals = new List<JitSignal>(4);

        // JIT heap bloat — > 500 MB is unusual and typically indicates reflection-intensive
        // or dynamic-code-gen heavy workloads that JIT-compile enormous method bodies.
        if (r.TotalJitHeapBytes > JitHeapBloatThreshold)
        {
            signals.Add(new JitSignal(
                Severity: FindingSeverity.Warning,
                Priority: 400,
                Title: "JIT code heap unusually large",
                Evidence: $"JIT code heap totals {FormatHelper.FormatBytes(r.TotalJitHeapBytes)} " +
                          $"across {r.JitManagerCount} JIT manager(s). " +
                          $"This is above the 500 MB warning threshold.",
                Recommendation: "Profile with dotnet-trace / PerfView to identify hot code paths. " +
                                "Consider ReadyToRun publication or NativeAOT to reduce JIT footprint.",
                Tags: ["jit", "native-heap", "performance"],
                MetricValue: (double)r.TotalJitHeapBytes,
                MetricUnit: "bytes"));
        }

        // High unmanaged frame ratio — many runtime/internal frames on thread stacks.
        int totalFrames = r.ManagedFrameCount + r.UnmanagedFrameCount;
        if (totalFrames > 0)
        {
            double unmanagedRatio = (double)r.UnmanagedFrameCount / totalFrames;
            if (unmanagedRatio >= HighUnmanagedFrameRatio && r.UnmanagedFrameCount > 50)
            {
                signals.Add(new JitSignal(
                    Severity: FindingSeverity.Info,
                    Priority: 120,
                    Title: "High unmanaged/runtime frame ratio on thread stacks",
                    Evidence: $"{r.UnmanagedFrameCount:N0} runtime/internal frames out of " +
                              $"{totalFrames:N0} total ({unmanagedRatio:P0}). " +
                              $"Indicates significant P/Invoke, COM interop, or CLR internal activity.",
                    Recommendation: "Review P/Invoke usage patterns. Batch interop calls and " +
                                    "prefer Span<T>/Memory<T> over per-element marshalling.",
                    Tags: ["jit", "interop", "unmanaged"],
                    MetricValue: unmanagedRatio,
                    MetricUnit: "ratio"));
            }
        }

        // Tiered compilation detected — informational note.
        if (r.TieredMethodCount > 0)
        {
            signals.Add(new JitSignal(
                Severity: FindingSeverity.Info,
                Priority: 80,
                Title: "Tiered compilation activity detected",
                Evidence: $"{r.TieredMethodCount:N0} method(s) observed with multiple native code " +
                          $"addresses for the same metadata token, indicating tiered recompilation " +
                          $"(Tier0 → Tier1). This is expected behaviour.",
                Recommendation: "If startup-time JIT overhead is a concern, " +
                                "consider ReadyToRun images (dotnet publish -r ... --self-contained).",
                Tags: ["jit", "tiered-compilation"],
                MetricValue: r.TieredMethodCount,
                MetricUnit: "methods"));
        }

        // Large methods detected on stacks.
        if (r.TopLargestMethods.Count > 0)
        {
            var top = r.TopLargestMethods[0];
            ulong topSize = (ulong)top.HotSize + top.ColdSize;
            signals.Add(new JitSignal(
                Severity: FindingSeverity.Info,
                Priority: 140,
                Title: "Large JIT-compiled methods detected on thread stacks",
                Evidence: $"Largest method on stacks: '{top.Signature}' " +
                          $"({FormatHelper.FormatBytes(topSize)} native code). " +
                          $"{r.TopLargestMethods.Count} method(s) exceed the 64 KB threshold.",
                Recommendation: "Refactor methods over 64 KB native code — they prevent " +
                                "inlining and stress the JIT register allocator.",
                Tags: ["jit", "code-size", "performance"],
                MetricValue: (double)topSize,
                MetricUnit: "bytes"));
        }

        FindingSeverity summarySeverity = FindingSeverity.Info;
        for (int i = 0; i < signals.Count; i++)
        {
            if (SeverityRank(signals[i].Severity) > SeverityRank(summarySeverity))
                summarySeverity = signals[i].Severity;
        }

        var findings = new List<InsightFinding>(2)
        {
            // Summary finding (always emitted).
            new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Performance",
            Severity: summarySeverity,
            Title: "JIT code heap overview",
            Evidence: $"Total JIT code heap: {FormatHelper.FormatBytes(r.TotalJitHeapBytes)}, " +
                      $"{r.JitManagerCount} JIT manager(s). " +
                      $"Active method frames on stacks: {r.ActiveMethodsOnStacks:N0} managed, " +
                      $"{r.UnmanagedFrameCount:N0} runtime/internal. " +
                      $"Tiered recompilations observed: {r.TieredMethodCount:N0}.",
            Recommendation: signals.Count > 0
                ? "Review the top JIT signal below and validate codegen/interop hotspots."
                : "JIT footprint is within expected range.",
            Tags: ["jit", "overview"],
            MetricValue: (double)r.TotalJitHeapBytes,
            MetricUnit: "bytes")
        };

        if (signals.Count > 0)
        {
            JitSignal top = signals[0];
            for (int i = 1; i < signals.Count; i++)
            {
                JitSignal s = signals[i];
                bool betterSeverity = SeverityRank(s.Severity) > SeverityRank(top.Severity);
                bool sameSeverityHigherPriority = SeverityRank(s.Severity) == SeverityRank(top.Severity) && s.Priority > top.Priority;
                if (betterSeverity || sameSeverityHigherPriority)
                    top = s;
            }

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Performance",
                Severity: top.Severity,
                Title: top.Title,
                Evidence: top.Evidence,
                Recommendation: top.Recommendation,
                Tags: top.Tags,
                MetricValue: top.MetricValue,
                MetricUnit: top.MetricUnit));
        }

        return findings;
    }

    private static int SeverityRank(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 3,
        FindingSeverity.Warning => 2,
        _ => 1
    };
}
