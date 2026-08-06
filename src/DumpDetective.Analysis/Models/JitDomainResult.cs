using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// JIT & Native Code

internal sealed record JitMethodSnapshot(
    string Signature,
    string DeclaringType,
    ulong NativeCodeAddress,
    uint HotSize,
    uint ColdSize,
    bool IsTiered);

internal sealed record JitDomainResult(
    ulong TotalJitHeapBytes,
    int JitManagerCount,
    int ActiveMethodsOnStacks,
    IReadOnlyList<JitMethodSnapshot> TopLargestMethods,
    IReadOnlyList<NameCountEntry> TopActiveFrameTypes,
    int UnmanagedFrameCount,
    int ManagedFrameCount,
    int TieredMethodCount,
    uint LargeMethodThresholdBytes) : AnalyzerDomainResult;
