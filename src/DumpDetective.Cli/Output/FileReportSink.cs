using DumpDetective.Cli.Console;
using DumpDetective.Cli.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Output;

internal sealed class FileReportSink(ReportBuilderFacade reportBuilderFacade) : IReportSink
{
    public string Name => "File";

    public async ValueTask PublishAsync(ResolvedExecutionOptions resolved, AnalysisReportDocument document, CancellationToken cancellationToken)
    {
        string rendered = reportBuilderFacade.RenderDocument(
            document,
            resolved.Report.Format,
            new HtmlRenderSettings(resolved.Report.PreRender, resolved.Report.StyleVersion));

        await File.WriteAllTextAsync(resolved.OutputPath, rendered, cancellationToken);
        ConsoleUx.ReportWritten(resolved.OutputPath);
    }
}
