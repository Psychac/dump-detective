using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CrashAnalyzerMessageDistributionTests
{
    [Fact]
    public void BuildMessageDistributions_ReportsDistinctCountAndMostCommonMessage()
    {
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new()
            {
                ["SqlException"] =
                [
                    NonActive("Timeout expired"),
                    NonActive("Timeout expired"),
                    NonActive("Connection refused"),
                    Active("Timeout expired", threadId: 7),
                ]
            }
        };

        IReadOnlyList<ExceptionMessageDistribution> distributions = CrashAnalyzer.BuildMessageDistributions(analysis);

        distributions.Should().ContainSingle();
        ExceptionMessageDistribution dist = distributions[0];
        dist.Type.Should().Be("SqlException");
        dist.SampledInstanceCount.Should().Be(4);
        dist.DistinctMessageCount.Should().Be(2);
        dist.MostCommonMessage.Should().Be("Timeout expired");
        dist.MostCommonMessageCount.Should().Be(3);
        dist.MostCommonActiveMessage.Should().Be("Timeout expired");
        dist.MostCommonActiveMessageCount.Should().Be(1);
    }

    [Fact]
    public void BuildMessageDistributions_SkipsTypesWithNoMessages()
    {
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new()
            {
                ["FooException"] = [NonActive(message: null), NonActive(message: "")]
            }
        };

        IReadOnlyList<ExceptionMessageDistribution> distributions = CrashAnalyzer.BuildMessageDistributions(analysis);

        distributions.Should().BeEmpty();
    }

    [Fact]
    public void BuildMessageDistributions_NoActiveInstances_LeavesMostCommonActiveMessageNull()
    {
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new()
            {
                ["BarException"] = [NonActive("boom")]
            }
        };

        IReadOnlyList<ExceptionMessageDistribution> distributions = CrashAnalyzer.BuildMessageDistributions(analysis);

        distributions.Should().ContainSingle();
        distributions[0].MostCommonActiveMessage.Should().BeNull();
        distributions[0].MostCommonActiveMessageCount.Should().Be(0);
    }

    private static ExceptionInstance NonActive(string? message) => new() { Message = message ?? string.Empty };

    private static ExceptionInstance Active(string message, uint threadId) => new()
    {
        Message = message,
        ThreadId = threadId
    };
}
