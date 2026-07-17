using DumpDetective.Core.Options;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Enums;

namespace DumpDetective.Cli.Configuration;

internal static class ConfigurationParseHelpers
{

    public static ReportFormat? ParseReportFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        return format.Trim().ToLowerInvariant() switch
        {
            "text" or "txt" => ReportFormat.Text,
            "markdown" or "md" => ReportFormat.Markdown,
            "html" or "htm" => ReportFormat.Html,
            _ => throw new ArgumentException($"Invalid ReportFormat value '{format}' in config.")
        };
    }

    public static ReportStyleVersion? ParseReportStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        return style.Trim().ToLowerInvariant() switch
        {
            "v1" or "1" => ReportStyleVersion.V1,
            "v2" or "2" => ReportStyleVersion.V2,
            _ => throw new ArgumentException($"Invalid ReportStyleVersion value '{style}' in config.")
        };
    }

    public static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    public static int? NonNegativeOrNull(int? value) => value is >= 0 ? value : null;

    public static AnalysisProfile? ParseAnalysisProfile(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "fast" => AnalysisProfile.Fast,
            "balanced" => AnalysisProfile.Balanced,
            "full" => AnalysisProfile.Full,
            "deep" => AnalysisProfile.Full,
            _ => throw new ArgumentException($"Invalid Analysis Profile value '{raw}' in config.")
        };
    }
}
