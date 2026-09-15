using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Bundles <see cref="LeakCandidateAnalyzer"/>'s real configuration options together with the three
/// cross-analyzer signals <c>LeakCandidateAnalyzer.BuildAnalysisContext</c> extracts from
/// <c>context.CompletedRunResults</c> (via the structural signal interfaces in
/// <c>DumpDetective.Core.Models</c>) before translating to the SDK context — the same
/// "options wrapper carries adapter-precomputed data alongside real options" shape
/// <c>EventLeakAnalyzerOptions</c>/<c>CrashAnalyzerOptions</c> already use. See
/// <see cref="LeakCandidateAnalyzer"/>'s own remarks for why this exists instead of a
/// <c>CompletedRunResults</c>-shaped SDK capability.
/// </summary>
internal sealed record LeakCandidateAnalyzerOptions(
    ReferenceChainOptions ReferenceChainOptions,
    IReadOnlySet<string> PinnedTargetTypes,
    IReadOnlySet<string> DependentTargetTypes,
    int LogicalTimerCount,
    IReadOnlyDictionary<ulong, DirectRootEntry>? DirectRootsByTargetAddress)
{
    public static readonly LeakCandidateAnalyzerOptions Empty = new(
        new ReferenceChainOptions(), new HashSet<string>(), new HashSet<string>(), LogicalTimerCount: 0, DirectRootsByTargetAddress: null);
}
