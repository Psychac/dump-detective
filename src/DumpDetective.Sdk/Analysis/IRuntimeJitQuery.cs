using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>One JIT-compiled method, source-neutral — mirrors <c>Runtime.EnumerateJitManagers()</c>'s
/// per-method compilation records.</summary>
public readonly record struct JitCompilationRef(MethodRef Method, bool IsTieredCompilation, ulong NativeCodeAddress);

/// <summary>The <c>runtime.jit</c> capability.</summary>
public interface IRuntimeJitQuery
{
    IEnumerable<JitCompilationRef> EnumerateJitCompilations();
}
