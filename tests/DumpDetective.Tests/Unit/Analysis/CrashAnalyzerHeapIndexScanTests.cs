using System.Reflection;

using DumpDetective.Analysis.Analyzers.Runtime;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;

using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Post-retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), the
/// heap-index-scan-participant role — and everything it accumulates — moved from
/// <see cref="CrashAnalyzer"/> onto <see cref="CrashAnalyzerLegacyAdapter"/>. The adapter's own
/// accumulation is now just a flat candidate list plus an aggregate-exception address set; all the
/// per-type/generation/message aggregation this file used to exercise on the analyzer's own merge
/// step now happens in a single pass over the merged candidate list inside
/// <see cref="CrashAnalyzer"/>'s own <c>Analyze</c> method, covered by the retyping characterization
/// tests instead.
/// </summary>
public sealed class CrashAnalyzerHeapIndexScanTests
{
    [Fact]
    public void CreateWorkerInstance_ReturnsFreshAdapterInstance()
    {
        var primary = new CrashAnalyzerLegacyAdapter();

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<CrashAnalyzerLegacyAdapter>();
    }

    [Fact]
    public void MergePartial_ConcatenatesCandidatesInWorkerOrder_AndUnionsAggregateAddresses()
    {
        var primary = new CrashAnalyzerLegacyAdapter();
        SetCandidates(primary, [MakeCandidate(0x1000, "FooException")]);
        SetAggregateAddresses(primary, [0x1000UL]);

        var worker = new CrashAnalyzerLegacyAdapter();
        SetCandidates(worker, [MakeCandidate(0x2000, "System.AggregateException")]);
        SetAggregateAddresses(worker, [0x2000UL]);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetCandidates(primary).Select(c => c.Address).Should().Equal(0x1000UL, 0x2000UL);
        GetAggregateAddresses(primary).Should().BeEquivalentTo(new HashSet<ulong> { 0x1000UL, 0x2000UL });
    }

    [Fact]
    public void MergePartial_PropagatesScanFailureFromAnyWorker()
    {
        var primary = new CrashAnalyzerLegacyAdapter();
        var worker = new CrashAnalyzerLegacyAdapter();
        ((IHeapIndexScanParticipant)worker).OnHeapIndexScanCompleted(false);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetScanSucceeded(primary).Should().BeFalse();
    }

    private static HeapObjectRef MakeCandidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static void SetCandidates(CrashAnalyzerLegacyAdapter adapter, List<HeapObjectRef> candidates)
    {
        var field = (List<HeapObjectRef>)GetField(adapter, "_candidates");
        field.AddRange(candidates);
    }

    private static void SetAggregateAddresses(CrashAnalyzerLegacyAdapter adapter, IEnumerable<ulong> addresses)
    {
        var field = (HashSet<ulong>)GetField(adapter, "_aggregateAddresses");
        foreach (ulong address in addresses)
            field.Add(address);
    }

    private static List<HeapObjectRef> GetCandidates(CrashAnalyzerLegacyAdapter adapter) =>
        (List<HeapObjectRef>)GetField(adapter, "_candidates");

    private static HashSet<ulong> GetAggregateAddresses(CrashAnalyzerLegacyAdapter adapter) =>
        (HashSet<ulong>)GetField(adapter, "_aggregateAddresses");

    private static bool GetScanSucceeded(CrashAnalyzerLegacyAdapter adapter) =>
        (bool)GetField(adapter, "_scanSucceeded");

    private static object GetField(CrashAnalyzerLegacyAdapter adapter, string fieldName) =>
        typeof(CrashAnalyzerLegacyAdapter).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(adapter)!;
}
