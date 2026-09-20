using DumpDetective.Reporting.Capabilities;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// The "before/after catalog snapshot diff" docs/refactor/modularity/phase-3-plugin-packaging.md's
/// migration step 4 asks for: the snapshot below (key, display name, order, tags) was captured from
/// the hand-maintained <c>DefaultAnalyzerFeatureModuleCatalog</c> immediately before it was deleted,
/// so this file is the diff target — a regression in <see cref="PluginCatalogBuilder"/>'s scanning
/// or convention resolution now has nothing live to compare against, only this frozen data.
/// </summary>
public sealed class PluginCatalogBuilderTests
{
    private static readonly (string Key, string DisplayName, int Order, string[] Tags)[] ExpectedModules =
    [
        ("memory", "Memory Analysis", 100, ["memory"]),
        ("gc-generation", "GC Generation Analysis", 110, ["gc"]),
        ("allocation-pattern", "Allocation Pattern Analysis", 120, ["gc", "allocation"]),
        ("object-shape", "Object Shape Analysis", 130, ["types"]),
        ("gc-root", "GC Root Analysis", 140, ["roots"]),
        ("heap-topology", "Heap Topology Analysis", 150, ["heap"]),
        ("module", "Module Analysis", 160, ["runtime"]),
        ("crash", "Crash Analysis", 170, ["exceptions"]),
        ("hang", "Hang Analysis", 180, ["threads"]),
        ("async-task", "Async Task Analysis", 190, ["async"]),
        ("leak-candidate", "Leak Candidate Analysis", 210, ["leaks"]),
        ("dominator", "Dominator Analysis", 220, ["retention", "dominator"]),
        ("string", "String Analysis", 230, ["memory", "string"]),
        ("collection", "Collection Analysis", 240, ["collections"]),
        ("static-root", "Static Root Leak Detection", 250, ["roots", "leaks"]),
        ("reference-chain", "Reference Chain Analysis", 260, ["roots"]),
        ("gc-handle", "GC Handle Analysis", 270, ["handles"]),
        ("loh-fragmentation", "LOH & POH Fragmentation Analysis", 290, ["gc", "loh", "poh"]),
        ("thread-stack-cluster", "Thread Stack Cluster Analysis", 300, ["threads"]),
        ("thread", "Thread Analysis", 310, ["threads"]),
        ("lock-graph", "Lock Graph Analysis", 320, ["threads", "locks"]),
        ("event-leak", "Event Leak Analysis", 330, ["events", "leaks"]),
        ("finalizable-object", "Finalizable Object Analysis", 340, ["gc"]),
        ("async-state-machine", "Async State Machine Analysis", 350, ["async"]),
        ("array", "Array Analysis", 360, ["types"]),
        ("segment-reservation", "Segment Reservation Analysis", 380, ["gc", "segments"]),
        ("weak-reference", "Weak Reference Analysis", 390, ["gc"]),
        ("boxing", "Boxing Analysis", 400, ["types", "perf"]),
        ("jit", "JIT Analysis", 410, ["runtime", "perf"]),
        ("db-connection", "DbConnection Analysis", 420, ["infra", "network"]),
        ("sql-transaction", "SQL Transaction Analysis", 425, ["infra", "network"]),
        ("sql-command", "SQL Command Analysis", 426, ["infra", "network"]),
        ("sql-connection-pool", "SQL Connection Pool Analysis", 427, ["infra", "network"]),
        ("wcf-channel", "WCF Channel Analysis", 430, ["infra", "network"]),
        ("http-object", "Http Object Analysis", 440, ["infra", "network"]),
        ("timer-leak", "Timer Leak Analysis", 450, ["infra", "timers"]),
    ];

    private static readonly Type[] ExpectedGlobalReportSectionBuilderTypes =
    [
        typeof(ExecutiveSummarySectionBuilder),
        typeof(TypeSystemSectionBuilder),
        typeof(InsightsSectionBuilder),
        typeof(ConfidenceSectionBuilder),
    ];

    private static IAnalyzerFeatureModuleCatalog BuildScannedCatalog() => PluginCatalogBuilder.BuildDefault();

    [Fact]
    public void Build_ProducesSameModuleCountAsSnapshot()
    {
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();

        actual.Modules.Should().HaveCount(ExpectedModules.Length);
    }

    [Fact]
    public void Build_ProducesModulesMatchingSnapshot()
    {
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();
        var actualByKey = actual.Modules.ToDictionary(m => m.Key);

        actualByKey.Keys.Should().BeEquivalentTo(ExpectedModules.Select(m => m.Key));

        foreach ((string key, string displayName, int order, string[] tags) in ExpectedModules)
        {
            AnalyzerFeatureModule actualModule = actualByKey[key];

            actualModule.DisplayName.Should().Be(displayName, "key {0}", key);
            actualModule.Order.Should().Be(order, "key {0}", key);
            actualModule.Tags.Should().BeEquivalentTo(tags, "key {0}", key);
        }
    }

    [Fact]
    public void Build_ModuleCompanionTypesFollowNamingConvention()
    {
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();

        foreach (AnalyzerFeatureModule module in actual.Modules)
        {
            string baseName = module.AnalyzerType.Name.EndsWith("Analyzer", StringComparison.Ordinal)
                ? module.AnalyzerType.Name[..^"Analyzer".Length]
                : module.AnalyzerType.Name;

            // FindingGeneratorType is retired (null) once its SynthesisRuleType ships — Phase 5
            // step 3. TrendComparerType is retired (null) once its analyzer is fully covered by
            // ObservationTrendDiffer — Phase 5 step 4. Whichever companion is present must still
            // follow the naming convention.
            module.FindingGeneratorType?.Name.Should().Be($"{baseName}FindingGenerator", "analyzer {0}", module.AnalyzerType.Name);
            module.SynthesisRuleType?.Name.Should().Be($"{baseName}SynthesisRule", "analyzer {0}", module.AnalyzerType.Name);
            module.TrendComparerType?.Name.Should().Be($"{baseName}TrendComparer", "analyzer {0}", module.AnalyzerType.Name);
            module.AnalyzerSectionBuilderType.Name.Should().Be($"{baseName}SectionBuilder", "analyzer {0}", module.AnalyzerType.Name);
        }
    }

    [Fact]
    public void Build_EveryModuleHasAFindingGeneratorOrASynthesisRule()
    {
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();

        actual.Modules.Should().OnlyContain(m => m.FindingGeneratorType != null || m.SynthesisRuleType != null);
    }

    /// <summary>
    /// Unlike FindingGeneratorType/SynthesisRuleType there's no per-module companion recording
    /// "what covers this analyzer's trend now" — <c>ObservationDifferAnalyzerOwnership</c> is the
    /// real answer, so this is the fast, DI-free guard against retiring a comparer without also
    /// registering its analyzer there (StartupValidator catches the same gap at CLI startup, this
    /// catches it at build time). Matched by <see cref="AnalyzerFeatureModule.AnalyzerType"/>, not
    /// <see cref="AnalyzerFeatureModule.DisplayName"/> — the two aren't guaranteed to agree
    /// (<c>ThreadStackClusterAnalyzer</c>'s DisplayName "Thread Stack Cluster Analysis" differs
    /// from its <c>Name</c>/<c>AnalyzerName</c> "Thread Stack Signature Clustering", a real case
    /// this exact test caught before the fix in
    /// <c>StartupValidator.ValidateRegistrations</c>'s equivalent finding-generator join).
    /// </summary>
    private static readonly Type[] ExpectedObservationDifferCoveredAnalyzerTypes =
    [
        typeof(DumpDetective.Analysis.Analyzers.Gc.WeakReferenceAnalyzer),
        typeof(DumpDetective.Analysis.Analyzers.Leaks.EventLeakAnalyzer),
        typeof(DumpDetective.Analysis.Analyzers.Leaks.ReferenceChainAnalyzer),
        typeof(DumpDetective.Analysis.Analyzers.Memory.LohFragmentationAnalyzer),
        typeof(DumpDetective.Analysis.Analyzers.Memory.SegmentReservationAnalyzer),
        typeof(DumpDetective.Analysis.Analyzers.Runtime.JitAnalyzer),
        typeof(DumpDetective.Analysis.Analyzers.Threads.LockGraphAnalyzer),
        typeof(DumpDetective.Analysis.Analyzers.Threads.ThreadStackClusterAnalyzer),
    ];

    [Fact]
    public void Build_EveryModuleWithNoTrendComparer_IsObservationDifferCovered()
    {
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();

        actual.Modules.Should().OnlyContain(
            m => m.TrendComparerType != null || ExpectedObservationDifferCoveredAnalyzerTypes.Contains(m.AnalyzerType));
    }

    [Fact]
    public void Build_ProducesExpectedGlobalReportSectionBuilderTypes()
    {
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();

        actual.GlobalReportSectionBuilderTypes.Should().BeEquivalentTo(ExpectedGlobalReportSectionBuilderTypes);
    }

    [Fact]
    public void Build_EveryModuleIsShapeValid()
    {
        IAnalyzerFeatureModuleCatalog actual = BuildScannedCatalog();

        actual.Modules.Should().OnlyContain(m => m.IsShapeValid());
    }
}
