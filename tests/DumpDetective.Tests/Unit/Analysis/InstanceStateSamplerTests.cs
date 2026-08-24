using DumpDetective.Analysis.Analyzers;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class InstanceStateSamplerTests
{
    [Fact]
    public void AddTopSample_AppendsEveryInstance_NoCap()
    {
        var sampler = new InstanceStateSampler<int>();

        sampler.AddTopSample(1);
        sampler.AddTopSample(2);
        sampler.AddTopSample(3);

        sampler.TopSamples.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void TopSamples_IsEmptyByDefault()
    {
        var sampler = new InstanceStateSampler<int>();

        sampler.TopSamples.Should().BeEmpty();
    }

    [Fact]
    public void MergeFrom_CombinesSamplesFromBothInstances()
    {
        var primary = new InstanceStateSampler<int>();
        primary.AddTopSample(1);

        var other = new InstanceStateSampler<int>();
        other.AddTopSample(2);
        other.AddTopSample(3);

        primary.MergeFrom(other);

        primary.TopSamples.Should().Equal(1, 2, 3);
    }
}
