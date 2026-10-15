using DumpDetective.Core.Configuration;
using DumpDetective.Core.Enums;

namespace DumpDetective.Core.Options;

internal sealed class ReportOptions
{
    public ReportFormat Format { get; init; } = ReportFormat.Html;
    public ReportStyleVersion StyleVersion { get; init; } = ReportStyleVersion.V1;
    public bool ObservationsUncompressed { get; init; } = false;
}