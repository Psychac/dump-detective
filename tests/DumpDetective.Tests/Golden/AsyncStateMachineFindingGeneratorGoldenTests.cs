using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.FindingGenerators;

using Xunit;

namespace DumpDetective.Tests.Golden;

/// <summary>
/// Freezes <see cref="AsyncStateMachineFindingGenerator"/>'s exact output as the parity oracle for
/// its eventual <c>ISynthesisRule</c> replacement — playbook step b, reusing
/// <see cref="AsyncStateMachineFindingGeneratorTests"/>'s existing boundary fixtures plus two new
/// ones it never exercised: the high-count branch's "top method" narrative (every existing
/// high-count fixture leaves <c>TopStateMachineTypes</c> empty, so it always hits the "N/A"
/// branch), and the fire-and-forget branch's <c>MaxFireAndForgetFindings</c> cap. See
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class AsyncStateMachineFindingGeneratorGoldenTests : GoldenTestBase
{
    private readonly AsyncStateMachineFindingGenerator _gen = new();

    [Fact]
    public void TotalStateMachinesAtOrAboveTenThousand_MatchesGoldenOutput() =>
        Approve(MakeResult(totalStateMachines: 15_000));

    [Fact]
    public void TotalStateMachinesAtOrAboveOneThousand_BelowTenThousand_MatchesGoldenOutput() =>
        Approve(MakeResult(totalStateMachines: 2_000));

    [Fact]
    public void TotalStateMachinesBelowOneThousand_MatchesGoldenOutput() =>
        Approve(MakeResult(totalStateMachines: 500));

    [Fact]
    public void TotalStateMachinesHighCount_WithTopTypePopulated_MatchesGoldenOutput() =>
        Approve(MakeResult(
            totalStateMachines: 2_000,
            topTypes:
            [
                MakeProfile("MyApp.Worker+<ProcessAsync>d__7", "ProcessAsync", "MyApp.Worker", count: 1_800, totalBytes: 900_000),
            ]));

    [Fact]
    public void FireAndForgetSuspendedCountAtOrAboveTenThousand_MatchesGoldenOutput() =>
        Approve(MakeResult(suspendedMethods:
        [
            new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "ProcessAsync", SuspendedCount: 12_000, TotalBytes: 500_000),
        ]));

    [Fact]
    public void FireAndForgetSuspendedCountAtOrAboveHundred_BelowCritical_MatchesGoldenOutput() =>
        Approve(MakeResult(suspendedMethods:
        [
            new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "ProcessAsync", SuspendedCount: 150, TotalBytes: 5_000),
        ]));

    [Fact]
    public void FireAndForgetSuspendedCountBelowHundred_MatchesGoldenOutput() =>
        Approve(MakeResult(suspendedMethods:
        [
            new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "ProcessAsync", SuspendedCount: 20, TotalBytes: 500),
        ]));

    [Fact]
    public void FireAndForgetExceedsMaxFindingsCap_MatchesGoldenOutput() =>
        Approve(MakeResult(suspendedMethods:
        [
            new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "Method1", SuspendedCount: 400, TotalBytes: 10_000),
            new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "Method2", SuspendedCount: 300, TotalBytes: 9_000),
            new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "Method3", SuspendedCount: 200, TotalBytes: 8_000),
            new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "Method4", SuspendedCount: 150, TotalBytes: 7_000),
        ]));

    [Fact]
    public void CapturedBytesAtOrAboveTwoHundredMb_MatchesGoldenOutput() =>
        Approve(MakeResult(topByCapturedSize:
        [
            new HighCaptureStateMachine(Address: 0x1000, TypeName: "MyApp.Handler<>d__1", TotalCapturedRefBytes: 250_000_000UL, LargeCaptures: ["_dbContext"]),
        ]));

    [Fact]
    public void CapturedBytesAtOrAboveFiftyMb_BelowCritical_MatchesGoldenOutput() =>
        Approve(MakeResult(topByCapturedSize:
        [
            new HighCaptureStateMachine(Address: 0x1000, TypeName: "MyApp.Handler<>d__1", TotalCapturedRefBytes: 60_000_000UL, LargeCaptures: ["_buffer"]),
        ]));

    [Fact]
    public void CapturedBytesBelowFiftyMb_MatchesGoldenOutput() =>
        Approve(MakeResult(topByCapturedSize:
        [
            new HighCaptureStateMachine(Address: 0x1000, TypeName: "MyApp.Handler<>d__1", TotalCapturedRefBytes: 1_000_000UL, LargeCaptures: []),
        ]));

    [Fact]
    public void NoAsyncVoid_MatchesGoldenOutput() =>
        Approve(MakeResult(topTypes: [MakeProfile("Test<Method>d__1", "Method", "TestClass", count: 5, totalBytes: 100, isAsyncVoid: false)]));

    [Fact]
    public void AsyncVoidWithLowCount_MatchesGoldenOutput() =>
        Approve(MakeResult(topTypes: [MakeProfile("Test<AsyncVoidMethod>d__1", "AsyncVoidMethod", "TestClass", count: 5, totalBytes: 100, isAsyncVoid: true)]));

    [Fact]
    public void AsyncVoidWithHighCount_MatchesGoldenOutput() =>
        Approve(MakeResult(topTypes: [MakeProfile("Test<AsyncVoidMethod>d__1", "AsyncVoidMethod", "TestClass", count: 50, totalBytes: 1000, gen2Count: 10, gen2Fraction: 0.2, isAsyncVoid: true)]));

    [Fact]
    public void MultipleAsyncVoidMethods_MatchesGoldenOutput() =>
        Approve(MakeResult(topTypes:
        [
            MakeProfile("Test<Method1>d__1", "Method1", "TestClass", count: 20, totalBytes: 400, gen2Count: 5, gen2Fraction: 0.25, isAsyncVoid: true),
            MakeProfile("Test<Method2>d__2", "Method2", "TestClass", count: 50, totalBytes: 1000, gen2Count: 10, gen2Fraction: 0.2, isAsyncVoid: true),
        ]));

    private void Approve(AsyncStateMachineDomainResult result, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var findings = _gen.Generate(result);
        ApproveGoldenOutput(SerializeToJson(findings), $"Synthesis/AsyncStateMachineFindingGenerator/{testName}");
    }

    private static StateMachineTypeProfile MakeProfile(
        string typeName, string originatingMethod, string declaringType, int count, ulong totalBytes,
        long gen2Count = 0, double gen2Fraction = 0.0, bool isAsyncVoid = false) => new(
        TypeName: typeName,
        OriginatingMethod: originatingMethod,
        DeclaringType: declaringType,
        Count: count,
        TotalBytes: totalBytes,
        DominantState: 0,
        StateDistribution: [],
        ReferenceFieldCount: 1,
        Gen2Count: gen2Count,
        Gen2Fraction: gen2Fraction,
        IsAsyncVoid: isAsyncVoid);

    private static AsyncStateMachineDomainResult MakeResult(
        int totalStateMachines = 100,
        ulong totalBytes = 1000,
        IReadOnlyList<StateMachineTypeProfile>? topTypes = null,
        IReadOnlyList<SuspendedMethodEntry>? suspendedMethods = null,
        IReadOnlyList<HighCaptureStateMachine>? topByCapturedSize = null) => new(
        TotalStateMachines: totalStateMachines,
        TotalStateMachineBytes: totalBytes,
        TopStateMachineTypes: topTypes ?? [],
        TopByCapturedSize: topByCapturedSize ?? [],
        SuspendedMethodMap: suspendedMethods ?? []);
}
