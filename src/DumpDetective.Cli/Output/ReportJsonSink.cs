using DumpDetective.Cli.Console;
using DumpDetective.Cli.Models;
using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Output;

/// <summary>
/// Writes <c>&lt;output&gt;.report.json</c> on every run, whatever <c>--format</c> was chosen, so tools and a
/// future UI can rely on it existing. Lands beside the primary output file (which defaults to beside the dump).
/// </summary>
internal sealed class ReportJsonSink(ReportBuilderFacade reportBuilderFacade) : IReportSink
{
    internal const string Suffix = ".report.json";

    public string Name => "ReportJson";

    public async ValueTask PublishAsync(ResolvedExecutionOptions resolved, AnalysisReportDocument document, CancellationToken cancellationToken)
    {
        string reportJsonPath = ReportSiblingPath.For(resolved.OutputPath, Suffix);

        string json = reportBuilderFacade.RenderDocument(document, ReportFormat.Json);
        await File.WriteAllTextAsync(reportJsonPath, json, cancellationToken);
        ConsoleUx.ReportWritten(reportJsonPath);
    }
}
