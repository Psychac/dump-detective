using DumpDetective.Cli.Services;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class BuildReportStage(ReportBuilderFacade reportBuilderFacade) : IAnalysisStage
{
    public string Name => "Build report";

    public Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Build the serializable report document and keep it in state for artifact persistence.
        var doc = reportBuilderFacade.BuildReportDocument(
            state.Resolved.DumpPath,
            state.Resolved.Report.Audience,
            state.Runs,
            state.AnalysisElapsed);
        state.ReportDocument = doc;

        // Render the report string for the chosen output format.
        state.RenderedReport = reportBuilderFacade.RenderDocument(doc, state.Resolved.Report.Format);

        return Task.CompletedTask;
    }
}
