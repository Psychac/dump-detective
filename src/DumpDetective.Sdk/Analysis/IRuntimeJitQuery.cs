using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>One JIT-compiled method, source-neutral — mirrors <c>Runtime.EnumerateJitManagers()</c>'s
/// per-method compilation records.</summary>
public readonly record struct JitCompilationRef(MethodRef Method, bool IsTieredCompilation, ulong NativeCodeAddress);

/// <summary>The <c>runtime.jit</c> capability.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IRuntimeJitQuery
{
    IEnumerable<JitCompilationRef> EnumerateJitCompilations();
}
