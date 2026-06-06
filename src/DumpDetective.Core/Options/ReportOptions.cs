using DumpDetective.Core.Configuration;

namespace DumpDetective.Core.Options;

internal sealed class ReportOptions
{
    public ReportFormat Format { get; init; } = ReportFormat.Html;
    public ReportStyleVersion StyleVersion { get; init; } = ReportStyleVersion.V1;
    public bool PreRender { get; init; } = false;
    public bool SeparateJson { get; init; } = false;
}