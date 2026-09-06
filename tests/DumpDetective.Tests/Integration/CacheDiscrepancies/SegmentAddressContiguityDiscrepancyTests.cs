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

    /// <summary>
    /// The second half of the global-monotonicity guarantee. The test above proves each segment
    /// yields ascending addresses; this proves segments occupy disjoint ranges, so concatenating
    /// them in ascending <c>Start</c> order — which <c>DiskBackedObjectIndexWriter</c> now sorts
    /// into — produces a strictly ascending <c>ObjectAddresses</c> column for the whole heap.
    /// </summary>
    /// <remarks>
    /// Both halves are needed. Sorting by <c>Start</c> alone would not give a monotonic column if
    /// two segments overlapped: the tail of the earlier one would exceed the head of the later.
    /// Global monotonicity is what makes <c>address -> row</c> a rank query instead of a hash
    /// lookup, which is worth 2,325.9 MB — see docs/cache/cache-ideal-design.md §3.1 R1 and §7.2.
    ///
    /// Deliberately asserted rather than assumed: the previous design took ClrMD's segment order as
    /// given and the column happened to come out ascending on both reference dumps. That is an
    /// observation about two dumps, not a property of the format.
    /// </remarks>
    [DiscrepancyFact]
    public void Segments_SortedByStart_AreDisjoint_SoTheConcatenatedColumnIsGloballyAscending()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        ClrSegment[] segments = heap.Segments.ToArray();
        Array.Sort(segments, static (a, b) => a.Start.CompareTo(b.Start));

        var violations = new List<string>();
        int alreadyAscending = 0;

        ClrSegment[] asClrMdReturnedThem = heap.Segments.ToArray();
        for (int i = 1; i < asClrMdReturnedThem.Length; i++)
            if (asClrMdReturnedThem[i].Start > asClrMdReturnedThem[i - 1].Start)
                alreadyAscending++;

        for (int i = 1; i < segments.Length; i++)
        {
            ClrSegment previous = segments[i - 1];
            ClrSegment current = segments[i];

            if (current.Start < previous.End)
            {
                violations.Add(
                    $"segment {current.Kind} [0x{current.Start:X}-0x{current.End:X}] overlaps " +
                    $"{previous.Kind} [0x{previous.Start:X}-0x{previous.End:X}]");
            }
        }

        _output.WriteLine($"segments: {segments.Length}");
        _output.WriteLine($"already ascending as ClrMD returned them: {alreadyAscending}/{Math.Max(0, segments.Length - 1)} adjacent pairs");
        _output.WriteLine($"overlaps after sorting by Start: {violations.Count}");
        foreach (string v in violations.Take(20))
            _output.WriteLine(v);

        violations.Should().BeEmpty(
            $"checked {segments.Length} segments; an overlap would break global monotonicity of the " +
            "ObjectAddresses column even with segments sorted by Start, and with it the rank-query " +
            "address lookup in docs/cache/cache-ideal-design.md §3.1 R1");
    }
}
