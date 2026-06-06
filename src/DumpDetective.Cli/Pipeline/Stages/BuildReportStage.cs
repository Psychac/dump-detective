using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Formatters;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class BuildReportStage(ReportBuilderFacade reportBuilderFacade) : IAnalysisStage
{
    public string Name => "Build report";

    public Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (state.IncidentContext is null)
        {
            throw new InvalidOperationException("Incident context was not initialized by the per-dump execution stage.");
        }

        // Build the serializable report document and keep it in state for artifact persistence.
        var doc = reportBuilderFacade.BuildReportDocument(
            state.Resolved.DumpPath,
            state.Runs,
            state.AnalysisElapsed,
            state.IncidentContext,
            state.Insights);
        state.ReportDocument = doc;

        // Render using explicit settings rather than mutable static renderer flags.
        state.RenderedReport = reportBuilderFacade.RenderDocument(
            doc,
            state.Resolved.Report.Format,
            new HtmlRenderSettings(state.Resolved.Report.PreRender, state.Resolved.Report.StyleVersion));

        return Task.CompletedTask;
    }
}
