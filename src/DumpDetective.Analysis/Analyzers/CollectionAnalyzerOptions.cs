using System;

namespace DumpDetective.Analysis.Analyzers
{
    public enum AnalysisProfile
    {
        Fast = 0,
        Balanced = 1,
        Deep = 2,
    }

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
        public ulong WasteThresholdBytes { get; init; } = 1 * 1024UL;

        /// <summary>
        /// <summary>
        /// NOTE: Summary warning thresholds are report-level concerns and have been moved
        /// to the findings generator options. This property was intentionally removed from
        /// analyzer options to avoid mixing analysis configuration with reporting thresholds.
        /// </summary>
        // SummaryWarnThresholdBytes removed; reporting thresholds live in CollectionFindingGeneratorOptions

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
        /// Analysis profile controls the depth and cost of additional diagnostics such as
        /// shortest-root-path searches. Fast = cheapest, Balanced = targeted deep search for top items,
        /// Deep = more exhaustive searches for top items.
        /// </summary>
        public AnalysisProfile Profile { get; init; } = AnalysisProfile.Balanced;

        /// <summary>
        /// Number of top wasteful items to run reference-path analysis for when the profile
        /// is not <see cref="AnalysisProfile.Fast"/>. Defaults to 5.
        /// </summary>
        public int PathAnalysisTopN { get; init; } = 5;

        /// <summary>
        /// Reference-chain search options used when running targeted path searches.
        /// Consumers may customize budgets for balanced/deep searches here.
        /// </summary>
        public DumpDetective.Core.Options.ReferenceChainOptions ReferenceChainOptions { get; init; } = new();

        /// <summary>
        /// If true, serialize accesses to the ClrHeap APIs (e.g., GetObject) to avoid
        /// potential thread-safety issues when running parallel heap scans. Default false.
        /// </summary>
        public bool SerializeHeapAccess { get; init; } = false;

        /// <summary>
        /// Default options instance with recommended values matching the original analyzer.
        /// Consumers may clone/modify this instance when invoking the analyzer.
        /// </summary>
        public static CollectionAnalyzerOptions Default { get; } = new();
    }
}
