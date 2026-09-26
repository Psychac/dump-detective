using DumpDetective.Cli.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Capabilities;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Analysis;

using DumpDetective.Core.Options;

namespace DumpDetective.Cli.Services;

internal sealed class StartupValidator
{
    public void ValidateRegistrations(
        IReadOnlyList<IAnalyzer> analyzers,
        IAnalyzerFeatureModuleCatalog moduleCatalog,
        IEnumerable<IAnalyzerTrendComparer> trendComparers,
        ISectionBuilderFactory sectionBuilderFactory)
    {
        var errors = new List<string>();

        IReadOnlyList<IAnalyzerSectionBuilder> analyzerSectionBuilders = sectionBuilderFactory.CreateAnalyzerBuilders();
        IReadOnlyList<IReportSectionBuilder> reportSectionBuilders = sectionBuilderFactory.CreateReportBuilders();

        if (reportSectionBuilders.Count == 0)
            errors.Add("No report section builders are registered.");

        // Joined against the real IAnalyzer instances by AnalyzerType, not
        // AnalyzerFeatureModule.DisplayName — DisplayName is a report-facing label that can
        // legitimately differ from IAnalyzer.Name (ThreadStackClusterAnalyzer: "Thread Stack Cluster
        // Analysis" vs. "Thread Stack Signature Clustering").
        IEnumerable<string> synthesisCovered = moduleCatalog.Modules
            .Join(analyzers, m => m.AnalyzerType, a => a.GetType(), (_, a) => a.Name);

        ValidateNameCoverage(
            "synthesis rules",
            analyzers.Select(a => a.Name),
            synthesisCovered,
            errors,
            requireEveryAnalyzer: true);

        // A retired trend comparer (Phase 5 step 4) has no IAnalyzerTrendComparer at all — its
        // headline metrics are covered by TrendAnalyzer's ObservationTrendDiffer-driven path
        // instead, via ObservationDifferAnalyzerOwnership. Unlike the SynthesisRule
        // case there's no per-analyzer companion type recorded on the module to check here, so this
        // reads DisplayName directly off Analysis.Trend.ObservationDifferAnalyzerOwnership rather
        // than off moduleCatalog.
        IEnumerable<string> retiredButObservationDifferCovered =
            DumpDetective.Analysis.Trend.ObservationDifferAnalyzerOwnership.ObservationTypeToAnalyzerName.Values.Distinct(StringComparer.Ordinal);

        ValidateNameCoverage(
            "trend comparers",
            analyzers.Select(a => a.Name),
            trendComparers.Select(c => c.AnalyzerName).Concat(retiredButObservationDifferCovered),
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

        var overlap = options.IncludeAnalyzers.Intersect(options.ExcludeAnalyzers, StringComparer.OrdinalIgnoreCase);
        if (overlap.Any())
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

        var missingOrdered = expected.Where(name => !registered.Contains(name)).OrderBy(name => name, StringComparer.Ordinal);
        var extraOrdered = registered.Where(name => !expected.Contains(name)).OrderBy(name => name, StringComparer.Ordinal);

        if (requireEveryAnalyzer && missingOrdered.Any())
            errors.Add($"Missing {label} for analyzers: {string.Join(", ", missingOrdered)}");

        if (extraOrdered.Any())
            errors.Add($"Registered {label} without matching analyzer: {string.Join(", ", extraOrdered)}");
    }

}
