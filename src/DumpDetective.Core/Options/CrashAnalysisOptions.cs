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

        public static CrashAnalysisOptions Preset(AnalysisProfile profile) => profile switch
        {
            AnalysisProfile.Fast => new CrashAnalysisOptions
            {
                MaxExceptionsPerType = 5,
                TopExceptionTypesToInclude = 5,
                MaxDetailedExceptionsPerType = 3,
                MaxOriginalStackFramesToPrint = 12,
                MaxCurrentThreadFramesToPrint = 4,
                TopCrashThreadCandidates = 3,
                TopDetailedExceptionInstances = 10,
                IncludeAllTypesInPayload = false
            },
            AnalysisProfile.Full => new CrashAnalysisOptions
            {
                MaxExceptionsPerType = 20,
                TopExceptionTypesToInclude = 20,
                MaxDetailedExceptionsPerType = 10,
                MaxOriginalStackFramesToPrint = 40,
                MaxCurrentThreadFramesToPrint = 10,
                TopCrashThreadCandidates = 10,
                TopDetailedExceptionInstances = 40,
                IncludeAllTypesInPayload = true
            },
            _ => new CrashAnalysisOptions
            {
                MaxExceptionsPerType = 10,
                TopExceptionTypesToInclude = 10,
                MaxDetailedExceptionsPerType = 5,
                MaxOriginalStackFramesToPrint = 20,
                MaxCurrentThreadFramesToPrint = 5,
                TopCrashThreadCandidates = 5,
                TopDetailedExceptionInstances = 25,
                IncludeAllTypesInPayload = true
            }
        };

        public static CrashAnalysisOptions ApplyOverrides(CrashAnalysisOptions @base, CrashAnalysisOptionsModel? model)
        {
            if (model is null)
                return @base;

            return new CrashAnalysisOptions
            {
                MaxExceptionsPerType = model.MaxExceptionsPerType ?? @base.MaxExceptionsPerType,
                TopExceptionTypesToInclude = model.TopExceptionTypesToInclude ?? @base.TopExceptionTypesToInclude,
                MaxDetailedExceptionsPerType = model.MaxDetailedExceptionsPerType ?? @base.MaxDetailedExceptionsPerType,
                MaxOriginalStackFramesToPrint = model.MaxOriginalStackFramesToPrint ?? @base.MaxOriginalStackFramesToPrint,
                MaxCurrentThreadFramesToPrint = model.MaxCurrentThreadFramesToPrint ?? @base.MaxCurrentThreadFramesToPrint,
                TopCrashThreadCandidates = model.TopCrashThreadCandidates ?? @base.TopCrashThreadCandidates,
                TopDetailedExceptionInstances = model.TopDetailedExceptionInstances ?? @base.TopDetailedExceptionInstances,
                IncludeAllTypesInPayload = model.IncludeAllTypesInPayload ?? @base.IncludeAllTypesInPayload
            };
        }

        public static CrashAnalysisOptions Validate(CrashAnalysisOptions options)
        {
            static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

            int maxExceptionsPerType = Clamp(options.MaxExceptionsPerType, 1, 1000);
            int topExceptionTypesToInclude = Clamp(options.TopExceptionTypesToInclude, 1, 1000);
            int maxDetailedExceptionsPerType = Clamp(options.MaxDetailedExceptionsPerType, 1, maxExceptionsPerType);
            int maxOriginalStackFramesToPrint = Clamp(options.MaxOriginalStackFramesToPrint, 1, 500);
            int maxCurrentThreadFramesToPrint = Clamp(options.MaxCurrentThreadFramesToPrint, 1, 200);
            int topCrashThreadCandidates = Clamp(options.TopCrashThreadCandidates, 1, 1000);
            int topDetailedExceptionInstances = Clamp(options.TopDetailedExceptionInstances, 1, 5000);

            return new CrashAnalysisOptions
            {
                MaxExceptionsPerType = maxExceptionsPerType,
                TopExceptionTypesToInclude = topExceptionTypesToInclude,
                MaxDetailedExceptionsPerType = maxDetailedExceptionsPerType,
                MaxOriginalStackFramesToPrint = maxOriginalStackFramesToPrint,
                MaxCurrentThreadFramesToPrint = maxCurrentThreadFramesToPrint,
                TopCrashThreadCandidates = topCrashThreadCandidates,
                TopDetailedExceptionInstances = topDetailedExceptionInstances,
                IncludeAllTypesInPayload = options.IncludeAllTypesInPayload
            };
        }

        /// <summary>
        /// Default options instance with recommended values matching the original analyzer.
        /// </summary>
        public static CrashAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
    }

    public sealed class CrashAnalysisOptionsModel
    {
        public string? Profile { get; init; }
        public int? MaxExceptionsPerType { get; init; }
        public int? TopExceptionTypesToInclude { get; init; }
        public int? MaxDetailedExceptionsPerType { get; init; }
        public int? MaxOriginalStackFramesToPrint { get; init; }
        public int? MaxCurrentThreadFramesToPrint { get; init; }
        public int? TopCrashThreadCandidates { get; init; }
        public int? TopDetailedExceptionInstances { get; init; }
        public bool? IncludeAllTypesInPayload { get; init; }
    }
}
