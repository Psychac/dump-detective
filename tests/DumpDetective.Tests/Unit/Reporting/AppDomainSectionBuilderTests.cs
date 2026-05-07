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
            AnonymousModuleCount: 0,
            TopModulesByTypeCount: [])
            with
        {
            Warnings = new[] { "Index missing; sampling used." },
            Metrics = new Dictionary<string, object?> { ["ExcludedModuleCount"] = 150 }
        };

        var stamped = Stamped(domain, "AppDomain Analysis", "Modules");

        AnalyzerDetailSection section = new AppDomainSectionBuilder().Build(stamped);

        // There should be a NOTES heading and message
        section.Blocks.OfType<HeadingBlock>().Any(h => h.Text.Contains("NOTES")).Should().BeTrue();
        section.Blocks.OfType<TextBlock>().Any(t => t.Text.Contains("Index missing; sampling used.")).Should().BeTrue();

        // There should be an EXCLUDED MODULES heading and a metric block for excluded modules
        section.Blocks.OfType<HeadingBlock>().Any(h => h.Text.Contains("EXCLUDED MODULES")).Should().BeTrue();
        section.Blocks.OfType<MetricBlock>().Any(m => m.Label == "Excluded Modules" && (int)m.RawValue == 150).Should().BeTrue();
    }
}
