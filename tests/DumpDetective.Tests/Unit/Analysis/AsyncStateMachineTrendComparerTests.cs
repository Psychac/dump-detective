using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Trend.Comparers;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class AsyncStateMachineTrendComparerTests
{
    private static AsyncStateMachineDomainResult MakeResult(
        int totalStateMachines = 100,
        ulong totalBytes = 1000,
        long gen2Count = 50,
        double gen2Fraction = 0.5) =>
        new(
            TotalStateMachines: totalStateMachines,
            TotalStateMachineBytes: totalBytes,
            TopStateMachineTypes:
            [
                new StateMachineTypeProfile(
                    TypeName: "Test<Method>d__1",
                    OriginatingMethod: "Method",
                    DeclaringType: "TestType",
                    Count: totalStateMachines,
                    TotalBytes: totalBytes,
                    DominantState: 0,
                StateDistribution: [],
                    ReferenceFieldCount: 2,
                    Gen2Count: gen2Count,
                    Gen2Fraction: gen2Fraction,
                    IsAsyncVoid: false)
            ],
            TopByCapturedSize: [],
            SuspendedMethodMap: [],
            ScanLimited: false,
            TotalGen2Count: gen2Count);

    [Fact]
    public void ExtractMetrics_ReturnsGeneration2Metrics()
    {
        var comparer = new AsyncStateMachineTrendComparer();
        var result = MakeResult(totalStateMachines: 100, gen2Count: 50);

        var metrics = comparer.ExtractMetrics(result);

        metrics.Should().Contain(m => m.Key == "statemachine.total" && m.Value == 100);
        metrics.Should().Contain(m => m.Key == "statemachine.total.bytes" && m.Value == 1000);
        metrics.Should().Contain(m => m.Key == "statemachine.gen2.count" && m.Value == 50);
        metrics.Should().Contain(m => m.Key == "statemachine.gen2.fraction" && m.Value == 50.0);
    }

    [Fact]
    public void ExtractMetrics_Gen2FractionCalculated_AsPercentage()
    {
        var comparer = new AsyncStateMachineTrendComparer();
        var result = MakeResult(totalStateMachines: 200, gen2Count: 75);

        var metrics = comparer.ExtractMetrics(result);

        var gen2Fraction = metrics.FirstOrDefault(m => m.Key == "statemachine.gen2.fraction");
        gen2Fraction.Should().NotBeNull();
        gen2Fraction!.Value.Should().Be(37.5);
    }

    [Fact]
    public void ExtractMetrics_NoStateMachines_Gen2FractionIsZero()
    {
        var comparer = new AsyncStateMachineTrendComparer();
        var result = MakeResult(totalStateMachines: 0, gen2Count: 0);

        var metrics = comparer.ExtractMetrics(result);

        var gen2Fraction = metrics.FirstOrDefault(m => m.Key == "statemachine.gen2.fraction");
        gen2Fraction.Should().NotBeNull();
        gen2Fraction!.Value.Should().Be(0.0);
    }

    [Fact]
    public void Compare_TracksDeltasInGen2Metrics()
    {
        var comparer = new AsyncStateMachineTrendComparer();
        var baseline = MakeResult(totalStateMachines: 100, gen2Count: 30);
        var current = MakeResult(totalStateMachines: 120, gen2Count: 50);

        var deltas = comparer.Compare(baseline, current);

        deltas.Should().Contain(d => d.Key == "statemachine.gen2.count" && d.Delta == 20);
        deltas.Should().Contain(d => d.Key == "statemachine.gen2.fraction");
    }

    [Fact]
    public void Compare_NonAsyncStateMachineDomainResults_ReturnsEmpty()
    {
        var comparer = new AsyncStateMachineTrendComparer();

        var deltas = comparer.Compare(new UnrelatedDomainResult(), new UnrelatedDomainResult());

        deltas.Should().BeEmpty();
    }

    [Fact]
    public void ExtractMetrics_UsesTotalGen2Count_NotTopStateMachineTypesSum()
    {
        // P1-7 regression test: TotalGen2Count is a domain-level aggregate over ALL candidate
        // types (bounded by TypeCandidateLimit), independent of TopStateMachineTypes (bounded by
        // TopTypeLimit). Previously the comparer summed Gen2Count over TopStateMachineTypes only,
        // which understated the fraction whenever candidates exceeded TopTypeLimit. Here
        // TopStateMachineTypes sums to 150 but TotalGen2Count (the correct source) is 400 — if the
        // comparer regressed to summing from TopStateMachineTypes, this test would fail.
        var comparer = new AsyncStateMachineTrendComparer();
        var result = new AsyncStateMachineDomainResult(
            TotalStateMachines: 1000,
            TotalStateMachineBytes: 3000,
            TopStateMachineTypes:
            [
                new StateMachineTypeProfile(
                    TypeName: "Type1<Method>d__1",
                    OriginatingMethod: "Method1",
                    DeclaringType: "TestType",
                    Count: 100,
                    TotalBytes: 1000,
                    DominantState: 0,
                    StateDistribution: [],
                    ReferenceFieldCount: 2,
                    Gen2Count: 40,
                    Gen2Fraction: 0.4,
                    IsAsyncVoid: false),
                new StateMachineTypeProfile(
                    TypeName: "Type2<Method>d__2",
                    OriginatingMethod: "Method2",
                    DeclaringType: "TestType",
                    Count: 100,
                    TotalBytes: 1000,
                    DominantState: 1,
                    StateDistribution: [],
                    ReferenceFieldCount: 1,
                    Gen2Count: 60,
                    Gen2Fraction: 0.6,
                    IsAsyncVoid: false),
                new StateMachineTypeProfile(
                    TypeName: "Type3<Method>d__3",
                    OriginatingMethod: "Method3",
                    DeclaringType: "TestType",
                    Count: 100,
                    TotalBytes: 1000,
                    DominantState: 2,
                    StateDistribution: [],
                    ReferenceFieldCount: 3,
                    Gen2Count: 50,
                    Gen2Fraction: 0.5,
                    IsAsyncVoid: false)
            ],
            TopByCapturedSize: [],
            SuspendedMethodMap: [],
            ScanLimited: true,
            TotalGen2Count: 400);

        var metrics = comparer.ExtractMetrics(result);

        var gen2Count = metrics.FirstOrDefault(m => m.Key == "statemachine.gen2.count");
        gen2Count.Should().NotBeNull();
        gen2Count!.Value.Should().Be(400);

        var gen2Fraction = metrics.FirstOrDefault(m => m.Key == "statemachine.gen2.fraction");
        gen2Fraction.Should().NotBeNull();
        gen2Fraction!.Value.Should().Be(40.0);
    }

    private sealed record UnrelatedDomainResult : AnalyzerDomainResult;
}
