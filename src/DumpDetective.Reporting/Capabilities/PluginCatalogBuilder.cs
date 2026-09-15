using System.Reflection;

using DumpDetective.Reporting.SectionBuilders;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Reporting.Capabilities;

/// <summary>
/// Builds an <see cref="IAnalyzerFeatureModuleCatalog"/> by scanning assemblies for
/// <see cref="AnalyzerModuleAttribute"/>-annotated <c>Sdk.Analysis.IAnalyzer</c> implementations —
/// the sole catalog implementation since migration step 4 deleted the hand-maintained
/// <c>DefaultAnalyzerFeatureModuleCatalog</c> — see
/// docs/refactor/modularity/phase-3-plugin-packaging.md migration steps 2 and 4.
/// </summary>
/// <remarks>
/// Each analyzer's <c>FindingGenerator</c>/<c>TrendComparer</c>/<c>SectionBuilder</c> companion
/// types are located by convention — <c>{AnalyzerName minus trailing "Analyzer"}{Suffix}</c> — not
/// declared on the attribute. Verified before this was written: 36/36 analyzers already follow this
/// convention (renamed the handful that didn't as part of the same change), so the convention
/// carries real information instead of being a rule invented to fit later.
/// </remarks>
internal sealed class PluginCatalogBuilder
{
    private static readonly Type[] GlobalReportSectionBuilderTypes =
    [
        typeof(ExecutiveSummarySectionBuilder),
        typeof(TypeSystemSectionBuilder),
        typeof(InsightsSectionBuilder),
        typeof(ConfidenceSectionBuilder),
    ];

    /// <summary>
    /// The 8 domain-package assemblies that carry every <see cref="AnalyzerModuleAttribute"/>-
    /// annotated analyzer, one representative type per package — used to build the production
    /// catalog without a caller having to know the full package list.
    /// </summary>
    private static readonly Assembly[] DefaultAnalyzerAssemblies =
    [
        typeof(DumpDetective.Analysis.Analyzers.Memory.MemoryAnalyzer).Assembly,
        typeof(DumpDetective.Analysis.Analyzers.Gc.GCRootAnalyzer).Assembly,
        typeof(DumpDetective.Analysis.Analyzers.Threads.ThreadAnalyzer).Assembly,
        typeof(DumpDetective.Analysis.Analyzers.Async.AsyncTaskAnalyzer).Assembly,
        typeof(DumpDetective.Analysis.Analyzers.Types.ArrayAnalyzer).Assembly,
        typeof(DumpDetective.Analysis.Analyzers.Leaks.LeakCandidateAnalyzer).Assembly,
        typeof(DumpDetective.Analysis.Analyzers.Runtime.ModuleAnalyzer).Assembly,
        typeof(DumpDetective.Analysis.Analyzers.Infra.DbConnectionAnalyzer).Assembly,
    ];

    /// <summary>
    /// Builds the production catalog by scanning <see cref="DefaultAnalyzerAssemblies"/> — the
    /// single source of truth for "which assemblies carry analyzers" that DI wiring, factory
    /// fallback constructors, and tests all share.
    /// </summary>
    public static IAnalyzerFeatureModuleCatalog BuildDefault() => new PluginCatalogBuilder().Build(DefaultAnalyzerAssemblies);

    public IAnalyzerFeatureModuleCatalog Build(params IReadOnlyList<Assembly> analyzerAssemblies)
    {
        List<AnalyzerFeatureModule> modules = [];

        foreach (Assembly assembly in analyzerAssemblies)
        {
            foreach (Type analyzerType in assembly.GetTypes())
            {
                if (!analyzerType.IsClass || analyzerType.IsAbstract || !typeof(IAnalyzer).IsAssignableFrom(analyzerType))
                {
                    continue;
                }

                AnalyzerModuleAttribute? moduleAttribute = analyzerType.GetCustomAttribute<AnalyzerModuleAttribute>();
                if (moduleAttribute is null)
                {
                    continue;
                }

                modules.Add(BuildModule(analyzerType, moduleAttribute));
            }
        }

        return new BuiltAnalyzerFeatureModuleCatalog(modules, GlobalReportSectionBuilderTypes);
    }

    private static AnalyzerFeatureModule BuildModule(Type analyzerType, AnalyzerModuleAttribute moduleAttribute)
    {
        string baseName = analyzerType.Name.EndsWith("Analyzer", StringComparison.Ordinal)
            ? analyzerType.Name[..^"Analyzer".Length]
            : analyzerType.Name;

        Assembly reportingAssembly = typeof(PluginCatalogBuilder).Assembly;

        Type findingGeneratorType = ResolveCompanionType(
            analyzerType, reportingAssembly, "DumpDetective.Reporting.FindingGenerators", $"{baseName}FindingGenerator");
        Type trendComparerType = ResolveCompanionType(
            analyzerType, analyzerType.Assembly, "DumpDetective.Analysis.Trend.Comparers", $"{baseName}TrendComparer");
        Type sectionBuilderType = ResolveCompanionType(
            analyzerType, reportingAssembly, "DumpDetective.Reporting.SectionBuilders", $"{baseName}SectionBuilder");

        AnalyzerFeatureModule module = new(
            Key: moduleAttribute.Key,
            DisplayName: moduleAttribute.DisplayName,
            AnalyzerType: analyzerType,
            FindingGeneratorType: findingGeneratorType,
            TrendComparerType: trendComparerType,
            AnalyzerSectionBuilderType: sectionBuilderType,
            ReportSectionContributionTypes: [],
            Order: moduleAttribute.Order,
            Tags: moduleAttribute.Tags);

        if (!module.IsShapeValid())
        {
            throw new InvalidOperationException(
                $"PluginCatalogBuilder built an AnalyzerFeatureModule for '{analyzerType.FullName}' that " +
                "failed IsShapeValid() despite resolving all companion types — check the [AnalyzerModule] " +
                "attribute's Key/DisplayName/Order values.");
        }

        return module;
    }

    private static Type ResolveCompanionType(Type analyzerType, Assembly assembly, string @namespace, string typeName)
    {
        Type? companionType = assembly.GetType($"{@namespace}.{typeName}", throwOnError: false);
        if (companionType is null)
        {
            throw new InvalidOperationException(
                $"Analyzer '{analyzerType.FullName}' is annotated with [AnalyzerModule] but its expected " +
                $"companion type '{@namespace}.{typeName}' was not found in assembly " +
                $"'{assembly.GetName().Name}'. PluginCatalogBuilder resolves FindingGenerator/" +
                "TrendComparer/SectionBuilder types by stripping a trailing \"Analyzer\" from the analyzer's " +
                "type name and appending the companion's suffix — rename the companion type to match, or " +
                "the analyzer if the mismatch is on its side.");
        }

        return companionType;
    }

    private sealed class BuiltAnalyzerFeatureModuleCatalog(
        IReadOnlyList<AnalyzerFeatureModule> modules,
        IReadOnlyList<Type> globalReportSectionBuilderTypes) : IAnalyzerFeatureModuleCatalog
    {
        public IReadOnlyList<AnalyzerFeatureModule> Modules { get; } = modules;

        public IReadOnlyList<Type> GlobalReportSectionBuilderTypes { get; } = globalReportSectionBuilderTypes;
    }
}
