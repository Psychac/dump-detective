using DumpDetective.Orchestration.Graph;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Pipeline.Nodes;

internal sealed class BuildReportNode(ReportBuilderFacade reportBuilderFacade) : IPipelineNode
{
    public string Id => "Build report";
    public IReadOnlyCollection<string> Produces { get; } = [SingleDumpPipelineKeys.ReportDocument];
    public IReadOnlyCollection<string> Consumes { get; } = [SingleDumpPipelineKeys.AnalyzerRuns];

    public ValueTask ExecuteAsync(PipelineNodeContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SingleDumpPipelineState state = context.Get<SingleDumpPipelineState>(SingleDumpPipelineKeys.State);

        if (state.IncidentContext is null)
        {
            throw new InvalidOperationException("Incident context was not initialized by the per-dump execution stage.");
        }

        // Rendering to a file format is the report sinks' job, run by WriteOutputNode.
        var doc = reportBuilderFacade.BuildReportDocument(
            state.Resolved.DumpPath,
            state.Runs,
            state.AnalysisElapsed,
            state.IncidentContext,
            state.Insights,
            state.EvidenceSourcesByFingerprint);
        doc = doc with { Coverage = state.Coverage, CoverageView = CoverageProse.Build(state.Coverage) };
        state.ReportDocument = doc;

        context.Set(SingleDumpPipelineKeys.ReportDocument, doc);

        return ValueTask.CompletedTask;
    }
}
