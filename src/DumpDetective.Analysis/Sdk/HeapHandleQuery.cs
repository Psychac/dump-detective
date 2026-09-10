using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.handles</c> capability, built for
/// <c>GCHandleAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// Reuses the same three-tier handle source the pre-retyping analyzer did, unchanged: the in-memory
/// Phase-1 handle snapshot, then the disk-backed/live-heap <see cref="IHandleSnapshotReader"/>, then
/// a raw <c>runtime.EnumerateHandles()</c> walk as the last resort.
/// </summary>
internal sealed class HeapHandleQuery(ClrRuntime runtime, ClrHeap heap, IHeapAnalysisCache cache) : IHeapHandleQuery
{
    public IEnumerable<HeapHandleRef> EnumerateHandles()
    {
        // OPT-#9 (pre-retyping): cache method-table -> type-name to avoid one heap.GetObject-class
        // call per handle for handles whose target type has already been resolved. Scoped to one
        // EnumerateHandles call, matching the pre-retyping analyzer's own per-run scope.
        var methodTableNameCache = new Dictionary<ulong, string>(capacity: 128);

        HeapIndexBuildResult? heapIndex = null;
        if (cache is HeapAnalysisCache heapCache)
            heapCache.TryGetHeapIndex(out heapIndex);

        if (heapIndex is not null && heapIndex.InMemoryHandleSnapshot is { Length: > 0 } inMemHandles)
        {
            foreach (var rec in inMemHandles)
                yield return ToRef(rec.Addr, rec.Mt, rec.Kind, rec.DependentTarget, methodTableNameCache);
            yield break;
        }

        IHandleSnapshotReader? reader = null;
        if (heapIndex is not null && heapIndex.StorageKind == HeapIndexStorageKind.Disk && heapIndex.IndexPath?.Length > 0)
            reader = HandleSnapshotProvider.CreateFromDiskIfExists(heapIndex.IndexPath);
        reader ??= HandleSnapshotProvider.CreateMemoryReader(runtime, heap, int.MaxValue);

        if (reader is not null)
        {
            using (reader)
            {
                foreach (HandleRecord rec in reader.EnumerateRecords(CancellationToken.None))
                    yield return ToRef(rec.Address, rec.MethodTable, rec.Kind, rec.DependentTarget, methodTableNameCache);
            }
            yield break;
        }

        // Unreachable through the legacy adapter (heap/runtime are always non-null there), kept for
        // parity with the pre-retyping analyzer's own last-resort path.
        foreach (ClrHandle handle in runtime.EnumerateHandles())
        {
            ulong dependentTarget = 0;
            if (handle.HandleKind == ClrHandleKind.Dependent)
                DependentHandleTargetResolver.TryGetDependentTargetAddress(handle, out dependentTarget);
            yield return ToRef(GetTargetAddress(handle), 0, (byte)handle.HandleKind, dependentTarget, methodTableNameCache);
        }
    }

    private HeapHandleRef ToRef(ulong targetAddress, ulong methodTable, byte kindByte, ulong dependentTarget, Dictionary<ulong, string> methodTableNameCache)
    {
        HeapHandleKind kind = ToHeapHandleKind((ClrHandleKind)kindByte);
        string typeName = ResolveTypeNameFromRecord(targetAddress, methodTable, methodTableNameCache);
        return new HeapHandleRef(kind, targetAddress, typeName, dependentTarget == 0 ? null : dependentTarget);
    }

    private string ResolveTypeNameFromRecord(ulong targetAddress, ulong methodTable, Dictionary<ulong, string> methodTableNameCache)
    {
        if (targetAddress == 0)
            return "";

        if (methodTable != 0 && methodTableNameCache.TryGetValue(methodTable, out string? cached))
            return cached;

        ClrType? type = methodTable != 0 ? heap.GetTypeByMethodTable(methodTable) : null;
        if (type is null)
            return $"Object@0x{targetAddress:X}";

        string name = type.Name ?? "Unknown";
        if (methodTable != 0)
            methodTableNameCache[methodTable] = name;

        return name;
    }

    private static ulong GetTargetAddress(ClrHandle handle)
    {
        object boxedTarget = handle.Object;

        if (boxedTarget is ClrObject clrObject)
            return clrObject.IsValid ? clrObject.Address : 0;

        if (boxedTarget is ulong address)
            return address;

        return 0;
    }

    private static HeapHandleKind ToHeapHandleKind(ClrHandleKind kind) => kind switch
    {
        ClrHandleKind.WeakShort => HeapHandleKind.WeakShort,
        ClrHandleKind.WeakLong => HeapHandleKind.WeakLong,
        ClrHandleKind.Strong => HeapHandleKind.Strong,
        ClrHandleKind.Pinned => HeapHandleKind.Pinned,
        ClrHandleKind.RefCounted => HeapHandleKind.RefCounted,
        ClrHandleKind.Dependent => HeapHandleKind.Dependent,
        ClrHandleKind.AsyncPinned => HeapHandleKind.AsyncPinned,
        ClrHandleKind.SizedRef => HeapHandleKind.SizedRef,
        ClrHandleKind.WeakWinRT => HeapHandleKind.WeakWinRT,
        _ => HeapHandleKind.Other,
    };
}
