using DumpDetective.Cli.Console;
using DumpDetective.Orchestration.Graph;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sources.NetTrace;

namespace DumpDetective.Cli.Pipeline.Nodes;

/// <summary>
/// Runs the 3 trace analyzers (step 5b's retyping onto <see cref="IAnalyzer"/>) against the
/// container <see cref="BuildTraceIndexNode"/> built — the trace-side analog of
/// <see cref="RunAnalyzersNode"/>. Hardcodes the same 3-analyzer list <c>TraceAnalysisRunner</c>
/// used (not yet capability-resolved against a live catalog here): a trace artifact always provides
/// every <c>trace.*</c> capability today (see <c>TraceArtifactFactory</c>'s known-gap remarks), so
/// all 3 always resolve <c>Satisfied</c> in practice — threading a real
/// <see cref="Discovery.CapabilityResolution"/> through here would be plumbing with no observable
/// difference yet.
/// </summary>
internal sealed class RunTraceAnalyzersNode(string id) : IPipelineNode
{
    private static readonly IReadOnlyList<IAnalyzer> Analyzers =
        [new GcPauseAnalyzer(), new ContentionAnalyzer(), new CpuHotspotAnalyzer()];

    public string Id { get; } = id;
    public IReadOnlyCollection<string> Produces { get; } = [TracePipelineKeys.Observations];
    public IReadOnlyCollection<string> Consumes { get; } = [TracePipelineKeys.Container];

    public async ValueTask ExecuteAsync(PipelineNodeContext context, CancellationToken cancellationToken)
    {
        TracePipelineState state = context.Get<TracePipelineState>(TracePipelineKeys.State);

        var sink = new CollectingObservationSink();
        var analysisContext = new AnalysisContext
        {
            Observations = sink,
            ArtifactId = new ArtifactId(state.TracePath),
            TraceCpuSamples = state.Container!.CpuSamples,
            TraceGcPauses = state.Container!.GcPauses,
            TraceContention = state.Container!.Contention,
        };

        foreach (IAnalyzer analyzer in Analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await analyzer.AnalyzeAsync(analysisContext, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolated per-analyzer, mirroring the dump side's own AnalysisPipeline (one bad
                // analyzer becomes a Failed result there, not a thrown exception) — a corrupt or
                // unexpected trace record in one analyzer's section must not sink a combined
                // session's dump half, whose report the executor has typically already written to
                // disk by the time this node runs (dump nodes precede trace nodes in the shared
                // node list).
                ConsoleUx.Warning($"Trace analyzer '{analyzer.Name}' failed: {ex.Message}");
            }
        }

        state.Observations = sink.Observations;
        context.Set(TracePipelineKeys.Observations, state.Observations);
    }
}
