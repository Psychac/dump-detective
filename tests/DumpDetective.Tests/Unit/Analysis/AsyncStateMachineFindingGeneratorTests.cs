using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class AsyncStateMachineFindingGeneratorTests
{
    private static AsyncStateMachineDomainResult MakeResult(
        int totalStateMachines = 100,
        ulong totalBytes = 1000,
        IReadOnlyList<StateMachineTypeProfile>? topTypes = null,
        IReadOnlyList<SuspendedMethodEntry>? suspendedMethods = null) =>
        new(
            TotalStateMachines: totalStateMachines,
            TotalStateMachineBytes: totalBytes,
            TopStateMachineTypes: topTypes ?? [],
            TopByCapturedSize: [],
            SuspendedMethodMap: suspendedMethods ?? []);

    [Fact]
    public void Generate_NoAsyncVoid_NoFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var types = new[]
        {
            new StateMachineTypeProfile(
                TypeName: "Test<Method>d__1",
                OriginatingMethod: "Method",
                DeclaringType: "TestClass",
                Count: 5,
                TotalBytes: 100,
                DominantState: 0,
                StateDistribution: [],
                ReferenceFieldCount: 1,
                Gen2Count: 0,
                Gen2Fraction: 0.0,
                IsAsyncVoid: false)
        };
        var result = MakeResult(topTypes: types);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("async-void"));
    }

    [Fact]
    public void Generate_AsyncVoidWithLowCount_NoFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var types = new[]
        {
            new StateMachineTypeProfile(
                TypeName: "Test<AsyncVoidMethod>d__1",
                OriginatingMethod: "AsyncVoidMethod",
                DeclaringType: "TestClass",
                Count: 5,
                TotalBytes: 100,
                DominantState: 0,
                StateDistribution: [],
                ReferenceFieldCount: 1,
                Gen2Count: 0,
                Gen2Fraction: 0.0,
                IsAsyncVoid: true)
        };
        var result = MakeResult(topTypes: types);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("async-void"));
    }

    [Fact]
    public void Generate_AsyncVoidWithHighCount_GeneratesFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var types = new[]
        {
            new StateMachineTypeProfile(
                TypeName: "Test<AsyncVoidMethod>d__1",
                OriginatingMethod: "AsyncVoidMethod",
                DeclaringType: "TestClass",
                Count: 50,
                TotalBytes: 1000,
                DominantState: 0,
                StateDistribution: [],
                ReferenceFieldCount: 1,
                Gen2Count: 10,
                Gen2Fraction: 0.2,
                IsAsyncVoid: true)
        };
        var result = MakeResult(topTypes: types);

        var findings = gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("async-void"));
        var asyncVoidFinding = findings.First(f => f.Tags.Contains("async-void"));
        asyncVoidFinding.Severity.Should().Be(FindingSeverity.Warning);
        asyncVoidFinding.Title.Should().Contain("AsyncVoidMethod");
    }

    [Fact]
    public void Generate_MultipleAsyncVoidMethods_ReportsHighestCount()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var types = new[]
        {
            new StateMachineTypeProfile(
                TypeName: "Test<Method1>d__1",
                OriginatingMethod: "Method1",
                DeclaringType: "TestClass",
                Count: 20,
                TotalBytes: 400,
                DominantState: 0,
                StateDistribution: [],
                ReferenceFieldCount: 1,
                Gen2Count: 5,
                Gen2Fraction: 0.25,
                IsAsyncVoid: true),
            new StateMachineTypeProfile(
                TypeName: "Test<Method2>d__2",
                OriginatingMethod: "Method2",
                DeclaringType: "TestClass",
                Count: 50,
                TotalBytes: 1000,
                DominantState: 0,
                StateDistribution: [],
                ReferenceFieldCount: 1,
                Gen2Count: 10,
                Gen2Fraction: 0.2,
                IsAsyncVoid: true)
        };
        var result = MakeResult(topTypes: types);

        var findings = gen.Generate(result);

        var asyncVoidFinding = findings.FirstOrDefault(f => f.Tags.Contains("async-void"));
        asyncVoidFinding.Should().NotBeNull();
        asyncVoidFinding!.Title.Should().Contain("Method2");
        asyncVoidFinding.MetricValue.Should().Be(2);
    }

    [Fact]
    public void Generate_AsyncVoidFindingContent_IsActionable()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var types = new[]
        {
            new StateMachineTypeProfile(
                TypeName: "Test<AsyncVoidEventHandler>d__1",
                OriginatingMethod: "AsyncVoidEventHandler",
                DeclaringType: "EventSource",
                Count: 100,
                TotalBytes: 2000,
                DominantState: 0,
                StateDistribution: [],
                ReferenceFieldCount: 2,
                Gen2Count: 20,
                Gen2Fraction: 0.2,
                IsAsyncVoid: true)
        };
        var result = MakeResult(topTypes: types);

        var findings = gen.Generate(result);

        var asyncVoidFinding = findings.First(f => f.Tags.Contains("async-void"));
        asyncVoidFinding.Evidence.Should().Contain("AsyncVoidEventHandler");
        asyncVoidFinding.Recommendation.Should().Contain("async Task");
        asyncVoidFinding.Recommendation.Should().Contain("exception");
    }
}
