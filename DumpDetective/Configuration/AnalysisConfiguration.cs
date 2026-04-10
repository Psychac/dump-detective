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

        /// <summary>
        /// Full path to the dump file to analyze.
        /// </summary>
        public required string DumpPath { get; init; }

        /// <summary>
        /// Optional output report path. When null, results are written only to console.
        /// </summary>
        public string? OutputPath { get; init; }

        /// <summary>
        /// Reference chain analyzer: number of top memory-consuming types to analyze.
        /// </summary>
        public int ReferenceChainTopCount { get; init; } = DefaultReferenceChainTopCount;

        /// <summary>
        /// Event leak analyzer: minimum subscriber count to report a leak candidate.
        /// </summary>
        public int EventLeakMinSubscribers { get; init; } = DefaultEventLeakMinSubscribers;
        public bool EnableMemoryDiagnostics { get; init; } = false;
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
            if (args.Length == 0)
            {
                throw new ArgumentException("Dump file path is required");
            }

            string dumpPath = args[0];
            string? outputPath = null;
            int optionStartIndex = 1;

            if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
            {
                outputPath = args[1];
                optionStartIndex = 2;
            }

            if (!File.Exists(dumpPath))
            {
                throw new FileNotFoundException($"Dump file not found at '{dumpPath}'", dumpPath);
            }

            int highReferenceThreshold = DefaultHighReferenceThreshold;
            int maxDuplicateStringLength = DefaultMaxDuplicateStringLength;
            int minDuplicateStringCount = DefaultMinDuplicateStringCount;
            int maxReferenceAddressesToTrack = DefaultMaxReferenceAddressesToTrack;
            int referenceChainTopCount = DefaultReferenceChainTopCount;
            int eventLeakMinSubscribers = DefaultEventLeakMinSubscribers;
            bool enableMemoryDiagnostics = false;

            for (int i = optionStartIndex; i < args.Length; i++)
            {
                string arg = args[i];

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
                else
                {
                    throw new ArgumentException($"Unknown option '{arg}'.");
                }
            }

            return new AnalysisConfiguration
            {
                DumpPath = dumpPath,
                OutputPath = outputPath,
                HighReferenceThreshold = highReferenceThreshold,
                MaxDuplicateStringLength = maxDuplicateStringLength,
                MinDuplicateStringCount = minDuplicateStringCount,
                MaxReferenceAddressesToTrack = maxReferenceAddressesToTrack,
                ReferenceChainTopCount = referenceChainTopCount,
                EventLeakMinSubscribers = eventLeakMinSubscribers,
                EnableMemoryDiagnostics = enableMemoryDiagnostics
            };
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
            Console.WriteLine();
        }
    }
}
