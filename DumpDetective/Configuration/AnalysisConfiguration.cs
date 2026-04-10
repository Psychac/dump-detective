using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpDetective.Configuration
{
    internal class AnalysisConfiguration
    {
        private const int DefaultReferenceChainTopCount = 5;
        private const int DefaultEventLeakMinSubscribers = 0;
        private const int DefaultHighReferenceThreshold = 50;
        private const int DefaultMaxDuplicateStringLength = 500;
        private const int DefaultMinDuplicateStringCount = 10;
        private const int DefaultMaxReferenceAddressesToTrack = 1_000_000;

        private const string HighReferenceThresholdOption = "--high-reference-threshold=";
        private const string MaxDuplicateStringLengthOption = "--max-duplicate-string-length=";
        private const string MinDuplicateStringCountOption = "--min-duplicate-string-count=";
        private const string MaxReferenceAddressesOption = "--max-reference-addresses=";
        private const string ReferenceChainTopCountOption = "--reference-chain-top-count=";
        private const string EventLeakMinSubscribersOption = "--event-leak-min-subscribers=";
        private const string EnableMemoryDiagnosticsOption = "--memory-diagnostics";
        private const string EnablePerformanceDiagnosticsOption = "--performance-diagnostics";
        private const string ReportFormatOption = "--report-format=";
        private const string BaselineDumpOption = "--baseline=";
        private const string TrendDumpsOption = "--trend=";
        private const string ConfigFileOption = "--config=";
        private const string DefaultConfigFileName = "config.json";
        private const string FallbackSampleConfigFileName = "config.sample.json";

        /// <summary>
        /// Full path to the dump file to analyze.
        /// </summary>
        public required string DumpPath { get; init; }

        /// <summary>
        /// Optional output report path. When null, results are written only to console.
        /// </summary>
        public string? OutputPath { get; init; }
        public string? BaselineDumpPath { get; init; }
        public IReadOnlyList<string>? TrendDumpPaths { get; init; }
        public ReportFormat ReportFormat { get; init; } = ReportFormat.Html;

        /// <summary>
        /// Reference chain analyzer: number of top memory-consuming types to analyze.
        /// </summary>
        public int ReferenceChainTopCount { get; init; } = DefaultReferenceChainTopCount;

        /// <summary>
        /// Event leak analyzer: minimum subscriber count to report a leak candidate.
        /// </summary>
        public int EventLeakMinSubscribers { get; init; } = DefaultEventLeakMinSubscribers;
        public bool EnableMemoryDiagnostics { get; init; } = false;
        public bool EnablePerformanceDiagnostics { get; init; } = false;
        public bool WaitForKeyPressOnComplete { get; init; } = true;
        public bool ForceGCBetweenStages { get; init; } = false;

        /// <summary>
        /// Memory leak analyzer: minimum incoming references to consider an object highly referenced.
        /// </summary>
        public int HighReferenceThreshold { get; init; } = DefaultHighReferenceThreshold;

        /// <summary>
        /// Memory leak analyzer: maximum string length to include in duplicate-string tracking.
        /// </summary>
        public int MaxDuplicateStringLength { get; init; } = DefaultMaxDuplicateStringLength;

        /// <summary>
        /// Memory leak analyzer: minimum duplicate count before a string is reported.
        /// </summary>
        public int MinDuplicateStringCount { get; init; } = DefaultMinDuplicateStringCount;

        /// <summary>
        /// Memory leak analyzer: cap on unique referenced addresses tracked to bound memory usage.
        /// </summary>
        public int MaxReferenceAddressesToTrack { get; init; } = DefaultMaxReferenceAddressesToTrack;

        // Symbol server configuration (null = use ClrMD defaults)
        public string[]? SymbolPaths { get; init; }
        public string? SymbolCachePath { get; init; }

        public static AnalysisConfiguration FromCommandLineArgs(string[] args)
        {
            string? configPath = null;
            string? dumpPathFromCli = null;
            string? baselineDumpPath = null;
            List<string>? trendDumpPaths = null;

            foreach (string arg in args)
            {
                if (arg.StartsWith(ConfigFileOption, StringComparison.OrdinalIgnoreCase))
                {
                    configPath = ParseStringOption(arg, ConfigFileOption.TrimEnd('='));
                    continue;
                }

                if (arg.StartsWith(BaselineDumpOption, StringComparison.OrdinalIgnoreCase))
                {
                    baselineDumpPath = ParseStringOption(arg, BaselineDumpOption.TrimEnd('='));
                    continue;
                }

                if (arg.StartsWith(TrendDumpsOption, StringComparison.OrdinalIgnoreCase))
                {
                    trendDumpPaths = ParseDumpListOption(arg, TrendDumpsOption.TrimEnd('='));
                    continue;
                }

                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    dumpPathFromCli ??= arg;
                }
            }

            if (string.IsNullOrWhiteSpace(dumpPathFromCli))
            {
                throw new ArgumentException("Dump file path is required as first command-line argument.");
            }

            string? resolvedConfigPath = ResolveConfigPath(configPath);
            AnalysisConfigurationFileModel? fileConfig = resolvedConfigPath != null
                ? LoadConfigurationFile(resolvedConfigPath)
                : null;
            bool hasFileConfig = fileConfig != null;

            string dumpPath = dumpPathFromCli;

            if (!File.Exists(dumpPath))
            {
                throw new FileNotFoundException($"Dump file not found at '{dumpPath}'", dumpPath);
            }

            if (!string.IsNullOrWhiteSpace(baselineDumpPath) && !File.Exists(baselineDumpPath))
            {
                throw new FileNotFoundException($"Baseline dump file not found at '{baselineDumpPath}'", baselineDumpPath);
            }

            if (trendDumpPaths != null)
            {
                foreach (string trendDumpPath in trendDumpPaths)
                {
                    if (!File.Exists(trendDumpPath))
                    {
                        throw new FileNotFoundException($"Trend dump file not found at '{trendDumpPath}'", trendDumpPath);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(baselineDumpPath) && trendDumpPaths is { Count: > 0 })
            {
                throw new ArgumentException("Options '--baseline' and '--trend' are mutually exclusive. Use only one.");
            }

            int highReferenceThreshold = fileConfig?.HighReferenceThreshold ?? DefaultHighReferenceThreshold;
            int maxDuplicateStringLength = fileConfig?.MaxDuplicateStringLength ?? DefaultMaxDuplicateStringLength;
            int minDuplicateStringCount = fileConfig?.MinDuplicateStringCount ?? DefaultMinDuplicateStringCount;
            int maxReferenceAddressesToTrack = fileConfig?.MaxReferenceAddressesToTrack ?? DefaultMaxReferenceAddressesToTrack;
            int referenceChainTopCount = fileConfig?.ReferenceChainTopCount ?? DefaultReferenceChainTopCount;
            int eventLeakMinSubscribers = fileConfig?.EventLeakMinSubscribers ?? DefaultEventLeakMinSubscribers;
            bool enableMemoryDiagnostics = fileConfig?.EnableMemoryDiagnostics ?? false;
            bool enablePerformanceDiagnostics = fileConfig?.EnablePerformanceDiagnostics ?? false;
            ReportFormat reportFormat = ReportFormat.Html;
            bool waitForKeyPressOnComplete = fileConfig?.WaitForKeyPressOnComplete ?? true;
            bool forceGCBetweenStages = fileConfig?.ForceGCBetweenStages ?? false;
            string[]? symbolPaths = fileConfig?.SymbolPaths;
            string? symbolCachePath = fileConfig?.SymbolCachePath;

            // CLI options are considered for report format always and for analyzer settings when config is missing.
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (!arg.StartsWith("--", StringComparison.Ordinal) ||
                    arg.StartsWith(ConfigFileOption, StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith(BaselineDumpOption, StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith(TrendDumpsOption, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (arg.StartsWith(ReportFormatOption, StringComparison.OrdinalIgnoreCase))
                {
                    reportFormat = ParseReportFormatOption(arg, ReportFormatOption.TrimEnd('='));
                    continue;
                }

                if (hasFileConfig)
                {
                    continue;
                }

                if (arg.StartsWith(HighReferenceThresholdOption, StringComparison.OrdinalIgnoreCase))
                {
                    highReferenceThreshold = ParsePositiveIntOption(arg, HighReferenceThresholdOption.TrimEnd('='));
                }
                else if (arg.StartsWith(MaxDuplicateStringLengthOption, StringComparison.OrdinalIgnoreCase))
                {
                    maxDuplicateStringLength = ParsePositiveIntOption(arg, MaxDuplicateStringLengthOption.TrimEnd('='));
                }
                else if (arg.StartsWith(MinDuplicateStringCountOption, StringComparison.OrdinalIgnoreCase))
                {
                    minDuplicateStringCount = ParsePositiveIntOption(arg, MinDuplicateStringCountOption.TrimEnd('='));
                }
                else if (arg.StartsWith(MaxReferenceAddressesOption, StringComparison.OrdinalIgnoreCase))
                {
                    maxReferenceAddressesToTrack = ParsePositiveIntOption(arg, MaxReferenceAddressesOption.TrimEnd('='));
                }
                else if (arg.StartsWith(ReferenceChainTopCountOption, StringComparison.OrdinalIgnoreCase))
                {
                    referenceChainTopCount = ParsePositiveIntOption(arg, ReferenceChainTopCountOption.TrimEnd('='));
                }
                else if (arg.StartsWith(EventLeakMinSubscribersOption, StringComparison.OrdinalIgnoreCase))
                {
                    eventLeakMinSubscribers = ParseNonNegativeIntOption(arg, EventLeakMinSubscribersOption.TrimEnd('='));
                }
                else if (arg.Equals(EnableMemoryDiagnosticsOption, StringComparison.OrdinalIgnoreCase))
                {
                    enableMemoryDiagnostics = true;
                }
                else if (arg.Equals(EnablePerformanceDiagnosticsOption, StringComparison.OrdinalIgnoreCase))
                {
                    enablePerformanceDiagnostics = true;
                }
                else
                {
                    throw new ArgumentException($"Unknown option '{arg}'.");
                }
            }

            string outputPath = BuildOutputPath(dumpPath, reportFormat);

            return new AnalysisConfiguration
            {
                DumpPath = dumpPath,
                OutputPath = outputPath,
                BaselineDumpPath = baselineDumpPath,
                TrendDumpPaths = trendDumpPaths,
                HighReferenceThreshold = highReferenceThreshold,
                MaxDuplicateStringLength = maxDuplicateStringLength,
                MinDuplicateStringCount = minDuplicateStringCount,
                MaxReferenceAddressesToTrack = maxReferenceAddressesToTrack,
                ReferenceChainTopCount = referenceChainTopCount,
                EventLeakMinSubscribers = eventLeakMinSubscribers,
                EnableMemoryDiagnostics = enableMemoryDiagnostics,
                EnablePerformanceDiagnostics = enablePerformanceDiagnostics,
                ReportFormat = reportFormat,
                WaitForKeyPressOnComplete = waitForKeyPressOnComplete,
                ForceGCBetweenStages = forceGCBetweenStages,
                SymbolPaths = symbolPaths,
                SymbolCachePath = symbolCachePath
            };
        }

        private static AnalysisConfigurationFileModel LoadConfigurationFile(string configPath)
        {
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException($"Config file not found at '{configPath}'", configPath);
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            string json = File.ReadAllText(configPath);
            AnalysisConfigurationFileModel? config = JsonSerializer.Deserialize<AnalysisConfigurationFileModel>(json, options);

            if (config == null)
            {
                throw new ArgumentException($"Config file '{configPath}' is empty or invalid.");
            }

            return config;
        }

        private static string? ResolveConfigPath(string? cliConfigPath)
        {
            if (!string.IsNullOrWhiteSpace(cliConfigPath))
            {
                return File.Exists(cliConfigPath) ? cliConfigPath : null;
            }

            string baseDirectory = AppContext.BaseDirectory;
            string primaryPath = Path.Combine(baseDirectory, DefaultConfigFileName);
            if (File.Exists(primaryPath))
            {
                return primaryPath;
            }

            string samplePath = Path.Combine(baseDirectory, FallbackSampleConfigFileName);
            return File.Exists(samplePath) ? samplePath : null;
        }

        private static ReportFormat ParseReportFormatOption(string arg, string optionName)
        {
            int separatorIndex = arg.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == arg.Length - 1)
            {
                throw new ArgumentException($"Option '{optionName}' requires a value in the format '{optionName}=<text|markdown|html>'.");
            }

            string value = arg[(separatorIndex + 1)..].Trim();
            return value.ToLowerInvariant() switch
            {
                "text" or "txt" => ReportFormat.Text,
                "markdown" or "md" => ReportFormat.Markdown,
                "html" or "htm" => ReportFormat.Html,
                _ => throw new ArgumentException($"Option '{optionName}' value '{value}' is invalid. Expected one of: text, markdown, html.")
            };
        }

        private static string ParseStringOption(string arg, string optionName)
        {
            int separatorIndex = arg.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == arg.Length - 1)
            {
                throw new ArgumentException($"Option '{optionName}' requires a value in the format '{optionName}=<value>'.");
            }

            return arg[(separatorIndex + 1)..].Trim();
        }

        private static ReportFormat InferReportFormatFromOutputPath(string outputPath)
        {
            string extension = Path.GetExtension(outputPath);
            return extension.ToLowerInvariant() switch
            {
                ".md" or ".markdown" => ReportFormat.Markdown,
                ".html" or ".htm" => ReportFormat.Html,
                _ => ReportFormat.Text
            };
        }

        private static string BuildOutputPath(string dumpPath, ReportFormat reportFormat)
        {
            string extension = reportFormat switch
            {
                ReportFormat.Markdown => ".md",
                ReportFormat.Text => ".txt",
                _ => ".html"
            };

            return Path.ChangeExtension(dumpPath, extension);
        }

        private static int ParsePositiveIntOption(string arg, string optionName)
        {
            int separatorIndex = arg.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == arg.Length - 1)
            {
                throw new ArgumentException($"Option '{optionName}' requires a value in the format '{optionName}=<positive-int>'.");
            }

            string value = arg[(separatorIndex + 1)..];
            if (!int.TryParse(value, out int parsedValue) || parsedValue <= 0)
            {
                throw new ArgumentException($"Option '{optionName}' value '{value}' is invalid. Expected a positive integer.");
            }

            return parsedValue;
        }

        private static int ParseNonNegativeIntOption(string arg, string optionName)
        {
            int separatorIndex = arg.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == arg.Length - 1)
            {
                throw new ArgumentException($"Option '{optionName}' requires a value in the format '{optionName}=<non-negative-int>'.");
            }

            string value = arg[(separatorIndex + 1)..];
            if (!int.TryParse(value, out int parsedValue) || parsedValue < 0)
            {
                throw new ArgumentException($"Option '{optionName}' value '{value}' is invalid. Expected a non-negative integer.");
            }

            return parsedValue;
        }

        public void PrintConfiguration()
        {
            Console.WriteLine($"Analyzing dump: {DumpPath}");
            if (!string.IsNullOrWhiteSpace(BaselineDumpPath))
            {
                Console.WriteLine($"Comparing against baseline dump: {BaselineDumpPath}");
            }
            if (TrendDumpPaths is { Count: > 0 })
            {
                Console.WriteLine($"Trend mode enabled with {TrendDumpPaths.Count} historical dump(s).");
            }
            if (OutputPath != null)
            {
                Console.WriteLine($"Output will be written to: {OutputPath}");
            }
            Console.WriteLine("Memory leak analyzer settings:");
            Console.WriteLine($"  HighReferenceThreshold: {HighReferenceThreshold:N0}");
            Console.WriteLine($"  MaxDuplicateStringLength: {MaxDuplicateStringLength:N0}");
            Console.WriteLine($"  MinDuplicateStringCount: {MinDuplicateStringCount:N0}");
            Console.WriteLine($"  MaxReferenceAddressesToTrack: {MaxReferenceAddressesToTrack:N0}");
            Console.WriteLine("General analyzer settings:");
            Console.WriteLine($"  ReferenceChainTopCount: {ReferenceChainTopCount:N0}");
            Console.WriteLine($"  EventLeakMinSubscribers: {EventLeakMinSubscribers:N0}");
            Console.WriteLine($"  MemoryDiagnostics: {(EnableMemoryDiagnostics ? "Enabled" : "Disabled (default)")}");
            Console.WriteLine($"  PerformanceDiagnostics: {(EnablePerformanceDiagnostics ? "Enabled" : "Disabled (default)")}");
            Console.WriteLine($"  ReportFormat: {ReportFormat}");
            Console.WriteLine();
        }

        private sealed class AnalysisConfigurationFileModel
        {
            public int? ReferenceChainTopCount { get; init; }
            public int? EventLeakMinSubscribers { get; init; }
            public bool? EnableMemoryDiagnostics { get; init; }
            public bool? EnablePerformanceDiagnostics { get; init; }
            public bool? WaitForKeyPressOnComplete { get; init; }
            public bool? ForceGCBetweenStages { get; init; }
            public int? HighReferenceThreshold { get; init; }
            public int? MaxDuplicateStringLength { get; init; }
            public int? MinDuplicateStringCount { get; init; }
            public int? MaxReferenceAddressesToTrack { get; init; }
            public string[]? SymbolPaths { get; init; }
            public string? SymbolCachePath { get; init; }
        }

        private static List<string> ParseDumpListOption(string arg, string optionName)
        {
            string value = ParseStringOption(arg, optionName);
            var result = value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (result.Count == 0)
            {
                throw new ArgumentException($"Option '{optionName}' requires one or more dump paths separated by ';'.");
            }

            return result;
        }
    }
}
