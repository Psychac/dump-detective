using DumpDetective.Cli.Console;
using DumpDetective.Cli.Models;
using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Output;

/// <summary>
/// Writes <c>report.json</c> on every run, whatever <c>--format</c> was chosen, so tools and a future
/// UI can rely on it existing. Lands beside the primary output file (which defaults to beside the dump).
/// </summary>
internal sealed class ReportJsonSink(ReportBuilderFacade reportBuilderFacade) : IReportSink
{
    internal const string FileName = "report.json";

    public string Name => "ReportJson";

    public async ValueTask PublishAsync(ResolvedExecutionOptions resolved, AnalysisReportDocument document, CancellationToken cancellationToken)
    {
        string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(resolved.OutputPath)) ?? Directory.GetCurrentDirectory();
        string reportJsonPath = Path.Combine(outputDirectory, FileName);

        bool primaryOutputIsAlreadyReportJson =
            resolved.Report.Format == ReportFormat.Json
            && string.Equals(Path.GetFullPath(resolved.OutputPath), reportJsonPath, StringComparison.OrdinalIgnoreCase);
        if (primaryOutputIsAlreadyReportJson)
            return;

        string json = reportBuilderFacade.RenderDocument(document, ReportFormat.Json);
        await File.WriteAllTextAsync(reportJsonPath, json, cancellationToken);
        ConsoleUx.ReportWritten(reportJsonPath);
    }
}
