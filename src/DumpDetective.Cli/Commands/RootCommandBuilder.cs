using DumpDetective.Core.Configuration;

using System.CommandLine;
using System.CommandLine.Parsing;

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

    private readonly Option<string?> _baselineDumpOption = new("--baseline")
    {
        Description = "Baseline dump path used for comparison."
    };

    private readonly Option<string?> _trendDumpOption = new("--trend")
    {
        Description = "Semicolon-separated dump paths ordered oldest->newest."
    };

    private readonly Option<int?> _highReferenceThresholdOption = new("--high-reference-threshold");
    private readonly Option<int?> _maxDuplicateStringLengthOption = new("--max-duplicate-string-length");
    private readonly Option<int?> _minDuplicateStringCountOption = new("--min-duplicate-string-count");
    private readonly Option<int?> _maxReferenceAddressesOption = new("--max-reference-addresses");
    private readonly Option<int?> _referenceChainTopCountOption = new("--reference-chain-top-count");
    private readonly Option<int?> _referenceChainMaxPathSearchObjectsOption = new("--reference-chain-max-path-search-objects");
    private readonly Option<int?> _eventLeakMinSubscribersOption = new("--event-leak-min-subscribers");
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
    private readonly Option<string?> _reportFormatOption = new("--report-format");
    private readonly Option<string?> _reportAudienceOption = new("--report-audience")
    {
        Description = "Audience tier: all, executive, developer, or deep."
    };
    private readonly Option<string?> _outputPathOption = new("--output");

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
            _referenceChainTopCountOption,
            _referenceChainMaxPathSearchObjectsOption,
            _eventLeakMinSubscribersOption,
            _memoryDiagnosticsOption,
            _performanceDiagnosticsOption,
            _diagnosticModeOption,
            _includeAnalyzersOption,
            _excludeAnalyzersOption,
            _reportFormatOption,
            _reportAudienceOption,
            _outputPathOption
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
            parseResult.GetValue(_referenceChainTopCountOption),
            parseResult.GetValue(_referenceChainMaxPathSearchObjectsOption),
            parseResult.GetValue(_eventLeakMinSubscribersOption),
            parseResult.GetValue(_memoryDiagnosticsOption),
            parseResult.GetValue(_performanceDiagnosticsOption),
            ParseReportAudience(parseResult.GetValue(_reportAudienceOption)));
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
            .ToList();
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

    private static ReportAudience? ParseReportAudience(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "all" => ReportAudience.All,
            "executive" or "exec" => ReportAudience.Executive,
            "developer" or "dev" => ReportAudience.Developer,
            "deep" or "full" => ReportAudience.Deep,
            _ => throw new ArgumentException($"Invalid report audience '{value}'. Expected all, executive, developer, or deep.")
        };
    }
}
