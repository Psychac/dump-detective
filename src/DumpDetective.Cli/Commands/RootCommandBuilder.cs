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
        // Also accepts .etl/.nettrace trace captures — DumpAnalysisService.IsTraceFile sniffs the
        // extension and routes to the trace-only pipeline (docs/refactor/modularity-plan.md § 8's
        // interim router) before any dump-specific option resolution runs. Not a separate argument
        // because the router already decides which pipeline runs from this one positional value;
        // adding a second argument would just be two ways to say the same thing.
        Description = "Path to the dump file (.dmp) or trace capture (.etl/.nettrace) to analyze."
    };

    private readonly Option<string?> _configPathOption = new("--config")
    {
        Description = "Path to JSON config. If found, config values take precedence over CLI values."
    };

    private readonly Option<string?> _trendDumpOption = new("--trend")
    {
        Description = "Semicolon-separated dump paths ordered oldest->newest."
    };

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
    private readonly Option<string?> _outputPathOption = new("--output")
    {
        Description = "Output report path. For a dump, the rendered --report-format document; " +
            "for a trace, the JSON observation report (content is always JSON regardless of the " +
            "extension given here — § 8's report.json-unconditional step, see " +
            "TraceReportWriter). Defaults next to the input file when omitted."
    };

    private readonly Option<bool> _observationsUncompressedOption = new("--observations-uncompressed") { Description = "Write the observations side-car as plain JSON instead of gzip." };

    private readonly Option<string?> _cacheDirectoryOption = new("--cache-dir")
    {
        Description = "Directory to store the disk-based heap index cache. Falls back to a folder next to the dump, then to a temp-folder location if neither is writable."
    };

    private readonly Option<string?> _tracePathOption = new("--trace")
    {
        Description = "Path to a trace capture (.etl/.nettrace) to analyze alongside the dump, " +
            "producing a combined dump+trace session (docs/refactor/modularity/" +
            "phase-4-session-orchestration.md migration step 5d). Cannot be combined with " +
            "--trend — a session accepts either N dumps or exactly one dump plus one trace, not " +
            "both at once."
    };

    public RootCommand Build()
    {
        var command = new RootCommand("DumpDetective dump analyzer")
        {
            _dumpPathArgument,
            _configPathOption,
            _trendDumpOption,
            _memoryDiagnosticsOption,
            _performanceDiagnosticsOption,
            _diagnosticModeOption,
            _includeAnalyzersOption,
            _excludeAnalyzersOption,
            _reportFormatOption,
            _reportStyleOption,
            _observationsUncompressedOption,
            _outputPathOption,
            _cacheDirectoryOption,
            _tracePathOption
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
            ParseTrend(parseResult.GetValue(_trendDumpOption)),
            parseResult.GetValue(_memoryDiagnosticsOption),
            parseResult.GetValue(_performanceDiagnosticsOption),
            ParseReportStyle(parseResult.GetValue(_reportStyleOption)),
            parseResult.GetValue(_observationsUncompressedOption),
            parseResult.GetValue(_cacheDirectoryOption),
            parseResult.GetValue(_tracePathOption));
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
