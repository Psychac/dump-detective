using DumpDetective.Orchestration.Graph;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Formatters;

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

        context.Set(SingleDumpPipelineKeys.ReportDocument, doc);

        return ValueTask.CompletedTask;
    }
}
