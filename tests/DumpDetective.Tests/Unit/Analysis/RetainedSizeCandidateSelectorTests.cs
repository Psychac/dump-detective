using System.Linq;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using FluentAssertions;
using Microsoft.Diagnostics.Runtime;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Regression coverage for <see cref="RetainedSizeCandidateSelector"/>
/// (docs/analysis/retained-size-candidate-selection.md Phase 1): shape-filter skip for
/// pointer-free types, budget-bounded walk for the rest, shared-visited-set exclusivity.
///
/// Attaches to this test process's own live heap via <c>DataTarget.CreateSnapshotAndAttach</c>,
/// same pattern as <see cref="TypeMetadataCacheFieldRecursionTests"/> — fast, deterministic,
/// not subject to the real-dump "never run in parallel" rule in CLAUDE.md.
/// </summary>
public sealed class RetainedSizeCandidateSelectorTests
{
    private struct EntryWithReference
    {
        public string Key;
    }

    private sealed class WrapperWithReference
    {
        private EntryWithReference _entry = new() { Key = "retained-size-selector-key" };
        public WrapperWithReference() { }
    }

    private static byte[]? s_byteArray;
    private static WrapperWithReference? s_wrapper;

    private static IHeapAnalysisCache NewCache() => new HeapAnalysisCache();

    [Fact]
    public void RequiresWalk_FalseForByteArray_TrueForTypeWithNestedReference()
    {
        s_byteArray = new byte[64];
        s_wrapper = new WrapperWithReference();

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        ulong byteArrayMt = FindMethodTable(heap, obj => obj.Type?.IsArray == true && obj.Type.ComponentType?.Name == "System.Byte");
        ulong wrapperMt = FindMethodTable(heap, obj => obj.Type?.Name?.EndsWith(nameof(WrapperWithReference), StringComparison.Ordinal) == true);

        byteArrayMt.Should().NotBe(0UL);
        wrapperMt.Should().NotBe(0UL);

        IHeapAnalysisCache cache = NewCache();

        RetainedSizeCandidateSelector.RequiresWalk(cache, heap, byteArrayMt).Should().BeFalse();
        RetainedSizeCandidateSelector.RequiresWalk(cache, heap, wrapperMt).Should().BeTrue();
    }

    [Fact]
    public void RequiresWalk_FalseForZeroMethodTable()
    {
        IHeapAnalysisCache cache = NewCache();
        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

        RetainedSizeCandidateSelector.RequiresWalk(cache, runtime.Heap, 0).Should().BeFalse();
    }

    [Fact]
    public void SelectAndCompute_SkipsBfsForPointerFreeCandidate_ReturnsShallowSizeUnwalked()
    {
        s_byteArray = new byte[64];

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        ulong byteArrayAddr = FindAddress(heap, obj => obj.Type?.IsArray == true && obj.Type.ComponentType?.Name == "System.Byte" && obj.Size >= 64);
        byteArrayAddr.Should().NotBe(0UL);

        ClrObject byteArrayObj = heap.GetObject(byteArrayAddr);
        ulong shallowSize = byteArrayObj.Size;

        IHeapAnalysisCache cache = NewCache();
        var visited = new HashSet<ulong>();
        var candidates = new List<(ulong Address, ulong MethodTable, ulong ShallowSize)>
        {
            (byteArrayAddr, byteArrayObj.Type!.MethodTable, shallowSize)
        };

        IReadOnlyList<RetainedSizeResult> results = RetainedSizeCandidateSelector.SelectAndCompute(
            candidates, heap, cache, visited, maxCandidatesToWalk: 10);

        results.Should().HaveCount(1);
        results[0].WasWalked.Should().BeFalse();
        results[0].RetainedSize.Should().Be(shallowSize);
        visited.Should().BeEmpty("a skipped-walk candidate must not consume the shared visited set");
    }

    [Fact]
    public void SelectAndCompute_WalksPointerCandidate_RetainedSizeAtLeastShallowSize()
    {
        s_wrapper = new WrapperWithReference();

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        ulong wrapperAddr = FindAddress(heap, obj => obj.Type?.Name?.EndsWith(nameof(WrapperWithReference), StringComparison.Ordinal) == true);
        wrapperAddr.Should().NotBe(0UL);

        ClrObject wrapperObj = heap.GetObject(wrapperAddr);
        ulong shallowSize = wrapperObj.Size;

        IHeapAnalysisCache cache = NewCache();
        var visited = new HashSet<ulong>();
        var candidates = new List<(ulong Address, ulong MethodTable, ulong ShallowSize)>
        {
            (wrapperAddr, wrapperObj.Type!.MethodTable, shallowSize)
        };

        IReadOnlyList<RetainedSizeResult> results = RetainedSizeCandidateSelector.SelectAndCompute(
            candidates, heap, cache, visited, maxCandidatesToWalk: 10);

        results.Should().HaveCount(1);
        results[0].WasWalked.Should().BeTrue();
        results[0].RetainedSize.Should().BeGreaterThanOrEqualTo(shallowSize,
            "the wrapper's retained size includes the referenced string it points to");
        visited.Should().Contain(wrapperAddr);
    }

    [Fact]
    public void SelectAndCompute_RespectsMaxCandidatesToWalk_RankedByShallowSizeDescending()
    {
        s_wrapper = new WrapperWithReference();
        var secondWrapper = new WrapperWithReferenceLarge();
        GC.KeepAlive(secondWrapper);

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        ulong smallAddr = FindAddress(heap, obj => obj.Type?.Name?.EndsWith(nameof(WrapperWithReference), StringComparison.Ordinal) == true);
        ulong largeAddr = FindAddress(heap, obj => obj.Type?.Name?.EndsWith(nameof(WrapperWithReferenceLarge), StringComparison.Ordinal) == true);

        smallAddr.Should().NotBe(0UL);
        largeAddr.Should().NotBe(0UL);

        ClrObject smallObj = heap.GetObject(smallAddr);
        ClrObject largeObj = heap.GetObject(largeAddr);

        IHeapAnalysisCache cache = NewCache();
        var visited = new HashSet<ulong>();
        var candidates = new List<(ulong Address, ulong MethodTable, ulong ShallowSize)>
        {
            (smallAddr, smallObj.Type!.MethodTable, smallObj.Size),
            (largeAddr, largeObj.Type!.MethodTable, largeObj.Size),
        };

        IReadOnlyList<RetainedSizeResult> results = RetainedSizeCandidateSelector.SelectAndCompute(
            candidates, heap, cache, visited, maxCandidatesToWalk: 1);

        RetainedSizeResult smallResult = results.Single(r => r.Address == smallAddr);
        RetainedSizeResult largeResult = results.Single(r => r.Address == largeAddr);

        // Budget of 1 must go to the larger-by-shallow-size candidate, not the smaller one.
        largeResult.WasWalked.Should().BeTrue();
        smallResult.WasWalked.Should().BeFalse();
        smallResult.RetainedSize.Should().Be(smallObj.Size);
    }

    private struct EntryPadding
    {
        public string Key;
        public long Pad0, Pad1, Pad2, Pad3, Pad4, Pad5, Pad6, Pad7;
    }

    private sealed class WrapperWithReferenceLarge
    {
        private EntryPadding _entry = new() { Key = "retained-size-selector-large-key" };
        public WrapperWithReferenceLarge() { }
    }

    private static ulong FindMethodTable(ClrHeap heap, Func<ClrObject, bool> predicate)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (predicate(obj))
                return obj.Type!.MethodTable;
        }

        return 0;
    }

    private static ulong FindAddress(ClrHeap heap, Func<ClrObject, bool> predicate)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (predicate(obj))
                return obj.Address;
        }

        return 0;
    }
}
