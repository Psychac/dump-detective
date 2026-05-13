using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;

using DumpDetective.Core.Options;

namespace DumpDetective.Cli.Services;

internal sealed class StartupValidator
{
    public void ValidateRegistrations(
        IReadOnlyList<IAnalyzer> analyzers,
        IEnumerable<IFindingGenerator> findingGenerators,
        IEnumerable<IAnalyzerTrendComparer> trendComparers,
        ISectionBuilderFactory sectionBuilderFactory)
    {
        List<string> errors = [];

        IReadOnlyList<IAnalyzerSectionBuilder> analyzerSectionBuilders = sectionBuilderFactory.CreateAnalyzerBuilders();
        IReadOnlyList<IReportSectionBuilder> reportSectionBuilders = sectionBuilderFactory.CreateReportBuilders();

        if (reportSectionBuilders.Count == 0)
            errors.Add("No report section builders are registered.");

        ValidateNameCoverage(
            "finding generators",
            analyzers.Select(a => a.Name),
            findingGenerators.Select(g => g.AnalyzerName),
            errors,
            requireEveryAnalyzer: true);

        ValidateNameCoverage(
            "trend comparers",
            analyzers.Select(a => a.Name),
            trendComparers.Select(c => c.AnalyzerName),
            errors,
            requireEveryAnalyzer: true);

        ValidateNameCoverage(
            "analyzer section builders",
            analyzers.Select(a => a.Name),
            analyzerSectionBuilders.Select(b => b.AnalyzerName),
            errors,
            requireEveryAnalyzer: false);

        if (errors.Count > 0)
            throw new ArgumentException(string.Join(Environment.NewLine, errors));
    }

    public void Validate(ResolvedExecutionOptions options)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.DumpPath))
        {
            errors.Add("DumpPath is required.");
        }
        else if (!File.Exists(options.DumpPath))
        {
            errors.Add($"DumpPath '{options.DumpPath}' does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(options.BaselineDumpPath) && !File.Exists(options.BaselineDumpPath))
        {
            errors.Add($"BaselineDumpPath '{options.BaselineDumpPath}' does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(options.BaselineDumpPath) && options.TrendDumpPaths is { Count: > 0 })
        {
            errors.Add("BaselineDumpPath and TrendDumpPaths are mutually exclusive.");
        }

        if (options.TrendDumpPaths is { Count: > 0 })
        {
            if (options.TrendDumpPaths.Count < 2)
            {
                errors.Add("TrendDumpPaths must contain at least two dump paths.");
            }

            foreach (string trendPath in options.TrendDumpPaths)
            {
                if (!File.Exists(trendPath))
                {
                    errors.Add($"TrendDumpPath '{trendPath}' does not exist.");
                }
            }
        }

        ValidateRetentionOptions(options.MemoryLeak, errors);
        ValidateStringAnalysisOptions(options.StringAnalysis, errors);
        ValidateReferenceChainOptions(options.ReferenceChain, errors);
        ValidateEventLeakOptions(options.EventLeak, errors);

        var overlap = options.IncludeAnalyzers
            .Intersect(options.ExcludeAnalyzers, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (overlap.Count > 0)
        {
            errors.Add($"IncludeAnalyzers and ExcludeAnalyzers overlap: {string.Join(", ", overlap)}");
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, errors));
        }
    }

    private static void ValidateNameCoverage(
        string label,
        IEnumerable<string> expectedNames,
        IEnumerable<string> registeredNames,
        List<string> errors,
        bool requireEveryAnalyzer)
    {
        HashSet<string> expected = new(expectedNames, StringComparer.Ordinal);
        HashSet<string> registered = new(registeredNames, StringComparer.Ordinal);

        List<string> missing = expected.Where(name => !registered.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
        List<string> extra = registered.Where(name => !expected.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();

        if (requireEveryAnalyzer && missing.Count > 0)
            errors.Add($"Missing {label} for analyzers: {string.Join(", ", missing)}");

        if (extra.Count > 0)
            errors.Add($"Registered {label} without matching analyzer: {string.Join(", ", extra)}");
    }

    private static void ValidateRetentionOptions(RetentionOptions options, List<string> errors)
    {
        if (options.HighReferenceThreshold <= 0)
        {
            errors.Add("MemoryLeak.HighReferenceThreshold must be greater than zero.");
        }
        if (options.MaxReferenceAddresses <= 0)
        {
            errors.Add("MemoryLeak.MaxReferenceAddresses must be greater than zero.");
        }
    }

    private static void ValidateStringAnalysisOptions(DumpDetective.Core.Options.StringAnalysisOptions options, List<string> errors)
    {
        if (options.MaxDuplicateStringLength <= 0)
        {
            errors.Add("StringAnalysis.MaxDuplicateStringLength must be greater than zero.");
        }

        if (options.MinDuplicateStringCount <= 0)
        {
            errors.Add("StringAnalysis.MinDuplicateStringCount must be greater than zero.");
        }
    }

    private static void ValidateReferenceChainOptions(ReferenceChainOptions options, List<string> errors)
    {
        if (options.TopCount <= 0)
        {
            errors.Add("ReferenceChain.TopCount must be greater than zero.");
        }

        if (options.MaxPathSearchObjects <= 0)
        {
            errors.Add("ReferenceChain.MaxPathSearchObjects must be greater than zero.");
        }
    }

    private static void ValidateEventLeakOptions(EventLeakOptions options, List<string> errors)
    {
        if (options.MinSubscribers < 0)
        {
            errors.Add("EventLeak.MinSubscribers must be zero or greater.");
        }
    }
}
