using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.roots</c> capability, built for <c>GCRootAnalyzer</c>'s
/// retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Delegates to
/// <c>IHeapAnalysisCache.GetOrBuildRootTriples</c>/<c>GetStaticFieldsByRootAddress</c> — the same
/// shared root-set cache <c>GCRootAnalyzer</c> already used (via its concrete-cache path) pre-retyping.
/// </summary>
internal sealed class HeapRootQuery(ClrHeap heap, IHeapAnalysisCache cache) : IHeapRootQuery
{
    public IEnumerable<HeapRootRef> EnumerateRoots()
    {
        IReadOnlyList<(string RootKind, ulong TargetAddr, ulong RootAddr)> roots = cache.GetOrBuildRootTriples(heap);
        if (roots.Count == 0)
            yield break;

        Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)>? staticFieldsByRootAddress = null;

        foreach ((string rootKind, ulong targetAddr, ulong rootAddr) in roots)
        {
            HeapRootKind kind = ToHeapRootKind(rootKind);
            string? ownerTypeName = null;
            string? fieldName = null;
            int? appDomainId = null;

            if (kind is HeapRootKind.StaticVar or HeapRootKind.ThreadStaticVar)
            {
                staticFieldsByRootAddress ??= cache.GetStaticFieldsByRootAddress(heap);
                if (staticFieldsByRootAddress.TryGetValue(rootAddr, out (string TypeName, string FieldName, int AppDomainId) info))
                {
                    ownerTypeName = info.TypeName;
                    fieldName = info.FieldName;
                    appDomainId = info.AppDomainId;
                }
            }

            yield return new HeapRootRef
            {
                Kind = kind,
                TargetAddress = targetAddr,
                RootAddress = rootAddr,
                OwnerTypeName = ownerTypeName,
                FieldName = fieldName,
                AppDomainId = appDomainId,
            };
        }
    }

    public bool TryResolveStackFrameOwner(ulong rootAddress, out string ownerTypeName, out string methodName) =>
        cache.TryResolveStackFrameOwner(heap, rootAddress, out ownerTypeName, out methodName);

    // Mirrors RootIndexReader.KindToString's byte switch one level up, at the string it already
    // produces — GetOrBuildRootTriples exposes the string projection, not the raw byte, so this
    // maps strings rather than re-deriving from ClrRootKind bytes. "None"/"Unknown(n)" (bytes 0
    // and 6+) fall through to Other; per HeapRootKind's own remarks, no real enumerated root is
    // ever byte 0, and byte 6 is unused in ClrMD 4's own ClrRootKind.
    private static HeapRootKind ToHeapRootKind(string kind) => kind switch
    {
        "Stack" => HeapRootKind.Stack,
        "StaticVar" => HeapRootKind.StaticVar,
        "ThreadStaticVar" => HeapRootKind.ThreadStaticVar,
        "FinalizerQueue" => HeapRootKind.FinalizerQueue,
        "StrongHandle" => HeapRootKind.StrongHandle,
        "PinnedHandle" => HeapRootKind.PinnedHandle,
        "AsyncPinnedHandle" => HeapRootKind.AsyncPinnedHandle,
        "RefCountedHandle" => HeapRootKind.RefCountedHandle,
        "SizedRefHandle" => HeapRootKind.SizedRefHandle,
        _ => HeapRootKind.Other,
    };
}
