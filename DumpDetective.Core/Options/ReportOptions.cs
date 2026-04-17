using DumpDetective.Core.Configuration;

namespace DumpDetective.Core.Options;

internal sealed class ReportOptions
{
    public ReportFormat Format { get; init; } = ReportFormat.Html;
}