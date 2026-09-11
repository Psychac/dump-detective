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
        IReadOnlyList<SuspendedMethodEntry>? suspendedMethods = null,
        IReadOnlyList<HighCaptureStateMachine>? topByCapturedSize = null) =>
        new(
            TotalStateMachines: totalStateMachines,
            TotalStateMachineBytes: totalBytes,
            TopStateMachineTypes: topTypes ?? [],
            TopByCapturedSize: topByCapturedSize ?? [],
            SuspendedMethodMap: suspendedMethods ?? []);

    [Fact]
    public void Generate_TotalStateMachinesAtOrAboveTenThousand_EmitsCriticalFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var result = MakeResult(totalStateMachines: 15_000);

        var findings = gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("suspend") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_TotalStateMachinesAtOrAboveOneThousand_BelowTenThousand_EmitsWarningFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var result = MakeResult(totalStateMachines: 2_000);

        var findings = gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("suspend") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_TotalStateMachinesBelowOneThousand_EmitsNoHighCountFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var result = MakeResult(totalStateMachines: 500);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("suspend"));
    }

    [Fact]
    public void Generate_FireAndForgetSuspendedCountAtOrAboveTenThousand_EmitsCriticalFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var result = MakeResult(suspendedMethods:
        [
            new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "ProcessAsync", SuspendedCount: 12_000, TotalBytes: 500_000)
        ]);

        var findings = gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("fire-and-forget") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_FireAndForgetSuspendedCountAtOrAboveHundred_BelowCritical_EmitsWarningFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var result = MakeResult(suspendedMethods:
        [
            new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "ProcessAsync", SuspendedCount: 150, TotalBytes: 5_000)
        ]);

        var findings = gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("fire-and-forget") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_FireAndForgetSuspendedCountBelowHundred_EmitsNoFireAndForgetFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var result = MakeResult(suspendedMethods:
        [
            new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "ProcessAsync", SuspendedCount: 20, TotalBytes: 500)
        ]);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("fire-and-forget"));
    }

    [Fact]
    public void Generate_CapturedBytesAtOrAboveTwoHundredMb_EmitsCriticalFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var result = MakeResult(topByCapturedSize:
        [
            new HighCaptureStateMachine(Address: 0x1000, TypeName: "MyApp.Handler<>d__1", TotalCapturedRefBytes: 250_000_000UL, LargeCaptures: ["_dbContext"])
        ]);

        var findings = gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("capture") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_CapturedBytesAtOrAboveFiftyMb_BelowCritical_EmitsWarningFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var result = MakeResult(topByCapturedSize:
        [
            new HighCaptureStateMachine(Address: 0x1000, TypeName: "MyApp.Handler<>d__1", TotalCapturedRefBytes: 60_000_000UL, LargeCaptures: ["_buffer"])
        ]);

        var findings = gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("capture") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_CapturedBytesBelowFiftyMb_EmitsNoCaptureFinding()
    {
        var gen = new AsyncStateMachineFindingGenerator();
        var result = MakeResult(topByCapturedSize:
        [
            new HighCaptureStateMachine(Address: 0x1000, TypeName: "MyApp.Handler<>d__1", TotalCapturedRefBytes: 1_000_000UL, LargeCaptures: [])
        ]);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("capture"));
    }

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
