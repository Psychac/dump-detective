using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ThreadSectionBuilderTests
{
    private static ThreadDomainResult BuildResult(
        IReadOnlyDictionary<string, int>? appDomainDistribution = null,
        IReadOnlyList<NameCountEntry>? topStackHotspots = null) =>
        new(
            TotalThreadCount: 10,
            AliveThreadCount: 10,
            InactiveThreadCount: 0,
            GcThreadCount: 0,
            BlockedThreadCount: 0,
            LockHoldingThreadCount: 0,
            ThreadsWithActiveExceptionsCount: 0,
            BackgroundThreadCount: 0,
            WaitPatternBreakdown: new Dictionary<string, int>(),
            AppDomainDistribution: appDomainDistribution,
            TopStackHotspots: topStackHotspots);

    private static CompactTable? AppDomainTable(AnalyzerDetailSection section) =>
        section.CompactTables?.SingleOrDefault(t => t.Title == "AppDomain thread distribution");

    private static bool HasClusterCrossReference(AnalyzerDetailSection section) =>
        section.Blocks.OfType<TextBlock>().Any(b => b.Text.Contains("Thread Stack Signature Clustering", StringComparison.Ordinal));

    [Fact]
    public void Build_SingleAppDomain_SuppressesTable()
    {
        var result = BuildResult(new Dictionary<string, int> { ["<No AppDomain>"] = 10 });

        var section = new ThreadSectionBuilder().Build(result);

        AppDomainTable(section).Should().BeNull();
    }

    [Fact]
    public void Build_MultipleAppDomains_IncludesTable()
    {
        var result = BuildResult(new Dictionary<string, int> { ["Domain A"] = 6, ["Domain B"] = 4 });

        var section = new ThreadSectionBuilder().Build(result);

        var table = AppDomainTable(section);
        table.Should().NotBeNull();
        table!.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void Build_NoAppDomainData_SuppressesTable()
    {
        var result = BuildResult(null);

        var section = new ThreadSectionBuilder().Build(result);

        AppDomainTable(section).Should().BeNull();
    }

    [Fact]
    public void Build_WithHotspots_IncludesClusterCrossReference()
    {
        var result = BuildResult(topStackHotspots: [new NameCountEntry("MyApp.Worker.Run()", 5)]);

        var section = new ThreadSectionBuilder().Build(result);

        HasClusterCrossReference(section).Should().BeTrue();
    }

    [Fact]
    public void Build_NoHotspots_OmitsClusterCrossReference()
    {
        var result = BuildResult(topStackHotspots: []);

        var section = new ThreadSectionBuilder().Build(result);

        HasClusterCrossReference(section).Should().BeFalse();
    }
}
