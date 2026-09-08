using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers.EventLeak;

/// <summary>
/// Discovers the <c>System.MulticastDelegate</c> internal field layout
/// (<c>_target</c> / <c>_invocationList</c> offsets). All MulticastDelegate subclasses share
/// identical offsets, so this only needs to run once per analysis. Moved out of
/// <c>EventLeakFastScanner</c> in Phase 3 (design §3) so both <see cref="PublisherRegistry"/>
/// and <see cref="FieldBackedDelegateShape"/> can share the same discovery logic without
/// depending on scan/construction order.
/// </summary>
internal static class DelegateLayoutDiscovery
{
    public static (int TargetOffset, int InvListOffset, int InvCountOffset) Discover(ClrHeap heap)
    {
        int ptrSize = heap.Runtime.DataTarget.DataReader.PointerSize;

        foreach (ClrAppDomain domain in heap.Runtime.AppDomains)
        {
            foreach (ClrModule module in domain.Modules)
            {
                ClrType? delegateBase = module.GetTypeByName("System.Delegate");
                ClrType? multicastDelegate = module.GetTypeByName("System.MulticastDelegate");

                if ((delegateBase != null || multicastDelegate != null)
                    && TryDiscoverFromType(multicastDelegate ?? delegateBase, ptrSize, out var offsets))
                {
                    return offsets;
                }
            }
        }

        // Fallback: known .NET 6+ 64/32-bit layout if ClrMD type inspection failed (e.g.
        // incomplete symbols). Offsets verified against coreclr source:
        //   System.Delegate:          _target(0) _methodBase(1) _methodPtr(2) _methodPtrAux(3)
        //   System.MulticastDelegate: _invocationList(4) _invocationCount(5)
        //   Absolute = interior + ptrSize, so multiply field-index by ptrSize.
        return (ptrSize, ptrSize * 5, ptrSize * 6);
    }

    /// <summary>
    /// Walks up the delegate type's base-type chain to discover the <c>_target</c> and
    /// <c>_invocationList</c> field offsets. <c>_target</c> is declared on
    /// <c>System.Delegate</c>, while <c>_invocationList</c> is declared on
    /// <c>System.MulticastDelegate</c> — they live on different types in the hierarchy, so
    /// both must be found for a complete layout.
    /// </summary>
    private static bool TryDiscoverFromType(ClrType? delegateType, int ptrSize,
        out (int TargetOffset, int InvListOffset, int InvCountOffset) offsets)
    {
        bool targetFound = false;
        bool invListFound = false;
        int targetOffset = 0, invListOffset = 0, invCountOffset = 0;

        ClrType? cur = delegateType;
        while (cur != null && !(targetFound && invListFound))
        {
            if (!targetFound && cur.Name == "System.Delegate")
            {
                ClrInstanceField? tf = cur.GetFieldByName("_target");
                if (tf != null)
                {
                    targetOffset = tf.Offset + ptrSize;
                    targetFound = true;
                }
            }

            if (!invListFound && cur.Name == "System.MulticastDelegate")
            {
                ClrInstanceField? ilf = cur.GetFieldByName("_invocationList");
                if (ilf != null)
                {
                    invListOffset = ilf.Offset + ptrSize;
                    // _invocationCount is the nint field immediately after _invocationList
                    // (both pointer-sized; always declared consecutively in the CLR source).
                    invCountOffset = invListOffset + ptrSize;
                    invListFound = true;
                }
            }

            cur = cur.BaseType;
        }

        offsets = (targetOffset, invListOffset, invCountOffset);
        return targetFound && invListFound;
    }
}
