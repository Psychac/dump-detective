using Microsoft.Diagnostics.Runtime;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 0 validation for docs/cache/cache-architecture.md: the proposed SegmentIndex
/// binary-search lookup relies on every GC segment yielding objects from
/// <see cref="ClrSegment.EnumerateObjects"/> in strictly increasing address order. This is a load-
/// bearing assumption for the whole design, not something to assume from GC folklore — this test
/// walks a real dump's segments and asserts it holds for every segment kind encountered.
/// </summary>
public sealed class SegmentAddressContiguityDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public SegmentAddressContiguityDiscrepancyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public void EverySegment_YieldsObjects_InStrictlyIncreasingAddressOrder()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var kindsSeen = new HashSet<GCSegmentKind>();
        var violations = new List<string>();
        int segmentsChecked = 0;
        long objectsChecked = 0;

        foreach (ClrSegment segment in heap.Segments)
        {
            segmentsChecked++;
            kindsSeen.Add(segment.Kind);

            ulong previousAddress = 0;
            bool first = true;

            foreach (ClrObject obj in segment.EnumerateObjects())
            {
                objectsChecked++;

                if (!first && obj.Address <= previousAddress)
                {
                    violations.Add(
                        $"segment {segment.Kind} [0x{segment.Start:X}-0x{segment.End:X}]: " +
                        $"address 0x{obj.Address:X} did not increase from previous 0x{previousAddress:X}");
                }

                if (obj.Address < segment.Start || obj.Address >= segment.End)
                {
                    violations.Add(
                        $"segment {segment.Kind} [0x{segment.Start:X}-0x{segment.End:X}]: " +
                        $"address 0x{obj.Address:X} is outside the segment's own bounds");
                }

                previousAddress = obj.Address;
                first = false;
            }
        }

        // Surface what was actually exercised so a future reader can tell whether this run covered
        // the segment kinds the design cares about (Gen0/1/2, Ephemeral, Large, Pinned) or whether
        // a different dump is needed to validate the remaining kinds.
        string kindsSummary = string.Join(", ", kindsSeen);
        _output.WriteLine($"segments checked: {segmentsChecked}");
        _output.WriteLine($"segment kinds seen: {kindsSummary}");
        _output.WriteLine($"objects checked: {objectsChecked}");
        _output.WriteLine($"violations: {violations.Count}");
        foreach (string v in violations.Take(20))
            _output.WriteLine(v);

        violations.Should().BeEmpty(
            $"checked {segmentsChecked} segments ({kindsSummary}), {objectsChecked} objects total; " +
            "any violation invalidates the SegmentIndex binary-search design in docs/cache/cache-architecture.md");
    }
}
