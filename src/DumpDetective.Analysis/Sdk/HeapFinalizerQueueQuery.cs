using DumpDetective.Sdk.Analysis;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.finalizer-queue</c> capability, built for
/// <c>FinalizableObjectAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md),
/// its first real consumer. <see cref="IsDisposableType"/>/<see cref="IsCriticalFinalizerType"/> port
/// the pre-retyping analyzer's own two type-shape checks unchanged, each cached per method table
/// (mirroring the pre-retyping analyzer's own <c>isDisposableCache</c>/<c>criticalFinalizerCache</c>
/// dictionaries) — the queue can repeat the same few method tables across many instances (e.g. a
/// thousand leaked <c>Timer</c> objects), so caching here avoids re-walking interface/base-type
/// metadata per instance. Scoped to the finalizer queue's own (bounded) population, not the whole
/// heap's type population — the exact mistake found and fixed in <c>BoxingAnalyzer</c>'s retyping
/// earlier this wave does not apply here by construction.
/// </summary>
internal sealed class HeapFinalizerQueueQuery(ClrHeap heap) : IHeapFinalizerQueueQuery
{
    public IEnumerable<FinalizerQueueObjectRef> EnumerateFinalizerQueue()
    {
        var isDisposableCache = new Dictionary<ulong, bool>();
        var criticalFinalizerCache = new Dictionary<ulong, bool>();

        foreach (ClrObject obj in heap.EnumerateFinalizableObjects())
        {
            if (!obj.IsValid || obj.Type is null)
            {
                yield return new FinalizerQueueObjectRef(
                    obj.Address, IsValid: false, SdkTypeRefFactory.FromName("<unknown>", methodTable: null),
                    "<unknown>", obj.Size, IsDisposableType: false, IsCriticalFinalizerType: false);
                continue;
            }

            ulong mt = obj.Type.MethodTable;
            string typeName = obj.Type.Name ?? "<unknown>";
            bool isDisposable = IsDisposableType(obj.Type, isDisposableCache, mt);
            bool isCriticalFinalizer = IsCriticalFinalizerType(obj.Type, criticalFinalizerCache, mt);

            yield return new FinalizerQueueObjectRef(
                obj.Address, IsValid: true, SdkTypeRefFactory.FromName(typeName, mt), typeName, obj.Size,
                isDisposable, isCriticalFinalizer);
        }
    }

    private static bool IsDisposableType(ClrType type, Dictionary<ulong, bool> cache, ulong methodTable)
    {
        if (cache.TryGetValue(methodTable, out bool cached))
            return cached;

        bool isDisposable = false;
        try
        {
            foreach (ClrInterface iface in type.EnumerateInterfaces())
            {
                if (iface.Name == "System.IDisposable")
                {
                    isDisposable = true;
                    break;
                }
            }
        }
        catch { /* corrupt/unusual type -- defaults to false, matches the pre-retyping analyzer's own defensive posture */ }

        cache[methodTable] = isDisposable;
        return isDisposable;
    }

    // BaseType chain walk for CriticalFinalizerObject. Guaranteed-finalization types (SafeHandle,
    // CriticalHandle, OS-handle wrappers) derive from it.
    private static bool IsCriticalFinalizerType(ClrType? type, Dictionary<ulong, bool> cache, ulong methodTable)
    {
        if (cache.TryGetValue(methodTable, out bool cached))
            return cached;

        bool isCriticalFinalizer = false;
        for (ClrType? cur = type; cur is not null; cur = cur.BaseType)
        {
            if (cur.Name is "System.Runtime.ConstrainedExecution.CriticalFinalizerObject")
            {
                isCriticalFinalizer = true;
                break;
            }
        }

        cache[methodTable] = isCriticalFinalizer;
        return isCriticalFinalizer;
    }
}
