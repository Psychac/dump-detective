using DumpDetective.Analysis.Analyzers;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class ThreadAnalyzerSamplingTests
{
    [Fact]
    public void Sampling_Is_Deterministic_For_Same_Seed()
    {
        var a = ThreadAnalyzer.SampleCandidateIndices(totalCandidates: 1000, capacity: 20, seed: 12345);
        var b = ThreadAnalyzer.SampleCandidateIndices(totalCandidates: 1000, capacity: 20, seed: 12345);

        a.Should().Equal(b);
        a.Count.Should().BeLessOrEqualTo(20);
    }

    [Fact]
    public void Sampling_Produces_Variable_Results_For_Different_Seeds()
    {
        var a = ThreadAnalyzer.SampleCandidateIndices(totalCandidates: 1000, capacity: 20, seed: 1);
        var b = ThreadAnalyzer.SampleCandidateIndices(totalCandidates: 1000, capacity: 20, seed: 2);

        // With extremely small probability these could be equal; assert they are not equal in practice.
        a.Should().NotEqual(b);
    }
}
