using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Reference Chains

internal sealed record ReferenceChainDomainResult(
    int AnalyzedSamples,
    int RetainedSamples,
    double RetainedPercent,
    IReadOnlyList<string>? RetainedTypeNames = null,
    IReadOnlyList<string>? SampleReferenceChains = null,
    IReadOnlyList<ReferenceTypeSampleSnapshot>? TopTypeSampleTraces = null,
    int TraversalLimitedSamples = 0,
    IReadOnlyList<ReferenceChainRootKindCount>? RootKindDistribution = null,
    int NoSampleAddressCount = 0,
    /// <summary>Groups of two or more top types whose representative sample resolved to the same
    /// root object address (E-6, docs/analysis/phase1/reference-chain-analyzer-audit.md) — a
    /// likely retention hub (e.g. a shared cache or singleton) rather than independent leaks.</summary>
    IReadOnlyList<ReferenceChainSharedRootGroup>? SharedRootGroups = null) : AnalyzerDomainResult;

/// <summary>Count of retained types whose sample resolved to <paramref name="RootKind"/>, out of
/// all retained (<c>HasGcRoot</c>) types in <see cref="ReferenceChainDomainResult.TopTypeSampleTraces"/>.</summary>
internal sealed record ReferenceChainRootKindCount(string RootKind, int RetainedTypeCount);

/// <summary>A single root object address shared by <see cref="TypeNames"/>' representative
/// samples — see <see cref="ReferenceChainDomainResult.SharedRootGroups"/> (E-6).</summary>
internal sealed record ReferenceChainSharedRootGroup(ulong RootAddress, string RootKind, IReadOnlyList<string> TypeNames);

internal sealed record ReferenceTypeSampleSnapshot(
    string TypeName,
    int Count,
    ulong TotalSizeBytes,
    ulong? SampleAddress,
    string? SampleObjectType,
    ulong SampleObjectSize,
    bool HasGcRoot,
    string? RootKind,
    string? RootPath,
    IReadOnlyList<string>? PathHops,
    bool TraversalLimited,
    /// <summary>Instances of this type actually probed for a root path (E-1) — the representative
    /// sample above (<see cref="SampleAddress"/>) plus any additional instances found via E-7's
    /// streaming multi-sample lookup. 0 when no sample was available at all; otherwise ≥ 1.</summary>
    int SampleCount = 0,
    /// <summary>Of <see cref="SampleCount"/> probed instances, how many found a GC root.</summary>
    int RetainedSampleCount = 0,
    /// <summary>Most common root kind among this type's retained samples — the "StaticVar" in a
    /// "4/5 StaticVar" consistency read. Null when no sample was retained.</summary>
    string? DominantSampleRootKind = null,
    /// <summary>Of <see cref="SampleCount"/> probed instances, how many resolved to
    /// <see cref="DominantSampleRootKind"/> specifically — the "4" in "4/5 StaticVar".</summary>
    int DominantSampleRootKindCount = 0,
    /// <summary>Exact retained-subgraph bytes (subtree sum, including the sample's own
    /// <see cref="SampleObjectSize"/>) for the representative sample, from the disk-backed
    /// dominator tree (E-2, docs/analysis/phase1/reference-chain-analyzer-audit.md). Null when the
    /// tree is unavailable for this run (in-memory mode, Stage B not gated on, legacy cache.bin, or
    /// the sample wasn't reachable when the tree was built) — <see cref="SampleObjectSize"/> is
    /// still the shallow-size fallback in that case, no separate approximate value is computed.</summary>
    ulong? RetainedBytes = null,
    /// <summary>Address of the actual GC root object the representative sample's path resolved
    /// to (E-6, docs/analysis/phase1/reference-chain-analyzer-audit.md) — the first hop in
    /// <see cref="PathHops"/>. Null when no root was found or no path was captured.</summary>
    ulong? RootAddress = null,
    /// <summary>Static field ("MyApp.Cache._items") or stack frame owner ("MyApp.Worker.Run") that
    /// holds the root reference (E-3, docs/analysis/phase1/reference-chain-analyzer-audit.md) —
    /// resolved from the root's own storage address, not <see cref="RootAddress"/> (which is the
    /// rooted object's address). Null when the root kind has no field-name concept (a GC handle),
    /// or the underlying lookup found nothing.</summary>
    string? RootFieldName = null,
    /// <summary>Instance field on the second-to-last path object that holds the reference to the
    /// last hop (E-3, same doc) — the "closes the primary WinDbg/SOS parity gap" field name. Null
    /// when the path has fewer than two hops (the root points directly at the sample), or the edge
    /// was via an array element/dependent handle rather than a named field.</summary>
    string? LastHopFieldName = null);
