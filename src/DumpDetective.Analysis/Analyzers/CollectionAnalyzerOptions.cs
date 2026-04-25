using System;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Options that control how the <see cref="CollectionAnalyzer"/> runs and reports findings.
    /// This class centralizes thresholds and performance-related configuration so they can be
    /// provided by callers (CLI, tests or higher-level orchestration) instead of hard-coded constants.
    /// </summary>
    public sealed class CollectionAnalyzerOptions
    {
        /// <summary>
        /// Threshold (in bytes) under which an individual collection is considered "wasteful".
        /// Default is 10 KB to match current heuristics.
        /// </summary>
        public ulong WasteThresholdBytes { get; init; } = 10 * 1024UL;

        /// <summary>
        /// Total wasted memory (sum of reported wasteful collections) above which the analyzer
        /// should surface a summary warning. Default is 10 MB.
        /// </summary>
        public ulong SummaryWarnThresholdBytes { get; init; } = 10 * 1024UL * 1024UL;

        /// <summary>
        /// Number of top wasteful collections to include in the short report.
        /// Default is 15.
        /// </summary>
        public int TopWastefulCollectionsToShow { get; init; } = 15;

        /// <summary>
        /// Maximum degree of parallelism to use during heap scanning. Default is
        /// <see cref="Environment.ProcessorCount"/> which balances CPU usage and throughput.
        /// Set to 1 to force sequential execution.
        /// </summary>
        public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

        /// <summary>
        /// If true, the analyzer will attempt to include queue analysis (circular buffer) in addition
        /// to lists/dictionaries/hashsets. This is a boolean toggle because queue analysis can be
        /// slightly more involved and may require additional CLR field fallbacks.
        /// </summary>
        public bool IncludeQueueAnalysis { get; init; } = true;

        /// <summary>
        /// If true, exceptions thrown while probing objects will be recorded or surfaced (depends
        /// on host integration). If false, probing errors are silently ignored. Default false
        /// preserves existing behavior but callers are encouraged to enable logging.
        /// </summary>
        public bool SurfaceProbingExceptions { get; init; } = false;

        /// <summary>
        /// Default options instance with recommended values matching the original analyzer.
        /// Consumers may clone/modify this instance when invoking the analyzer.
        /// </summary>
        public static CollectionAnalyzerOptions Default { get; } = new();
    }
}
