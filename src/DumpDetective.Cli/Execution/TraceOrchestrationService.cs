using System.Diagnostics;

using DumpDetective.Cli.Console;
using DumpDetective.Cli.Output;
using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sources.NetTrace;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Trace-only counterpart to <see cref="SingleDumpOrchestrationService"/> — the CLI-facing half of
/// the interim router accepted in docs/refactor/modularity-plan.md § 8. Builds the trace's own
/// container, runs the Phase 6b analyzers over it, prints a console summary, and writes
/// <c>report.json</c> unconditionally (§ 8 step 6). No combined dump+trace session (out of scope
/// for this increment — see docs/refactor/modularity/phase-6-trace-source.md § Phase 6b exit
/// criteria).
/// </summary>
internal sealed class TraceOrchestrationService
{
    public async Task<int> ExecuteAsync(string tracePath, string? outputPath, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ConsoleUx.Header("DumpDetective Trace Analysis");
        ConsoleUx.Info($"Trace: {Path.GetFileName(tracePath)}");

        IReadOnlyList<Observation> observations = TraceAnalysisRunner.Run(tracePath, targetProcessId: null, cancellationToken);

        PrintSummary(observations);

        var report = new TraceSessionReport(tracePath, DateTime.UtcNow, observations);
        string resolvedOutputPath = TraceReportWriter.ResolveOutputPath(tracePath, outputPath);
        await TraceReportWriter.WriteAsync(report, resolvedOutputPath, cancellationToken);

        ConsoleUx.Success($"Trace analysis complete in {stopwatch.Elapsed.TotalSeconds:N1}s — {observations.Count} observation(s).");
        return 0;
    }

    /// <summary>
    /// Deliberately a plain console printer, not a synthesis rule — there is no
    /// <see cref="Synthesis.ISynthesisRule"/> engine wired up yet (Phase 5, deferred under § 8).
    /// Aggregating for a human-readable summary here does not feed back into any
    /// <see cref="Observation"/>; it is presentation only.
    /// </summary>
    private static void PrintSummary(IReadOnlyList<Observation> observations)
    {
        var byType = observations.GroupBy(o => o.ObservationType);
        foreach (var group in byType)
        {
            List<Observation> items = [.. group];
            ConsoleUx.Info($"{group.Key}: {items.Count}");

            if (group.Key == "gc.pause")
            {
                int withGcData = items.Count(o => o.Measures.ContainsKey("gc.generation"));
                double totalMs = items.Sum(o => o.Measures["pause.duration"].Value);
                double maxMs = items.Count > 0 ? items.Max(o => o.Measures["pause.duration"].Value) : 0;
                ConsoleUx.Info($"  total pause {totalMs:N1} ms, max {maxMs:N1} ms, {withGcData}/{items.Count} attributed to a completed GC");
            }
            else if (group.Key == "contention.episode")
            {
                double totalMs = items.Sum(o => o.Measures["contention.duration"].Value);
                double maxMs = items.Count > 0 ? items.Max(o => o.Measures["contention.duration"].Value) : 0;
                ConsoleUx.Info($"  total contention {totalMs:N1} ms, max {maxMs:N1} ms");
            }
            else if (group.Key == "cpu.sample-attribution")
            {
                long totalSamples = (long)items.Sum(o => o.Measures["cpu.sample-count"].Value);
                ConsoleUx.Info($"  {totalSamples:N0} samples resolved across {items.Count} distinct methods (leaf frame only — no call tree)");

                foreach (Observation top in items.OrderByDescending(o => o.Measures["cpu.sample-count"].Value).Take(5))
                {
                    var method = (MethodRef)top.Subjects[0];
                    long sampleCount = (long)top.Measures["cpu.sample-count"].Value;
                    ConsoleUx.Info($"    {sampleCount,8:N0}  {method.DeclaringType.CanonicalName}.{method.Name}");
                }
            }
        }
    }
}
