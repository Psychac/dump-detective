using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class SectionIdDomainMapTests
{
    private static string IdOf(string analyzerName)
    {
        SectionIdDomainMap.TryGet(analyzerName, out _, out string sectionId).Should().BeTrue();
        return sectionId;
    }

    [Fact]
    public void LockGraphAndStackClusterSections_HaveDifferentIds()
    {
        string lockGraph = IdOf("Lock Graph Analysis");
        string stackCluster = IdOf("Thread Stack Signature Clustering");

        stackCluster.Should().NotBe(lockGraph);
    }

    [Fact]
    public void AnalyzerAndItsDisplayName_ShareOneId()
    {
        IdOf("ThreadStackClusterAnalyzer").Should().Be(IdOf("Thread Stack Signature Clustering"));
    }
}
