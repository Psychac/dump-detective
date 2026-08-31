using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// R10 (docs/analysis/phase1/DbConnectionAnalyzer-audit.md): shared helper for tests that need a
/// real <see cref="ClrHeap"/>/<see cref="ClrObject"/> to exercise ClrMD field-reading code,
/// without loading an external .dmp file. Attaches to this test process's own live heap — fast,
/// deterministic, and exempt from the "never run real-dump tests in parallel" rule in CLAUDE.md
/// (that rule targets multi-GB loaded dump files, not a live-process snapshot of this small test
/// process). Extracted from the pattern already used by
/// <see cref="TypeMetadataCacheFieldRecursionTests"/>.
/// </summary>
internal static class LiveHeapSnapshotFixture
{
    public static (DataTarget DataTarget, ClrHeap Heap) AttachToSelf()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        return (dataTarget, runtime.Heap);
    }

    /// <summary>Finds the MethodTable of a live object whose resolved type name ends with
    /// <paramref name="typeNameSuffix"/> (nested/private types report "Outer+Inner").</summary>
    public static ulong FindMethodTable(ClrHeap heap, string typeNameSuffix)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.Type?.Name is string name && name.EndsWith(typeNameSuffix, StringComparison.Ordinal))
                return obj.Type.MethodTable;
        }
        return 0;
    }

    /// <summary>Finds the address of a live object whose resolved type name ends with
    /// <paramref name="typeNameSuffix"/>.</summary>
    public static ulong FindObjectAddress(ClrHeap heap, string typeNameSuffix)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.Type?.Name is string name && name.EndsWith(typeNameSuffix, StringComparison.Ordinal))
                return obj.Address;
        }
        return 0;
    }

    /// <summary>
    /// Attaches and locates a fixture object by type-name suffix in one call, retrying the whole
    /// attach when the object isn't found. <c>DataTarget.CreateSnapshotAndAttach</c> occasionally
    /// races against this process's own state (observed empirically: reliable one test at a time,
    /// but running several self-attach tests back-to-back can intermittently produce a snapshot
    /// where the target object's MethodTable resolves to 0) — this is a snapshot-timing issue in
    /// the attach itself, not a signal about the object under test, so retrying the attach (not
    /// just the search) is the correct fix.
    /// </summary>
    public static (DataTarget DataTarget, ClrHeap Heap, ulong MethodTable, ulong Address) AttachAndFind(
        string typeNameSuffix, int maxAttempts = 3)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            (DataTarget dataTarget, ClrHeap heap) = AttachToSelf();
            ulong methodTable = FindMethodTable(heap, typeNameSuffix);
            ulong address = FindObjectAddress(heap, typeNameSuffix);

            if (methodTable != 0 && address != 0)
                return (dataTarget, heap, methodTable, address);

            dataTarget.Dispose();
        }

        throw new InvalidOperationException(
            $"Fixture object with type-name suffix '{typeNameSuffix}' was not found on the live heap after {maxAttempts} attempts.");
    }
}
