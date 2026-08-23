using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Opt-in interface for the typed-resource quartet (<c>DbConnectionAnalyzer</c>,
/// <c>WcfChannelAnalyzer</c>, <c>HttpObjectAnalyzer</c>, <c>TimerLeakAnalyzer</c>).
/// Compiler-checked replacement for passing a classification <c>Func&lt;string, bool&gt;</c>
/// lambda into <see cref="TypedResourceCandidateScanner.DiscoverCandidates"/> by convention —
/// implementing this makes the type-classification predicate part of the analyzer's public
/// contract instead of an easy-to-drop closure argument.
/// </summary>
internal interface ITypedResourceCandidateSource
{
    bool IsCandidateType(string typeName);
}

/// <summary>
/// Opt-in interface for the typed-resource quartet members that read a per-instance runtime
/// state field via the shared per-object heap scan (<c>DbConnectionAnalyzer</c>,
/// <c>WcfChannelAnalyzer</c>, <c>HttpObjectAnalyzer</c>). Pairs with
/// <see cref="InstanceStateSampler{TSnapshot}"/>: <see cref="TypedResourceScanDriver"/> enforces
/// that a sample slot is reserved via <see cref="InstanceStateSampler{TSnapshot}.TryReserveSample"/>
/// before <see cref="TrySample"/> is ever called, so implementations can't accidentally read a
/// field for an entry that was never gated. <c>TimerLeakAnalyzer</c> is the fourth quartet member
/// but doesn't implement this — it isn't a heap-scan participant and samples its one evidence
/// instance directly, outside this reserve/top-N mechanism.
/// </summary>
internal interface ITypedResourceInstanceSampler<TSnapshot>
{
    int MaxStateSamplesPerType { get; }
    int TopSampleCap { get; }

    /// <summary>
    /// Reads and classifies the instance at <paramref name="entry"/>'s address. Called only for
    /// entries whose sample slot was already reserved. Returns <c>null</c> if the state field
    /// couldn't be read (invalid/corrupt object) — callers should treat a null return as "no
    /// usable state for this instance" rather than "not interesting enough to keep".
    /// </summary>
    TSnapshot? TrySample(ClrHeap heap, in HeapEntry entry, string typeName);
}
