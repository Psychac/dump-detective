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
        IEnumerable<IFindingGenerator> findingGenerators,
        IEnumerable<IAnalyzerTrendComparer> trendComparers,
        ISectionBuilderFactory sectionBuilderFactory)
    {
        var errors = new List<string>();

        IReadOnlyList<IAnalyzerSectionBuilder> analyzerSectionBuilders = sectionBuilderFactory.CreateAnalyzerBuilders();
        IReadOnlyList<IReportSectionBuilder> reportSectionBuilders = sectionBuilderFactory.CreateReportBuilders();

        if (reportSectionBuilders.Count == 0)
            errors.Add("No report section builders are registered.");

        // A retired analyzer (Phase 5 step 3) has no IFindingGenerator at all — its module's
        // SynthesisRuleType is its only path to a Finding, via SynthesisFindingEnricher rather
        // than this DI-registered set. AnalyzerFeatureModule.DisplayName is what both
        // IAnalyzer.Name and IFindingGenerator.AnalyzerName are conventionally set to (verified by
        // PluginCatalogBuilderTests), so it's the right key to join on here.
        IEnumerable<string> retiredButSynthesisCovered = moduleCatalog.Modules
            .Where(m => m.FindingGeneratorType is null && m.SynthesisRuleType is not null)
            .Select(m => m.DisplayName);

        ValidateNameCoverage(
            "finding generators",
            analyzers.Select(a => a.Name),
            findingGenerators.Select(g => g.AnalyzerName).Concat(retiredButSynthesisCovered),
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
