using DumpDetective.Core.Configuration;

using System.CommandLine;
using System.CommandLine.Parsing;
using DumpDetective.Core.Enums;

namespace DumpDetective.Cli.Commands;

internal sealed class RootCommandBuilder
{
    private readonly Argument<string?> _dumpPathArgument = new("dump-path")
    {
        Arity = ArgumentArity.ZeroOrOne,
        Description = "Path to the dump file to analyze."
    };

    private readonly Option<string?> _configPathOption = new("--config")
    {
        Description = "Path to JSON config. If found, config values take precedence over CLI values."
    };

    // need to see use case and remove this.
    [Obsolete("Use --trend instead.")]
    private readonly Option<string?> _baselineDumpOption = new("--baseline")
    {
        Description = "Baseline dump path used for comparison."
    };

    private readonly Option<string?> _trendDumpOption = new("--trend")
    {
        Description = "Semicolon-separated dump paths ordered oldest->newest."
    };

    // TODO: Need to see if these options are necessary tbh, or if a simpler approach is better. For now, leaving them in for advanced users.
    private readonly Option<int?> _highReferenceThresholdOption = new("--high-reference-threshold");
    private readonly Option<int?> _maxDuplicateStringLengthOption = new("--max-duplicate-string-length");
    private readonly Option<int?> _minDuplicateStringCountOption = new("--min-duplicate-string-count");
    private readonly Option<int?> _maxReferenceAddressesOption = new("--max-reference-addresses");
    private readonly Option<int?> _eventLeakMinSubscribersOption = new("--event-leak-min-subscribers");

    // TODO: Need to investigate why there is separate memory and performance diagnostics options
    private readonly Option<bool> _memoryDiagnosticsOption = new("--memory-diagnostics");
    private readonly Option<bool> _performanceDiagnosticsOption = new("--performance-diagnostics");
    private readonly Option<bool> _diagnosticModeOption = new("--diagnostic-mode");


    private readonly Option<string?> _includeAnalyzersOption = new("--include-analyzers")
    {
        Description = "Comma-separated analyzer names to include."
    };
    private readonly Option<string?> _excludeAnalyzersOption = new("--exclude-analyzers")
    {
        Description = "Comma-separated analyzer names to exclude."
    };

    // TODO: whats report format? is this for the report output format? if so, we should clarify that in the description.
    private readonly Option<string?> _reportFormatOption = new("--report-format");
    private readonly Option<string?> _reportStyleOption = new("--report-style")
    {
        Description = "Report style version: v1 or v2."
    };
    private readonly Option<string?> _outputPathOption = new("--output");

    // TODO: pre-render?? there is something like this??
    private readonly Option<bool> _preRenderOption = new("--pre-render") { Description = "Pre-render findings and analyzer sections server-side for faster initial paint." };
    
    private readonly Option<bool> _separateJsonOption = new("--separate-json") { Description = "Write report.html and report.json side-by-side; client will load external JSON." };
    
    
    private readonly Option<string?> _cacheDirectoryOption = new("--cache-dir")
    {
        Description = "Directory to store the disk-based heap index cache. Falls back to a folder next to the dump, then to a temp-folder location if neither is writable."
    };

    public RootCommand Build()
    {
        var command = new RootCommand("DumpDetective dump analyzer")
        {
            _dumpPathArgument,
            _configPathOption,
            _baselineDumpOption,
            _trendDumpOption,
            _highReferenceThresholdOption,
            _maxDuplicateStringLengthOption,
            _minDuplicateStringCountOption,
            _maxReferenceAddressesOption,
            _eventLeakMinSubscribersOption,
            _memoryDiagnosticsOption,
            _performanceDiagnosticsOption,
            _diagnosticModeOption,
            _includeAnalyzersOption,
            _excludeAnalyzersOption,
            _reportFormatOption,
            _reportStyleOption,
            _preRenderOption,
            _separateJsonOption,
            _outputPathOption,
            _cacheDirectoryOption
        };

        return command;
    }

    public AnalysisCommandRequest Map(ParseResult parseResult)
    {
        return new AnalysisCommandRequest(
            parseResult.GetValue(_dumpPathArgument),
            parseResult.GetValue(_outputPathOption),
            ParseReportFormat(parseResult.GetValue(_reportFormatOption)),
            parseResult.GetValue(_configPathOption),
            ParseNameList(parseResult.GetValue(_includeAnalyzersOption)),
            ParseNameList(parseResult.GetValue(_excludeAnalyzersOption)),
            parseResult.GetValue(_diagnosticModeOption),
            parseResult.GetValue(_baselineDumpOption),
            ParseTrend(parseResult.GetValue(_trendDumpOption)),
            parseResult.GetValue(_highReferenceThresholdOption),
            parseResult.GetValue(_maxDuplicateStringLengthOption),
            parseResult.GetValue(_minDuplicateStringCountOption),
            parseResult.GetValue(_maxReferenceAddressesOption),
            parseResult.GetValue(_eventLeakMinSubscribersOption),
            parseResult.GetValue(_memoryDiagnosticsOption),
            parseResult.GetValue(_performanceDiagnosticsOption),
            ParseReportStyle(parseResult.GetValue(_reportStyleOption)),
            parseResult.GetValue(_preRenderOption),
            parseResult.GetValue(_separateJsonOption),
            parseResult.GetValue(_cacheDirectoryOption));
    }

    private static IReadOnlyList<string>? ParseTrend(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyCollection<string> ParseNameList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ReportFormat? ParseReportFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "text" or "txt" => ReportFormat.Text,
            "markdown" or "md" => ReportFormat.Markdown,
            "html" or "htm" => ReportFormat.Html,
            _ => throw new ArgumentException($"Invalid report format '{value}'. Expected text, markdown, or html.")
        };
    }

    private static ReportStyleVersion? ParseReportStyle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "v1" or "1" => ReportStyleVersion.V1,
            "v2" or "2" => ReportStyleVersion.V2,
            _ => throw new ArgumentException($"Invalid report style '{value}'. Expected v1 or v2.")
        };
    }
}
