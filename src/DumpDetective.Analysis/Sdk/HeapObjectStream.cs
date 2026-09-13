using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Live-default dump-side implementation of the <c>heap.objects</c> streaming surface — the whole
/// heap, address order, no filtering. Added 2026-09-13 for <c>DominatorAnalyzer</c>'s retyping: its
/// no-index fallback pass needs to stream every heap object when no disk-backed heap index exists
/// for this session, which is a different shape from every prior <c>IHeapObjectStream</c> consumer
/// (a heap-index <em>scan participant</em>, only invoked when the pipeline's shared disk-index scan
/// actually runs — see <see cref="LegacyAnalysisContextTranslator"/>'s own remarks on why
/// <c>heapObjectsOverride</c> exists). Prefers the disk-backed index when one is present (a
/// sequential index read, no per-object ClrMD field walk) — mirrors the pre-retyping analyzer's own
/// <c>EnumerateLeakEntries</c> fork exactly, falling back to a live <c>ClrHeap.EnumerateObjects()</c>
/// walk only when no index exists (in-memory mode, or before Phase 1's scan ran).
/// </summary>
internal sealed class HeapObjectStream(ClrHeap heap, IHeapAnalysisCache cache) : IHeapObjectStream
{
    public IEnumerable<HeapObjectRef> EnumerateObjects()
    {
        if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out _))
        {
            foreach (HeapEntry entry in heapCache.EnumerateIndexedEntries())
            {
                string name = entry.MethodTable != 0
                    ? heap.GetTypeByMethodTable(entry.MethodTable)?.Name ?? $"MT:0x{entry.MethodTable:x}"
                    : "(unknown)";

                yield return new HeapObjectRef(
                    entry.Address, SdkTypeRefFactory.FromName(name, entry.MethodTable == 0 ? null : entry.MethodTable), entry.Size, name);
            }

            yield break;
        }

        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null)
                continue;

            ulong methodTable = obj.Type.MethodTable;
            if (methodTable == 0)
                continue;

            string name = obj.Type.Name ?? $"MT:0x{methodTable:x}";
            yield return new HeapObjectRef(obj.Address, SdkTypeRefFactory.FromName(name, methodTable), obj.Size, name, IsFree: obj.IsFree);
        }
    }
}
