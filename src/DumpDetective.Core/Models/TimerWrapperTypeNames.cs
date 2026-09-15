namespace DumpDetective.Core.Models;

/// <summary>
/// Exact-match timer wrapper/queue type names (excludes the generic "OtherTimer"
/// namespace/token fallback classification). Shared vocabulary, not analyzer-run data — owned here
/// so <c>TimerLeakAnalyzer</c> (which classifies these types) and <c>LeakCandidateAnalyzer</c>
/// (which correlates candidate rows against them without duplicating the literals) can each
/// reference it without either analyzer's package depending on the other's.
/// </summary>
public static class TimerWrapperTypeNames
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Threading.Timer",
        "System.Timers.Timer",
        "System.Threading.TimerQueueTimer",
        "System.Threading.TimerHolder",
        "System.Threading.PeriodicTimer",
    };
}
