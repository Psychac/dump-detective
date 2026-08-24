using System;

namespace DumpDetective.Core.Options
{
    /// <summary>
    /// Options that control how the <see cref="DumpDetective.Analysis.Analyzers.CollectionAnalyzer"/> runs and reports findings.
    /// This class centralizes thresholds and performance-related configuration so they can be
    /// provided by callers (CLI, tests or higher-level orchestration) instead of hard-coded constants.
    /// </summary>
    public sealed class CollectionAnalysisOptions
    {
        /// <summary>
        /// Threshold (in bytes) under which an individual collection is considered "wasteful".
        /// </summary>
        public ulong WasteThresholdBytes { get; init; } = 10 * 1024UL;

        /// <summary>
        /// Bounded top-K capacity used during the streaming heap scan itself (see
        /// <c>CollectionAnalyzer.AddToTopWasteful</c>) — not a post-hoc display truncation of an
        /// already-complete list. The scan can't retain every wasteful collection found across a
        /// 25GB heap, so this genuinely bounds in-scan memory/work, alongside
        /// <see cref="PathAnalysisTopN"/> (see §9.17 implementation notes in
        /// docs/refactor/analysis-profile-removal-plan.md for why this stayed a Category-5 kept
        /// threshold rather than moving to the render layer).
        /// </summary>
        public int TopWastefulCollectionsToShow { get; init; } = 50;

        /// <summary>
        /// Maximum degree of parallelism to use during heap scanning. Default is
        /// <see cref="Environment.ProcessorCount"/> which balances CPU usage and throughput.
        /// Set to 1 to force sequential execution.
        /// </summary>
        public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

        /// <summary>
        /// If true, exceptions thrown while probing objects will be recorded or surfaced (depends
        /// on host integration). If false, probing errors are silently ignored. Default false
        /// preserves existing behavior but callers are encouraged to enable logging.
        /// </summary>
        public bool SurfaceProbingExceptions { get; init; } = false;

        /// <summary>
        /// Number of top wasteful items (by shallow waste) to run root-path search for. Bounds a
        /// real per-item <c>RootPathFinder</c> search, not a display row count — see
        /// <see cref="TopWastefulCollectionsToShow"/>'s remarks.
        /// </summary>
        public int PathAnalysisTopN { get; init; } = 5;

        /// <summary>
        /// If true, serialize accesses to the ClrHeap APIs (e.g., GetObject) to avoid
        /// potential thread-safety issues when running parallel heap scans. Default false.
        /// </summary>
        public bool SerializeHeapAccess { get; init; } = false;

        public static CollectionAnalysisOptions ApplyOverrides(CollectionAnalysisOptions @base, CollectionAnalysisOptionsModel? model)
        {
            if (model is null)
                return @base;

            return new CollectionAnalysisOptions
            {
                WasteThresholdBytes = model.WasteThresholdBytes ?? @base.WasteThresholdBytes,
                TopWastefulCollectionsToShow = model.TopWastefulCollectionsToShow ?? @base.TopWastefulCollectionsToShow,
                MaxDegreeOfParallelism = model.MaxDegreeOfParallelism ?? @base.MaxDegreeOfParallelism,
                SurfaceProbingExceptions = model.SurfaceProbingExceptions ?? @base.SurfaceProbingExceptions,
                PathAnalysisTopN = model.PathAnalysisTopN ?? @base.PathAnalysisTopN,
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
                SurfaceProbingExceptions = options.SurfaceProbingExceptions,
                PathAnalysisTopN = pathTopN,
                SerializeHeapAccess = options.SerializeHeapAccess
            };
        }
    }

    public sealed class CollectionAnalysisOptionsModel
    {
        public ulong? WasteThresholdBytes { get; init; }
        public int? TopWastefulCollectionsToShow { get; init; }
        public int? MaxDegreeOfParallelism { get; init; }
        public bool? SurfaceProbingExceptions { get; init; }
        public int? PathAnalysisTopN { get; init; }
        public bool? SerializeHeapAccess { get; init; }
    }
}
