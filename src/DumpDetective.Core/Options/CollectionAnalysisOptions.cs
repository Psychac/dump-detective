using System;

namespace DumpDetective.Core.Options
{
    public enum AnalysisProfile
    {
        Fast = 0,
        Balanced = 1,
        Full = 2,
        Deep = Full,
    }

    /// <summary>
    /// Options that control how the <see cref="DumpDetective.Analysis.Analyzers.CollectionAnalyzer"/> runs and reports findings.
    /// This class centralizes thresholds and performance-related configuration so they can be
    /// provided by callers (CLI, tests or higher-level orchestration) instead of hard-coded constants.
    /// </summary>
    public sealed class CollectionAnalysisOptions
    {
        /// <summary>
        /// Threshold (in bytes) under which an individual collection is considered "wasteful".
        /// Default is 10 KB to match current heuristics.
        /// </summary>
        public ulong WasteThresholdBytes { get; init; } = 10 * 1024UL;

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
        public int TopWastefulCollectionsToShow { get; init; } = 50;

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
        /// is not <see cref="AnalysisProfile.Fast"/>.
        /// </summary>
        public int PathAnalysisTopN { get; init; } = 5;

        /// <summary>
        /// Reference-chain search options used when running targeted path searches.
        /// Consumers may customize budgets for balanced/deep searches here.
        /// </summary>
        public ReferenceChainOptions ReferenceChainOptions { get; init; } = new();

        /// <summary>
        /// If true, serialize accesses to the ClrHeap APIs (e.g., GetObject) to avoid
        /// potential thread-safety issues when running parallel heap scans. Default false.
        /// </summary>
        public bool SerializeHeapAccess { get; init; } = false;

        /// <summary>
        public static CollectionAnalysisOptions Preset(AnalysisProfile profile) => profile switch
        {
            AnalysisProfile.Fast => new CollectionAnalysisOptions
            {
                WasteThresholdBytes = 10 * 1024UL,
                TopWastefulCollectionsToShow = 25,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                IncludeQueueAnalysis = true,
                SurfaceProbingExceptions = false,
                Profile = AnalysisProfile.Fast,
                PathAnalysisTopN = 0,
                ReferenceChainOptions = new ReferenceChainOptions
                {
                    SearchMode = ReferenceChainSearchMode.Fast,
                    TopCount = 5,
                    MaxPathDepth = 15
                },
                SerializeHeapAccess = false
            },
            AnalysisProfile.Full => new CollectionAnalysisOptions
            {
                WasteThresholdBytes = 10 * 1024UL,
                TopWastefulCollectionsToShow = 100,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                IncludeQueueAnalysis = true,
                SurfaceProbingExceptions = false,
                Profile = AnalysisProfile.Full,
                PathAnalysisTopN = 15,
                ReferenceChainOptions = new ReferenceChainOptions
                {
                    SearchMode = ReferenceChainSearchMode.Deep,
                    TopCount = 15,
                    MaxPathDepth = 40
                },
                SerializeHeapAccess = false
            },
            _ => new CollectionAnalysisOptions
            {
                WasteThresholdBytes = 10 * 1024UL,
                TopWastefulCollectionsToShow = 50,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                IncludeQueueAnalysis = true,
                SurfaceProbingExceptions = false,
                Profile = AnalysisProfile.Balanced,
                PathAnalysisTopN = 5,
                ReferenceChainOptions = new ReferenceChainOptions
                {
                    SearchMode = ReferenceChainSearchMode.Balanced,
                    TopCount = 10,
                    MaxPathDepth = 25
                },
                SerializeHeapAccess = false
            }
        };

        public static CollectionAnalysisOptions ApplyOverrides(CollectionAnalysisOptions @base, CollectionAnalysisOptionsModel? model)
        {
            if (model is null)
                return @base;

            return new CollectionAnalysisOptions
            {
                WasteThresholdBytes = model.WasteThresholdBytes ?? @base.WasteThresholdBytes,
                TopWastefulCollectionsToShow = model.TopWastefulCollectionsToShow ?? @base.TopWastefulCollectionsToShow,
                MaxDegreeOfParallelism = model.MaxDegreeOfParallelism ?? @base.MaxDegreeOfParallelism,
                IncludeQueueAnalysis = model.IncludeQueueAnalysis ?? @base.IncludeQueueAnalysis,
                SurfaceProbingExceptions = model.SurfaceProbingExceptions ?? @base.SurfaceProbingExceptions,
                Profile = @base.Profile,
                PathAnalysisTopN = model.PathAnalysisTopN ?? @base.PathAnalysisTopN,
                ReferenceChainOptions = model.ReferenceChainOptions ?? @base.ReferenceChainOptions,
                SerializeHeapAccess = model.SerializeHeapAccess ?? @base.SerializeHeapAccess
            };
        }

        public static CollectionAnalysisOptions Validate(CollectionAnalysisOptions options)
        {
            static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

            int maxDegree = options.MaxDegreeOfParallelism <= 0 ? 1 : options.MaxDegreeOfParallelism;
            int topWasteful = Clamp(options.TopWastefulCollectionsToShow, 1, 10_000);
            int pathTopN = options.PathAnalysisTopN < 0 ? 0 : options.PathAnalysisTopN;
            ulong wasteThreshold = options.WasteThresholdBytes == 0 ? 1 : options.WasteThresholdBytes;

            return new CollectionAnalysisOptions
            {
                WasteThresholdBytes = wasteThreshold,
                TopWastefulCollectionsToShow = topWasteful,
                MaxDegreeOfParallelism = maxDegree,
                IncludeQueueAnalysis = options.IncludeQueueAnalysis,
                SurfaceProbingExceptions = options.SurfaceProbingExceptions,
                Profile = options.Profile,
                PathAnalysisTopN = pathTopN,
                ReferenceChainOptions = options.ReferenceChainOptions,
                SerializeHeapAccess = options.SerializeHeapAccess
            };
        }

        /// <summary>
        /// Default options instance with recommended values matching the Balanced preset.
        /// Consumers may clone/modify this instance when invoking the analyzer.
        /// </summary>
        public static CollectionAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
    }

    public sealed class CollectionAnalysisOptionsModel
    {
        public string? Profile { get; init; }
        public ulong? WasteThresholdBytes { get; init; }
        public int? TopWastefulCollectionsToShow { get; init; }
        public int? MaxDegreeOfParallelism { get; init; }
        public bool? IncludeQueueAnalysis { get; init; }
        public bool? SurfaceProbingExceptions { get; init; }
        public int? PathAnalysisTopN { get; init; }
        public ReferenceChainOptions? ReferenceChainOptions { get; init; }
        public bool? SerializeHeapAccess { get; init; }
    }
}
