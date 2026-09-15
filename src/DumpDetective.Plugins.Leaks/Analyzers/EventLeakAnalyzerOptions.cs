using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Bundles <see cref="EventLeakAnalyzer"/>'s real configuration options together with
/// <see cref="EventLeakAnalyzerLegacyAdapter"/>'s finished, per-group scan result — the same
/// "options wrapper carries adapter-precomputed data alongside real options" shape
/// <c>CrashAnalyzerOptions</c>/<c>StringAnalyzerOptions</c> already use. The scan itself
/// (<c>PublisherRegistry</c>/<c>EventLeakFastScanner</c>/delegate-chain walking) is a deliberately
/// dump-side, perf-sensitive concern with no per-instance SDK-capability expression — see
/// <see cref="EventLeakAnalyzerLegacyAdapter"/>'s own remarks — so it has exactly one consumer and
/// doesn't warrant a general SDK capability.
/// </summary>
internal sealed record EventLeakAnalyzerOptions(
    EventLeakOptions Options,
    /// <summary>Every publisher/event group the shared scan found, with fully-resolved subscriber
    /// type/method names, disposal state, and root hints already populated — exactly what the
    /// pre-retyping analyzer's own <c>FindEventLeaks</c> produced.</summary>
    List<EventGroupInfo> GroupedLeaks,
    int EventsScanned,
    int PublisherInstances,
    int CandidatePublisherTypeCount,
    int LeakingPublisherTypeCount);
