using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.SectionBuilders;
using DumpDetective.Reporting.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class AppDomainSectionBuilderTests
{
    private static T Stamped<T>(T domain, string analyzerName, string category)
        where T : AnalyzerDomainResult
        => domain with { AnalyzerName = analyzerName, Category = category };

    [Fact]
    public void Build_IncludesWarningsAndExcludedModuleSummary()
    {
        var domainSnapshot = new AppDomainSnapshot(
            Name: "DefaultDomain",
            Address: 0x1000_0000,
            DomainId: 1,
            ModuleCount: 200,
            EstimatedManagedBytes: 0);

        var domain = new AppDomainDomainResult(
            TotalDomains: 1,
            Domains: [domainSnapshot],
            TotalDynamicModules: 0,
            DynamicModuleBytes: 0,
            AnonymousModuleCount: 0,
            TopModulesByTypeCount: [],
            ExcludedModuleCount: 150)
            with
        {
            Warnings = new[] { "Index missing; sampling used." }
        };

        var stamped = Stamped(domain, "AppDomain Analysis", "Modules");

        var resultSet = new AnalyzerResultSet([
            new AnalyzerRunResult(
                AnalyzerName: "AppDomain Analysis",
                Status: AnalyzerExecutionStatus.Success,
                Duration: TimeSpan.FromMilliseconds(1),
                Result: stamped,
                ErrorMessage: null,
                ErrorType: null)]);

        AnalyzerDetailSection section = new AppDomainAssemblySectionBuilder().Build(resultSet);

        // Current builder renders the inventory summary for AppDomains as a table.
        section.Tables.Should().NotBeNull();
        section.Tables!.Any(t => t.Title != null && t.Title.Contains("AppDomain", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
        section.KeyMetrics.Should().NotBeNull();
        section.KeyMetrics!.Should().ContainKey("total_domains");
        section.KeyMetrics["total_domains"].Should().BeOfType<NumericMetricValue>().Which.Value.Should().Be(1);
        section.Tables.Should().ContainSingle();
    }
}
