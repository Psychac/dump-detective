using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Types;

/// <summary>
/// Bundles <see cref="StringAnalyzer"/>'s real configuration options together with the P1-3
/// string-field-ownership aggregate <see cref="StringAnalyzerLegacyAdapter"/> computes dump-side
/// during the shared heap-index scan pass — the same "options wrapper carries adapter-precomputed
/// data alongside real options" shape <c>CrashAnalyzerOptions</c> already uses for
/// <c>AggregateExceptionAddresses</c>. This aggregate is fundamentally a whole-heap-population
/// computation (which types, across the *entire* heap, own string-typed fields) with no
/// per-instance SDK-capability expression, so it has exactly one consumer and doesn't warrant a
/// general SDK capability.
/// </summary>
internal sealed record StringAnalyzerOptions(
    StringAnalysisOptions Options,
    /// <summary>Top object types by total string bytes they own via string-typed instance fields
    /// (not duplicate strings — all strings), sorted descending by bytes. Null when the shared scan
    /// didn't run or found no string-owning types.</summary>
    IReadOnlyList<(string TypeName, ulong TotalBytes)>? OwnerTypeBytes);
