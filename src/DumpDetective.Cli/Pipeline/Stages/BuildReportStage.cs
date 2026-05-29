using DumpDetective.Cli.Services;

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
            state.Resolved.Report.Audience,
            state.Runs,
            state.AnalysisElapsed,
            state.IncidentContext,
            state.Insights);
        state.ReportDocument = doc;

        // Render the report string for the chosen output format.
        // Honor explicit pre-render request for template-driven renderer.
        try
        {
            DumpDetective.Reporting.Formatters.HtmlReportRenderer.ForcePreRender = state.Resolved.Report.PreRender;
            DumpDetective.Reporting.Formatters.HtmlReportRenderer.ForceReportStyleVersion = state.Resolved.Report.StyleVersion;
            state.RenderedReport = reportBuilderFacade.RenderDocument(doc, state.Resolved.Report.Format);
        }
        finally
        {
            DumpDetective.Reporting.Formatters.HtmlReportRenderer.ForcePreRender = false;
            DumpDetective.Reporting.Formatters.HtmlReportRenderer.ForceReportStyleVersion = DumpDetective.Core.Configuration.ReportStyleVersion.V1;
        }

        return Task.CompletedTask;
    }
}
