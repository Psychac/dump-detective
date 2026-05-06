using DumpDetective.Analysis.Utilities;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class ReservoirSamplerTests
{
    [Fact]
    public void ReservoirSampler_Deterministic_For_Seeded_Sequence()
    {
        var sampler1 = new ReservoirSampler<int>(5, seed: 12345);
        var sampler2 = new ReservoirSampler<int>(5, seed: 12345);

        for (int i = 0; i < 100; i++)
        {
            sampler1.Add(i);
            sampler2.Add(i);
        }

        var s1 = sampler1.Samples();
        var s2 = sampler2.Samples();

        s1.Should().Equal(s2);
        s1.Count.Should().Be(5);
    }

    [Fact]
    public void ReservoirSampler_Respects_Capacity_Zero()
    {
        var sampler = new ReservoirSampler<int>(0, seed: 42);
        for (int i = 0; i < 10; i++) sampler.Add(i);
        sampler.Samples().Should().BeEmpty();
    }
}
