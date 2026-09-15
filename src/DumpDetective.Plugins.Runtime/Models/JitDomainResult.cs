using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// JIT & Native Code

internal sealed record JitMethodSnapshot(
    string Signature,
    string DeclaringType,
    ulong NativeCodeAddress,
    uint HotSize,
    uint ColdSize,
    bool IsTiered,
    bool IsReadyToRun);

/// <param name="DistinctMethodsOnStacks">
/// Distinct methods observed on stacks, keyed by <c>ClrMethod.MethodDesc</c> (only methods with a
/// resolved <c>NativeCode</c> are counted). <see cref="ActiveMethodsOnStacks"/> divided by this value
/// is a reuse ratio: 1.0 means every observed frame is a different method, higher values mean the
/// same methods recur across many threads (hot-path concentration).
/// </param>
/// <param name="ReadyToRunFrameCount">
/// Count of managed frames whose method has <c>ClrMethod.CompilationType == MethodCompilationType.Ngen</c>
/// — the DAC has no dedicated ReadyToRun value and reports precompiled methods (R2R images included)
/// under the legacy Ngen classification. A subset of <see cref="ManagedFrameCount"/>.
/// </param>
/// <param name="DynamicMethodFrameCount">
/// Count of managed frames whose declaring type lives in a dynamic module (<c>ClrModule.IsDynamic</c>) —
/// covers <c>System.Reflection.Emit.DynamicMethod</c>, <c>AssemblyBuilder</c>-emitted types, and
/// compiled LINQ expression trees. A subset of <see cref="ManagedFrameCount"/>.
/// </param>
/// <param name="TieredMethodCount">
/// Estimate, not an exact count: keyed on <c>ClrMethod.MethodDesc</c>, and only methods that
/// appear on a live thread stack are observed at all, so tiered methods with no currently-live
/// frame are invisible to this count.
/// </param>
/// <param name="MaxThreadFrameDepth">
/// Deepest thread stack observed (managed + unmanaged frames combined), across all live threads.
/// An unusually deep stack is a signal for unbounded recursion or re-entrant call chains.
/// </param>
/// <param name="MaxThreadFrameDepthOSThreadId">OS thread ID owning <see cref="MaxThreadFrameDepth"/>.</param>
/// <param name="TopActiveModulesByFrameHits">
/// Active managed frames aggregated by declaring module name (<c>ClrModule.Name</c>) instead of by
/// type — a per-module JIT stack heatmap. Keyed the same way as
/// <c>LoadedModuleSnapshot.Name</c>/<c>ModuleHeapStats.ModuleName</c>, so it can be joined against
/// <c>ModuleDomainResult</c> by name for cross-analyzer correlation.
/// </param>
internal sealed record JitDomainResult(
    ulong TotalJitHeapBytes,
    int JitManagerCount,
    int ActiveMethodsOnStacks,
    int DistinctMethodsOnStacks,
    IReadOnlyList<JitMethodSnapshot> TopLargestMethods,
    IReadOnlyList<NameCountEntry> TopActiveFrameTypes,
    IReadOnlyList<NameCountEntry> TopActiveModulesByFrameHits,
    int UnmanagedFrameCount,
    int ManagedFrameCount,
    int ReadyToRunFrameCount,
    int DynamicMethodFrameCount,
    int TieredMethodCount,
    int MaxThreadFrameDepth,
    uint MaxThreadFrameDepthOSThreadId,
    uint LargeMethodThresholdBytes) : AnalyzerDomainResult;
