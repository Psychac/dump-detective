using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Runtime;

/// <summary>
/// <c>Sdk.Analysis.AnalysisContext.AnalyzerOptions</c> is a single untyped slot, but
/// <see cref="CrashAnalyzer"/> needs both its own legacy <see cref="CrashAnalysisOptions"/> and a
/// side-channel set of addresses <see cref="CrashAnalyzerLegacyAdapter"/> already determined are
/// <c>AggregateException</c> instances — structural type detection
/// (<c>ClrType.BaseType</c> chain walk) has no Tier-1/Tier-2 SDK surface, and none exists for it
/// elsewhere yet (hard-need-basis), so the adapter computes it once during the shared heap-index
/// scan (where it already has real <c>ClrType</c> access) and hands the result down instead of the
/// retyped analyzer re-deriving structural type facts it has no capability to ask for. Same "small
/// wrapper record for one slot" shape <c>Types.CollectionAnalyzerOptions</c> already established.
/// </summary>
internal sealed record CrashAnalyzerOptions(CrashAnalysisOptions Options, IReadOnlySet<ulong> AggregateExceptionAddresses);
