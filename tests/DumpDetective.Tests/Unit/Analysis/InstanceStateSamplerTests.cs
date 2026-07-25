using DumpDetective.Analysis.Analyzers;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class InstanceStateSamplerTests
{
    [Fact]
    public void TryReserveSample_AllowsUpToPerTypeCap_ThenReportsCapped()
    {
        var sampler = new InstanceStateSampler<string>(maxSamplesPerType: 2, topNCap: 10);
        const ulong mt = 0x1000;

        sampler.TryReserveSample(mt).Should().BeTrue();
        sampler.TryReserveSample(mt).Should().BeTrue();
        sampler.ScanCapped.Should().BeFalse();

        sampler.TryReserveSample(mt).Should().BeFalse();
        sampler.ScanCapped.Should().BeTrue();
    }

    [Fact]
    public void TryReserveSample_TracksEachMethodTableIndependently()
    {
        var sampler = new InstanceStateSampler<string>(maxSamplesPerType: 1, topNCap: 10);

        sampler.TryReserveSample(0x1000).Should().BeTrue();
        sampler.TryReserveSample(0x2000).Should().BeTrue();
        sampler.ScanCapped.Should().BeFalse();

        sampler.TryReserveSample(0x1000).Should().BeFalse();
        sampler.ScanCapped.Should().BeTrue();
    }

    [Fact]
    public void AddTopSample_StopsAppendingOnceTopNCapReached()
    {
        var sampler = new InstanceStateSampler<int>(maxSamplesPerType: 100, topNCap: 2);

        sampler.AddTopSample(1);
        sampler.AddTopSample(2);
        sampler.AddTopSample(3);

        sampler.TopSamples.Should().Equal(1, 2);
    }

    [Fact]
    public void TopSamples_IsEmptyByDefault()
    {
        var sampler = new InstanceStateSampler<int>(maxSamplesPerType: 10, topNCap: 5);

        sampler.TopSamples.Should().BeEmpty();
        sampler.ScanCapped.Should().BeFalse();
    }
}
