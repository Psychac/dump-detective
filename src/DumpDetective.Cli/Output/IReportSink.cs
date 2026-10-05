using DumpDetective.Cli.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Cli.Output;

internal interface IReportSink
{
    string Name { get; }

    ValueTask PublishAsync(ResolvedExecutionOptions resolved, AnalysisReportDocument document, CancellationToken cancellationToken);
}
