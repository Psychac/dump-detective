using System;

namespace DumpDetective.Core.Options
{
    /// <summary>
    /// Options that control the behavior and payload limits of the CrashAnalyzer.
    /// Follows the pattern used by other analyzers (e.g., CollectionAnalysisOptions).
    /// </summary>
    public sealed class CrashAnalysisOptions
    {
        public int MaxExceptionsPerType { get; init; } = 10;
        public int TopExceptionTypesToInclude { get; init; } = 10;
        public int MaxDetailedExceptionsPerType { get; init; } = 5;
        public int MaxOriginalStackFramesToPrint { get; init; } = 20;
        public int MaxCurrentThreadFramesToPrint { get; init; } = 5;
        public int TopCrashThreadCandidates { get; init; } = 5;
        public int TopDetailedExceptionInstances { get; init; } = 25;

        /// <summary>
        /// When true, analyzer will include full type lists and details in the domain result
        /// payload. The report renderer may choose to only display the top-N types.
        /// Default true to prefer sending maximal data to the report and let the client filter.
        /// </summary>
        public bool IncludeAllTypesInPayload { get; init; } = true;

        /// <summary>
        /// Default options instance with recommended values matching the original analyzer.
        /// </summary>
        public static CrashAnalysisOptions Default { get; } = new();
    }
}
