namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Mirrors <c>ClrRuntime.ThreadPool</c>'s <c>ClrThreadPool</c> — a ClrMD-provided convenience wrapper
/// over the runtime's thread-pool state, source-neutral in shape the same way
/// <c>ClrException.Type</c>/<c>.Message</c> are (not a raw field read), even though only a dump
/// implements it today.
/// </summary>
public readonly record struct RuntimeThreadPoolInfo(
    int MinThreads,
    int MaxThreads,
    int ActiveWorkerThreads,
    int IdleWorkerThreads,
    int RetiredWorkerThreads,
    /// <summary>Mirrors <c>ClrThreadPool.QueueLength</c> — read via a defensive reflection probe
    /// dump-side (not a public property on every ClrMD version this project has run against), so
    /// <c>null</c> here means "the underlying property wasn't there or wasn't readable," not "zero
    /// queued items."</summary>
    int? QueueLength,
    int CpuUtilization);

/// <summary>The <c>runtime.thread-pool</c> capability — built for <c>HangAnalyzer</c>'s retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), its first real consumer.
/// </summary>
public interface IRuntimeThreadPoolQuery
{
    /// <summary><c>false</c> when <c>ClrRuntime.ThreadPool</c> itself is <c>null</c> (thread pool
    /// state wasn't captured in this dump, or the runtime never initialized one) — mirrors the
    /// pre-retyping analyzer's own <c>ThreadPoolAnalysis.RuntimeInitialized</c> flag.</summary>
    bool TryGetThreadPoolInfo(out RuntimeThreadPoolInfo info);
}
