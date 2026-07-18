using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class EventLeakFindingGeneratorTests
{
    [Fact]
    public void Generate_NoLeaks_ReturnsInfoFinding()
    {
        var gen = new EventLeakFindingGenerator();
        var result = new EventLeakDomainResult(
            TotalEventLeakInstances: 0,
            TotalSubscribers: 0,
            StaticEventLeakCount: 0,
            InstanceEventLeakCount: 0,
            TopPublisherEventsBySubscribers: [],
            TopLeakGroups: [],
            TopLeakInstances: []);

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(FindingSeverity.Info);
        findings[0].Title.Should().Contain("No event-leak signatures");
    }

    [Fact]
    public void Generate_MixedLeakGroups_EmitsOneInstanceAndOneStaticFinding()
    {
        var gen = new EventLeakFindingGenerator();
        var result = BuildResult(
            new EventLeakGroupSnapshot("OrderPublisher", "Changed", false, 30, 12, 140, 11.6, 1, 20),
            new EventLeakGroupSnapshot("OrderPublisher", "Closed", false, 18, 7, 30, 4.2, 1, 8),
            new EventLeakGroupSnapshot("GlobalBus", "MessageArrived", true, 26, 1, 95, 95, 95, 95));

        var findings = gen.Generate(result);

        findings.Should().HaveCount(2);
        findings.Should().Contain(f => f.Tags.Contains("instance-event") && f.Evidence.Contains("2 leak group(s)", StringComparison.Ordinal));
        findings.Should().Contain(f => f.Tags.Contains("static-event") && f.Evidence.Contains("1 leak group(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_OnlyInstanceLeakGroups_EmitsSingleInstanceAggregate()
    {
        var gen = new EventLeakFindingGenerator();
        var result = BuildResult(
            new EventLeakGroupSnapshot("OrderPublisher", "Changed", false, 22, 8, 64, 8.0, 1, 12),
            new EventLeakGroupSnapshot("CartPublisher", "Updated", false, 19, 5, 28, 5.6, 1, 9));

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Tags.Should().Contain("instance-event");
        findings[0].Title.Should().Contain("patterns");
    }

    [Fact]
    public void Generate_UsesHighestGroupSeverity_ForAggregateSeverity()
    {
        var gen = new EventLeakFindingGenerator();
        var result = BuildResult(
            new EventLeakGroupSnapshot("OrderPublisher", "Changed", false, 19, 8, 44, 5.5, 1, 9),
            new EventLeakGroupSnapshot("OrderPublisher", "Completed", false, 41, 4, 60, 15, 1, 30));

        var findings = gen.Generate(result);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(FindingSeverity.Critical);
    }

    private static EventLeakDomainResult BuildResult(params EventLeakGroupSnapshot[] groups)
    {
        int totalSubscribers = 0;
        int staticCount = 0;
        int instanceCount = 0;
        for (int i = 0; i < groups.Length; i++)
        {
            totalSubscribers += groups[i].TotalSubscribers;
            if (groups[i].IsStatic) staticCount++;
            else instanceCount++;
        }

        return new EventLeakDomainResult(
            TotalEventLeakInstances: groups.Length,
            TotalSubscribers: totalSubscribers,
            StaticEventLeakCount: staticCount,
            InstanceEventLeakCount: instanceCount,
            TopPublisherEventsBySubscribers: [],
            TopLeakGroups: groups,
            TopLeakInstances: []);
    }
}
