using DumpDetective.Cli.Services;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class BuildReportStage(ReportBuilderFacade reportBuilderFacade) : IAnalysisStage
{
    public string Name => "Build report";

    public Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        state.RenderedReport = reportBuilderFacade.BuildRenderedReport(
            state.Resolved.DumpPath,
            state.Resolved.Report.Format,
            state.Resolved.Report.Audience,
            state.Runs,
            state.AnalysisElapsed,
            cancellationToken);

        return Task.CompletedTask;
    }
}
