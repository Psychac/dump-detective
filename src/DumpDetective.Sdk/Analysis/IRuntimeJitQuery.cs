namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// The <c>runtime.jit</c> capability — JIT code heap usage. Redesigned 2026-09-11 for
/// <c>JitAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md);
/// the original shape (<c>EnumerateJitCompilations</c> returning a flat <c>JitCompilationRef</c> per
/// method) had zero real consumers and didn't match what the one analyzer that actually needs this
/// capability does — <c>JitAnalyzer</c> never enumerates JIT-compiled methods directly at all; every
/// per-method fact it reports (signature, hot/cold size, tiering, R2R) comes from walking thread
/// stacks instead (see <see cref="IRuntimeThreadQuery"/>). What it does need from JIT managers
/// themselves is exactly these two heap-level totals.
/// </summary>
public interface IRuntimeJitQuery
{
    /// <summary>Mirrors <c>ClrRuntime.EnumerateJitManagers()</c>'s count.</summary>
    int JitManagerCount { get; }

    /// <summary>Sum of every JIT manager's native code heap byte length
    /// (<c>ClrJitManager.EnumerateNativeHeaps()</c>'s <c>MemoryRange.Length</c>).</summary>
    ulong TotalJitHeapBytes { get; }
}
