namespace DumpDetective.Configuration
{
    internal class AnalysisConfiguration
    {
        public required string DumpPath { get; init; }
        public string? OutputPath { get; init; }
        public int ReferenceChainTopCount { get; init; } = 5;
        public int EventLeakMinSubscribers { get; init; } = 0;
        public bool EnableMemoryDiagnostics { get; init; } = true;
        public bool WaitForKeyPressOnComplete { get; init; } = true;
        public bool ForceGCBetweenStages { get; init; } = false;

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

            return new AnalysisConfiguration
            {
                DumpPath = dumpPath,
                OutputPath = outputPath
            };
        }

        public void PrintConfiguration()
        {
            Console.WriteLine($"Analyzing dump: {DumpPath}");
            if (OutputPath != null)
            {
                Console.WriteLine($"Output will be written to: {OutputPath}");
            }
            Console.WriteLine();
        }
    }
}
