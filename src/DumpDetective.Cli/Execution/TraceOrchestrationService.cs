using System.Diagnostics;

using DumpDetective.Cli.Console;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Cli.Pipeline.Nodes;
using DumpDetective.Orchestration.Graph;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Trace-only counterpart to <see cref="DumpOrchestrationService"/> — runs the same
/// <see cref="TraceNodeFactory"/> node triple a combined dump+trace session runs, just alone in its
/// own <see cref="PipelineGraph"/>/<see cref="PipelineNodeContext"/> instead of appended to a dump
/// artifact's nodes. Unified onto the node-based path in migration step 5f — before that, this
/// class called the now-deleted <c>TraceAnalysisRunner</c> directly, a separate procedural
/// implementation of the same 3 steps <see cref="BuildTraceIndexNode"/>/<see cref="RunTraceAnalyzersNode"/>/
/// <see cref="WriteTraceReportNode"/> now own, which the combined path would otherwise have had to
/// duplicate or diverge from.
/// </summary>
internal sealed class TraceOrchestrationService
{
    public async Task<int> ExecuteAsync(string tracePath, string? outputPath, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ConsoleUx.Header("DumpDetective Trace Analysis");
        ConsoleUx.Info($"Trace: {Path.GetFileName(tracePath)}");

        using TracePipelineState state = new() { TracePath = tracePath, OutputPath = outputPath };

        PipelineGraph graph = PipelineGraphBuilder.Build(TraceNodeFactory.BuildNodes());
        var context = new PipelineNodeContext();
        context.Set(TracePipelineKeys.State, state);

        await new PipelineExecutor().RunAsync(graph, context, cancellationToken);

        ConsoleUx.Success($"Trace analysis complete in {stopwatch.Elapsed.TotalSeconds:N1}s — {state.Observations.Count} observation(s).");
        return 0;
    }

    /// <summary>
    /// Deliberately a plain console printer, not a synthesis rule — there is no
    /// <see cref="Synthesis.ISynthesisRule"/> engine wired up yet (Phase 5, deferred under § 8).
    /// Aggregating for a human-readable summary here does not feed back into any
    /// <see cref="Observation"/>; it is presentation only. Internal (not private) so
    /// <see cref="WriteTraceReportNode"/> can reuse it for both the standalone and combined paths.
    /// </summary>
    internal static void PrintSummary(IReadOnlyList<Observation> observations)
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
