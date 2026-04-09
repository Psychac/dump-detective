namespace DumpDetective.Configuration
{
    internal class AnalysisConfiguration
    {
        /// <summary>
        /// Full path to the dump file to analyze.
        /// </summary>
        public required string DumpPath { get; init; }

        /// <summary>
        /// Optional output report path. When null, results are written only to console.
        /// </summary>
        public string? OutputPath { get; init; }

        public int ReferenceChainTopCount { get; init; } = 5;
        public int EventLeakMinSubscribers { get; init; } = 0;
        public bool EnableMemoryDiagnostics { get; init; } = true;
        public bool WaitForKeyPressOnComplete { get; init; } = true;
        public bool ForceGCBetweenStages { get; init; } = false;

        /// <summary>
        /// Memory leak analyzer: minimum incoming references to consider an object highly referenced.
        /// </summary>
        public int HighReferenceThreshold { get; init; } = 50;

        /// <summary>
        /// Memory leak analyzer: maximum string length to include in duplicate-string tracking.
        /// </summary>
        public int MaxDuplicateStringLength { get; init; } = 500;

        /// <summary>
        /// Memory leak analyzer: minimum duplicate count before a string is reported.
        /// </summary>
        public int MinDuplicateStringCount { get; init; } = 10;

        /// <summary>
        /// Memory leak analyzer: cap on unique referenced addresses tracked to bound memory usage.
        /// </summary>
        public int MaxReferenceAddressesToTrack { get; init; } = 1_000_000;

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
            string? outputPath = args.Length > 1 ? args[1] : null;

            if (!File.Exists(dumpPath))
            {
                throw new FileNotFoundException($"Dump file not found at '{dumpPath}'", dumpPath);
            }

            int highReferenceThreshold = 50;
            int maxDuplicateStringLength = 500;
            int minDuplicateStringCount = 10;
            int maxReferenceAddressesToTrack = 1_000_000;

            for (int i = 2; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg.StartsWith("--high-reference-threshold=", StringComparison.OrdinalIgnoreCase))
                {
                    highReferenceThreshold = ParsePositiveIntOption(arg, "--high-reference-threshold");
                }
                else if (arg.StartsWith("--max-duplicate-string-length=", StringComparison.OrdinalIgnoreCase))
                {
                    maxDuplicateStringLength = ParsePositiveIntOption(arg, "--max-duplicate-string-length");
                }
                else if (arg.StartsWith("--min-duplicate-string-count=", StringComparison.OrdinalIgnoreCase))
                {
                    minDuplicateStringCount = ParsePositiveIntOption(arg, "--min-duplicate-string-count");
                }
                else if (arg.StartsWith("--max-reference-addresses=", StringComparison.OrdinalIgnoreCase))
                {
                    maxReferenceAddressesToTrack = ParsePositiveIntOption(arg, "--max-reference-addresses");
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
                MaxReferenceAddressesToTrack = maxReferenceAddressesToTrack
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
            Console.WriteLine();
        }
    }
}
