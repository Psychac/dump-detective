using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Models;

/// <summary>
/// Sampled state from a single timer instance (period, callback owner).
/// </summary>
internal sealed record TimerStateSnapshot(
    ulong Address,
    DumpDetective.Core.Enums.GenerationTag Generation,
    long PeriodMs,
    string? CallbackOwnerType);

/// <summary>
/// Per-type count/size summary for timer-related objects found on the heap.
/// </summary>
internal sealed record TimerObjectTypeSummary(
    string TypeName,
    int Count,
    ulong TotalBytes,
    Evidence? Evidence = null,
    IReadOnlyList<TimerStateSnapshot>? Samples = null);

/// <summary>
/// Domain result produced by <c>TimerLeakAnalyzer</c>.
/// </summary>
internal sealed record TimerLeakDomainResult(
    bool TimersFound,
    int TotalTimers,
    int LogicalTimerCount,
    int ThreadingTimerCount,
    int TimersTimerCount,
    int TimerQueueTimerCount,
    int TimerHolderCount,
    int PeriodicTimerCount,
    int OtherTimerCount,
    ulong TotalBytes,
    IReadOnlyList<TimerObjectTypeSummary> ByType,
    IReadOnlyList<(string Bucket, int Count)> IntervalHistogram)
    : DumpDetective.Core.Models.AnalyzerDomainResult, DumpDetective.Core.Models.ILogicalTimerCountSignal
{
    int DumpDetective.Core.Models.ILogicalTimerCountSignal.LogicalTimerCount => LogicalTimerCount;
}
