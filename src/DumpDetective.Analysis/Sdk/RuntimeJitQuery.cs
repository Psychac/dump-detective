using DumpDetective.Sdk.Analysis;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>runtime.jit</c> capability, built for <c>JitAnalyzer</c>'s
/// retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Both members are
/// computed eagerly in the constructor — <c>ClrRuntime.EnumerateJitManagers()</c> is a live,
/// non-cached ClrMD walk, and this capability's only consumer reads both values exactly once per
/// run, so there's no reason to re-walk on a second property read.
/// </summary>
internal sealed class RuntimeJitQuery : IRuntimeJitQuery
{
    public int JitManagerCount { get; }
    public ulong TotalJitHeapBytes { get; }

    public RuntimeJitQuery(ClrRuntime runtime)
    {
        int managerCount = 0;
        ulong totalBytes = 0;
        foreach (ClrJitManager mgr in runtime.EnumerateJitManagers())
        {
            managerCount++;
            foreach (ClrNativeHeapInfo heap in mgr.EnumerateNativeHeaps())
                totalBytes += heap.MemoryRange.Length;
        }

        JitManagerCount = managerCount;
        TotalJitHeapBytes = totalBytes;
    }
}
