using System.Reflection;
using System.Runtime.InteropServices;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class StringAnalyzerHeapIndexScanTests
{
    // StringFingerprint is a private nested struct inside StringAnalyzer — access via reflection.
    private static readonly Type FingerprintType =
        typeof(StringAnalyzer).GetNestedType("StringFingerprint", BindingFlags.NonPublic)!;

    private static readonly Type StatsDictType =
        typeof(Dictionary<,>).MakeGenericType(FingerprintType, typeof(StringLeakInfo));

    [Fact]
    public void CreateWorkerInstance_ReturnsFreshStringAnalyzer()
    {
        StringAnalyzer primary = new();

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<StringAnalyzer>();
    }

    [Fact]
    public void MergePartial_DoesNothing_WhenDedupNotActive()
    {
        StringAnalyzer primary = new();
        SeedState(primary, dedupActive: false);

        StringAnalyzer worker = new();
        SeedState(worker, dedupActive: false);

        var act = () => ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);
        act.Should().NotThrow();
    }

    [Fact]
    public void MergePartial_SumsFingerprintCounts_PerKey()
    {
        StringAnalyzer primary = new();
        var fp1 = MakeFingerprint(0xAAAA, 10, 'a', 'z');
        var fp2 = MakeFingerprint(0xBBBB, 20, 'b', 'y');
        var fp3 = MakeFingerprint(0xCCCC, 5, 'c', 'x');

        var primaryStats = MakeStatsDict(new (object key, StringLeakInfo val)[]
        {
            (fp1, new StringLeakInfo { Count = 5, TotalSize = 500, SamplingSource = "IndexScan" }),
            (fp2, new StringLeakInfo { Count = 3, TotalSize = 300, SamplingSource = "IndexScan" })
        });
        var workerStats = MakeStatsDict(new (object key, StringLeakInfo val)[]
        {
            (fp1, new StringLeakInfo { Count = 4, TotalSize = 400, SamplingSource = "IndexScan" }),
            (fp3, new StringLeakInfo { Count = 2, TotalSize = 200, SamplingSource = "IndexScan" })
        });

        SeedState(primary, dedupActive: true, stringStats: primaryStats);

        StringAnalyzer worker = new();
        SeedState(worker, dedupActive: true, stringStats: workerStats);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var merged = (System.Collections.IDictionary)GetField<object>(primary, "_indexScanStringStats")!;
        GetCount(merged, fp1).Should().Be(9);
        GetCount(merged, fp2).Should().Be(3);
        merged.Contains(fp3).Should().BeTrue();
    }

    [Fact]
    public void MergePartial_SumsBuckets_AndStringsRead()
    {
        StringAnalyzer primary = new();
        SeedState(primary, dedupActive: true,
            buckets: new(StringComparer.Ordinal) { ["0-15"] = 10, ["16-31"] = 5 },
            stringsRead: 15);

        StringAnalyzer worker = new();
        SeedState(worker, dedupActive: true,
            buckets: new(StringComparer.Ordinal) { ["0-15"] = 7, ["16-31"] = 3, ["32-63"] = 2 },
            stringsRead: 12);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetBuckets(primary)["0-15"].Should().Be(17);
        GetBuckets(primary)["16-31"].Should().Be(8);
        GetBuckets(primary)["32-63"].Should().Be(2);
        GetStringsRead(primary).Should().Be(27);
    }

    [Fact]
    public void MergePartial_ConcatsVeryLongStrings()
    {
        StringAnalyzer primary = new();
        SeedState(primary, dedupActive: true,
            veryLongStrings: [new LongStringEntry(0x1000, 5000, 10026)]);

        StringAnalyzer worker = new();
        SeedState(worker, dedupActive: true,
            veryLongStrings: [new LongStringEntry(0x2000, 6000, 12026)]);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetVeryLongStrings(primary).Should().HaveCount(2);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static object MakeFingerprint(ulong hash, int length, char first, char last) =>
        Activator.CreateInstance(FingerprintType, hash, length, first, last)!;

    // Creates a Dictionary<StringFingerprint, StringLeakInfo> via reflection and populates it.
    private static object MakeStatsDict((object key, StringLeakInfo val)[] entries)
    {
        var dict = (System.Collections.IDictionary)Activator.CreateInstance(StatsDictType)!;
        foreach (var (key, val) in entries)
            dict[key] = val;
        return dict;
    }

    private static int GetCount(System.Collections.IDictionary dict, object key)
    {
        if (!dict.Contains(key)) return 0;
        return ((StringLeakInfo)dict[key]!).Count;
    }

    private static void SeedState(
        StringAnalyzer analyzer,
        bool dedupActive,
        object? stringStats = null,
        Dictionary<string, int>? buckets = null,
        List<LongStringEntry>? veryLongStrings = null,
        int stringsRead = 0)
    {
        Type t = typeof(StringAnalyzer);
        Set(t, analyzer, "_indexScanDedupActive", dedupActive);
        if (!dedupActive) return;

        Set(t, analyzer, "_indexScanStringStats", stringStats ?? Activator.CreateInstance(StatsDictType)!);
        Set(t, analyzer, "_indexScanMethodTableDupCounts", new Dictionary<ulong, int>());
        Set(t, analyzer, "_indexScanLengthSamples", new List<int>());
        Set(t, analyzer, "_indexScanLengthBuckets", buckets ?? new Dictionary<string, int>(StringComparer.Ordinal));
        Set(t, analyzer, "_indexScanVeryLongStrings", veryLongStrings ?? new List<LongStringEntry>());
        Set(t, analyzer, "_indexScanStringsRead", stringsRead);
        Set(t, analyzer, "_indexScanMaxUnique", 10_000);
    }

    private static void Set(Type t, object obj, string name, object? val) =>
        t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(obj, val);

    private static T? GetField<T>(StringAnalyzer a, string name) =>
        (T?)typeof(StringAnalyzer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(a);

    private static Dictionary<string, int> GetBuckets(StringAnalyzer a) =>
        GetField<Dictionary<string, int>>(a, "_indexScanLengthBuckets")!;

    private static int GetStringsRead(StringAnalyzer a) =>
        GetField<int>(a, "_indexScanStringsRead");

    private static List<LongStringEntry> GetVeryLongStrings(StringAnalyzer a) =>
        GetField<List<LongStringEntry>>(a, "_indexScanVeryLongStrings")!;
}

