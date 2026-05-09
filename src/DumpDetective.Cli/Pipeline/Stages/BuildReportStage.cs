using DumpDetective.Cli.Services;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class BuildReportStage(ReportBuilderFacade reportBuilderFacade) : IAnalysisStage
{
    public string Name => "Build report";

    public Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        state.IncidentContext = IncidentContextFactory.Create(
            mode: "Single",
            loadContext: state.LoadContext,
            resolved: state.Resolved,
            activeAnalyzers: state.ActiveAnalyzers,
            elapsed: state.AnalysisElapsed);

        // Build the serializable report document and keep it in state for artifact persistence.
        var doc = reportBuilderFacade.BuildReportDocument(
            state.Resolved.DumpPath,
            state.Resolved.Report.Audience,
            state.Runs,
            state.AnalysisElapsed,
            state.IncidentContext);
        state.ReportDocument = doc;

        // Render the report string for the chosen output format.
        // Honor explicit pre-render request for template-driven renderer.
        try
        {
            DumpDetective.Reporting.Formatters.HtmlReportRenderer.ForcePreRender = state.Resolved.Report.PreRender;
            state.RenderedReport = reportBuilderFacade.RenderDocument(doc, state.Resolved.Report.Format);
        }
        finally
        {
            DumpDetective.Reporting.Formatters.HtmlReportRenderer.ForcePreRender = false;
        }

        return Task.CompletedTask;
    }
}
