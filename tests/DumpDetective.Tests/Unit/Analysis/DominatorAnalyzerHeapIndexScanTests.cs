using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Pipeline;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class DominatorAnalyzerHeapIndexScanTests
{
    [Fact]
    public void CreateWorkerInstance_ReturnsFreshDominatorAnalyzer()
    {
        DominatorAnalyzer primary = new();

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<DominatorAnalyzer>();
    }

    [Fact]
    public void MergePartial_SumsReferenceCountsAcrossWorkers_ByAddressKey()
    {
        DominatorAnalyzer primary = new();
        SeedParticipantState(primary, maxReferenceAddresses: 100,
            referenceCount: new Dictionary<ulong, int> { [0x1000] = 2, [0x2000] = 1 },
            skipped: 0, traced: 5, capped: false);

        DominatorAnalyzer worker = new();
        SeedParticipantState(worker, maxReferenceAddresses: 100,
            referenceCount: new Dictionary<ulong, int> { [0x1000] = 3, [0x3000] = 4 },
            skipped: 1, traced: 7, capped: false);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetReferenceCount(primary).Should().BeEquivalentTo(new Dictionary<ulong, int>
        {
            [0x1000] = 5,
            [0x2000] = 1,
            [0x3000] = 4
        });
        GetSkipped(primary).Should().Be(1);
        GetTraced(primary).Should().Be(12);
        GetCapped(primary).Should().BeFalse();
    }

    [Fact]
    public void MergePartial_RespectsMaxReferenceAddressesCap_ForNewAddresses()
    {
        DominatorAnalyzer primary = new();
        SeedParticipantState(primary, maxReferenceAddresses: 1,
            referenceCount: new Dictionary<ulong, int> { [0x1000] = 1 },
            skipped: 0, traced: 1, capped: false);

        DominatorAnalyzer worker = new();
        SeedParticipantState(worker, maxReferenceAddresses: 1,
            referenceCount: new Dictionary<ulong, int> { [0x1000] = 2, [0x2000] = 5 },
            skipped: 0, traced: 2, capped: false);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetReferenceCount(primary).Should().BeEquivalentTo(new Dictionary<ulong, int> { [0x1000] = 3 });
        GetSkipped(primary).Should().Be(1);
    }

    [Fact]
    public void MergePartial_OrsObjectScanCappedAcrossWorkers()
    {
        DominatorAnalyzer primary = new();
        SeedParticipantState(primary, maxReferenceAddresses: 100,
            referenceCount: new Dictionary<ulong, int>(), skipped: 0, traced: 0, capped: false);

        DominatorAnalyzer worker = new();
        SeedParticipantState(worker, maxReferenceAddresses: 100,
            referenceCount: new Dictionary<ulong, int>(), skipped: 0, traced: 0, capped: true);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetCapped(primary).Should().BeTrue();
    }

    private static void SeedParticipantState(
        DominatorAnalyzer analyzer,
        int maxReferenceAddresses,
        Dictionary<ulong, int> referenceCount,
        long skipped,
        long traced,
        bool capped)
    {
        Type type = typeof(DominatorAnalyzer);
        SetField(type, analyzer, "_maxReferenceAddresses", maxReferenceAddresses);
        SetField(type, analyzer, "_referenceCount", referenceCount);
        SetField(type, analyzer, "_skippedReferenceAddresses", skipped);
        SetField(type, analyzer, "_objectsTraced", traced);
        SetField(type, analyzer, "_objectScanCapped", capped);
    }

    private static void SetField(Type type, object instance, string fieldName, object? value) =>
        type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static Dictionary<ulong, int> GetReferenceCount(DominatorAnalyzer analyzer) =>
        (Dictionary<ulong, int>)typeof(DominatorAnalyzer)
            .GetField("_referenceCount", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static long GetSkipped(DominatorAnalyzer analyzer) =>
        (long)typeof(DominatorAnalyzer)
            .GetField("_skippedReferenceAddresses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static long GetTraced(DominatorAnalyzer analyzer) =>
        (long)typeof(DominatorAnalyzer)
            .GetField("_objectsTraced", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static bool GetCapped(DominatorAnalyzer analyzer) =>
        (bool)typeof(DominatorAnalyzer)
            .GetField("_objectScanCapped", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;
}
