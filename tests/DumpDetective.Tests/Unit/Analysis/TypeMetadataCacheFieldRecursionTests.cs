using System.Diagnostics;
using DumpDetective.Analysis.Cache;
using FluentAssertions;
using Microsoft.Diagnostics.Runtime;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Regression coverage for the recursive field-tree check in
/// <see cref="TypeMetadataCache"/> (docs/analysis/retained-size-candidate-selection.md
/// "Filter precision" resolution). A type with zero top-level reference-typed fields can
/// still hold a reference underneath a struct-typed field — <c>ContainsPointers</c> must
/// see it too, matching what <c>ClrObject.EnumerateReferences</c> actually walks.
///
/// Attaches to this test process's own live heap via <c>DataTarget.CreateSnapshotAndAttach</c>
/// instead of loading an external .dmp file, so it is fast, deterministic, and not subject to
/// the real-dump "never run in parallel" rule in CLAUDE.md — that rule targets multi-GB
/// dump files loaded into memory, not a live-process snapshot of this small test process.
/// </summary>
public sealed class TypeMetadataCacheFieldRecursionTests
{
    private struct EntryWithReference
    {
        public string Key;
        public int HashCode;
    }

    private struct EntryWithoutReference
    {
        public int A;
        public int B;
    }

#pragma warning disable CS0414 // fields exist to be present in the heap snapshot, never read in C#
    private sealed class WrapperWithNestedReference
    {
        private EntryWithReference _entry = new() { Key = "regression-test-key", HashCode = 42 };
        private int _count = 1;

        public WrapperWithNestedReference() { }
    }

    private sealed class WrapperWithoutAnyReference
    {
        private EntryWithoutReference _entry = new() { A = 1, B = 2 };
        private long _extra = 99;

        public WrapperWithoutAnyReference() { }
    }
#pragma warning restore CS0414

    // Kept alive as static fields so the objects remain reachable on this process's heap
    // for the snapshot taken in the test below.
    private static WrapperWithNestedReference? s_wrapperWithNestedReference;
    private static WrapperWithoutAnyReference? s_wrapperWithoutAnyReference;

    [Fact]
    public void ContainsPointers_TrueForStructFieldHoldingReference_NotJustTopLevelFields()
    {
        s_wrapperWithNestedReference = new WrapperWithNestedReference();
        s_wrapperWithoutAnyReference = new WrapperWithoutAnyReference();

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        ulong withRefMethodTable = FindMethodTable(heap, nameof(WrapperWithNestedReference));
        ulong withoutRefMethodTable = FindMethodTable(heap, nameof(WrapperWithoutAnyReference));

        withRefMethodTable.Should().NotBe(0UL, "the test object must be reachable on the live heap");
        withoutRefMethodTable.Should().NotBe(0UL, "the test object must be reachable on the live heap");

        var cache = new TypeMetadataCache(() => null);

        // The regression case: zero top-level reference-typed fields on the wrapper itself,
        // but the struct field embeds a string. A non-recursive check would say false here.
        cache.GetOrCreate(heap, withRefMethodTable).ContainsPointers.Should().BeTrue(
            "the struct field's own reference field must be found even though the wrapper " +
            "class has no top-level reference-typed field");

        // Control case: genuinely no reference anywhere in the field tree.
        cache.GetOrCreate(heap, withoutRefMethodTable).ContainsPointers.Should().BeFalse();
    }

    // Known-container fixtures per the "no whitelist needed" resolution: the recursive check
    // must flag these generically (via their array-typed backing fields) without any
    // Dictionary/List-specific special-casing in TypeMetadataCache itself.
    private static Dictionary<int, int>? s_dictionary;
    private static List<int>? s_list;

    [Fact]
    public void ContainsPointers_TrueForKnownBclContainers_NoWhitelistNeeded()
    {
        s_dictionary = new Dictionary<int, int> { [1] = 1 };
        s_list = new List<int> { 1 };

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        ulong dictionaryMethodTable = FindMethodTableByPrefix(heap, "System.Collections.Generic.Dictionary<System.Int32, System.Int32>");
        ulong listMethodTable = FindMethodTableByPrefix(heap, "System.Collections.Generic.List<System.Int32>");

        dictionaryMethodTable.Should().NotBe(0UL, "the test object must be reachable on the live heap");
        listMethodTable.Should().NotBe(0UL, "the test object must be reachable on the live heap");

        var cache = new TypeMetadataCache(() => null);

        cache.GetOrCreate(heap, dictionaryMethodTable).ContainsPointers.Should().BeTrue();
        cache.GetOrCreate(heap, listMethodTable).ContainsPointers.Should().BeTrue();
    }

    private static ulong FindMethodTable(ClrHeap heap, string typeName)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            // Nested private classes report their CLR name as "Outer+Inner" — match by suffix.
            if (obj.Type?.Name is string name && name.EndsWith(typeName, StringComparison.Ordinal))
                return obj.Type.MethodTable;
        }

        return 0;
    }

    private static ulong FindMethodTableByPrefix(ClrHeap heap, string typeNamePrefix)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.Type is ClrType type && type.IsArray == false
                && type.Name is string name && name.StartsWith(typeNamePrefix, StringComparison.Ordinal))
            {
                return type.MethodTable;
            }
        }

        return 0;
    }
}
