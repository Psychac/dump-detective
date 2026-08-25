using System;
using System.Collections.Generic;

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class FinalizableObjectFindingGenerator : IFindingGenerator
{
    private const int Gen2WarningThreshold = 1_000;
    private const int Gen2CriticalThreshold = 10_000;
    private const ulong QueueRetainedWarningBytes = 10_000_000UL;  // 10 MB
    private const ulong QueueRetainedCriticalBytes = 100_000_000UL;  // 100 MB
    private const int CriticalFinalizerWarningThreshold = 100;
    private const int CriticalFinalizerCriticalThreshold = 1_000;
    private const int DynamicResolverQueueInfoThreshold = 10;
    private const int DynamicResolverQueueWarningThreshold = 100;
    private const int ThreadQueueInfoThreshold = 5;
    private const int ThreadQueueWarningThreshold = 50;
    private const int TimerHolderQueueInfoThreshold = 5;
    private const int TimerHolderQueueWarningThreshold = 50;
    private const int ReaderWriterLockQueueWarningThreshold = 3;

    public string AnalyzerName => "Finalizable Object Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is FinalizableObjectDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not FinalizableObjectDomainResult r) return [];

        var findings = new List<InsightFinding>(4);

        // ── Gen2 finalizable accumulation ─────────────────────────────────────
        if (r.Gen2Count >= Gen2WarningThreshold)
        {
            FindingSeverity sev = r.Gen2Count >= Gen2CriticalThreshold
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            string topType = r.TopFinalizableTypesByGen2Count.Count > 0
                ? r.TopFinalizableTypesByGen2Count[0].TypeName
                : "N/A";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"High Gen2 finalizable object count: {r.Gen2Count:N0} objects",
                Evidence: $"{r.Gen2Count:N0} finalizable objects survived to Gen2 " +
                          $"out of {r.TotalFinalizableObjects:N0} total ({FormatBytes(r.TotalFinalizableBytes)}). " +
                          $"Top type: {topType}.",
                Recommendation: "Gen2 finalizable objects extend memory pressure by at least two GC cycles. " +
                                "Implement IDisposable + using to enable early cleanup and call GC.SuppressFinalize " +
                                "in the Dispose method to prevent unnecessary finalization.",
                Tags: ["finalizer", "gen2", "gc", "dispose"],
                MetricValue: r.Gen2Count,
                MetricUnit: "objects"));
        }

        // ── Finalizer queue retained bytes ─────────────────────────────────────
        if (r.FinalizerQueueRetainedBytes >= QueueRetainedWarningBytes)
        {
            FindingSeverity sev = r.FinalizerQueueRetainedBytes >= QueueRetainedCriticalBytes
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            string estimateQualifier = r.IsRetainedEstimatePartial
                ? " (exact dominator-tree retained bytes unavailable for some entries—shallow size used instead, an underestimate)"
                : " (exact, from the dominator tree)";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"Finalizer queue retaining ~{FormatBytes(r.FinalizerQueueRetainedBytes)} in sub-graphs",
                Evidence: $"{r.FinalizerQueueCount:N0} objects in finalizer queue. " +
                          $"Top {r.TopQueueEntriesByRetainedSize.Count} entries retain an estimated " +
                          $"~{FormatBytes(r.FinalizerQueueRetainedBytes)}{estimateQualifier}." +
                          (r.HasUndisposedDisposableInQueue ? " Sampled entries contain undisposed IDisposable types." : string.Empty),
                Recommendation: "Objects in the finalizer queue block sub-graph collection until finalization completes. " +
                                "Implement IDisposable and call GC.SuppressFinalize in Dispose() to prevent queuing.",
                Tags: ["finalizer", "queue", "retention", "dispose"],
                MetricValue: r.FinalizerQueueRetainedBytes,
                MetricUnit: "bytes"));
        }

        // ── CriticalFinalizerObject / SafeHandle accumulation ───────────────────
        if (r.CriticalFinalizerQueueCount >= CriticalFinalizerWarningThreshold)
        {
            FindingSeverity sev = r.CriticalFinalizerQueueCount >= CriticalFinalizerCriticalThreshold
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            string topCriticalType = r.TopCriticalFinalizerTypesByCount.Count > 0
                ? r.TopCriticalFinalizerTypesByCount[0].TypeName
                : "N/A";

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: sev,
                Title: $"CriticalFinalizerObject accumulation in finalizer queue: {r.CriticalFinalizerQueueCount:N0} objects",
                Evidence: $"{r.CriticalFinalizerQueueCount:N0} of {r.FinalizerQueueCount:N0} queued objects derive from " +
                          $"CriticalFinalizerObject (e.g. SafeHandle/CriticalHandle), retaining ~{FormatBytes(r.CriticalFinalizerQueueBytes)}. " +
                          $"Top type: {topCriticalType}.",
                Recommendation: "CriticalFinalizerObject-derived types wrap OS resource handles (sockets, file descriptors, " +
                                "registry keys) with guaranteed finalization priority — accumulation implies unreleased native " +
                                "handles rather than ordinary managed memory pressure. Investigate the top type for missing " +
                                "Dispose()/Close() calls on the underlying handle-owning object.",
                Tags: ["finalizer", "criticalfinalizerobject", "safehandle", "handle-leak"],
                MetricValue: r.CriticalFinalizerQueueCount,
                MetricUnit: "objects"));
        }

        // ── Undisposed IDisposable in queue ────────────────────────────────────
        int undisposedCount = 0;
        foreach (FinalizerQueueEntry entry in r.TopQueueEntriesByRetainedSize)
        {
            if (entry.IsDisposableType && entry.DisposedFieldFound && !entry.DisposedFieldValue)
                undisposedCount++;
        }

        if (undisposedCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: FindingSeverity.Warning,
                Title: $"{undisposedCount} undisposed IDisposable object(s) detected in finalizer queue",
                Evidence: $"{undisposedCount} of the top finalizer queue entries implement IDisposable " +
                          $"but have a '_disposed' field that is false, indicating they were not disposed " +
                          $"before being collected.",
                Recommendation: "Use using statements or explicit Dispose() calls to ensure all IDisposable " +
                                "objects are disposed before GC collection. This eliminates finalizer queue " +
                                "pressure for these types.",
                Tags: ["finalizer", "idisposable", "undisposed", "leak"],
                MetricValue: undisposedCount,
                MetricUnit: "objects"));
        }

        AppendKnownQueuePatternFindings(findings, r);

        return findings;
    }

    // Well-known problematic types actually sitting in the finalizer queue right now, each
    // indicating a specific resource-management anti-pattern (uncached dynamic code generation,
    // abandoned threads, undisposed timers, legacy lock abandonment).
    private void AppendKnownQueuePatternFindings(List<InsightFinding> findings, FinalizableObjectDomainResult r)
    {
        long dynamicResolverCount = 0;
        long threadCount = 0;
        long timerHolderCount = 0;
        long readerWriterLockCount = 0;

        foreach (QueueTypeStatistic stat in r.TopQueueTypesByCount)
        {
            if (stat.TypeName.Contains("DynamicResolver", StringComparison.OrdinalIgnoreCase))
                dynamicResolverCount += stat.QueueCount;
            else if (stat.TypeName is "System.Threading.Thread")
                threadCount += stat.QueueCount;
            else if (stat.TypeName.Contains("TimerHolder", StringComparison.OrdinalIgnoreCase) ||
                     stat.TypeName.Contains("TimerQueueTimer", StringComparison.OrdinalIgnoreCase))
                timerHolderCount += stat.QueueCount;
            else if (stat.TypeName is "System.Threading.ReaderWriterLock")
                readerWriterLockCount += stat.QueueCount;
        }

        if (dynamicResolverCount >= DynamicResolverQueueInfoThreshold)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: dynamicResolverCount >= DynamicResolverQueueWarningThreshold ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "DynamicResolver accumulation in finalizer queue — uncached dynamic code generation",
                Evidence: $"{dynamicResolverCount:N0} DynamicResolver object(s) in the finalizer queue. " +
                          "DynamicResolver is the CLR internal finalizable backing for DynamicMethod and compiled expressions.",
                Recommendation: "Cache results of Expression.Compile<T>() and Delegate.CreateDelegate() in static fields. " +
                                "Consider using a compile-once / reuse pattern for serializers, mappers, and validators.",
                Tags: ["dynamic-method", "expression-compile", "finalizer", "queue", "memory-leak"],
                MetricValue: dynamicResolverCount,
                MetricUnit: "objects"));
        }

        if (threadCount >= ThreadQueueInfoThreshold)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threads",
                Severity: threadCount >= ThreadQueueWarningThreshold ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Abandoned Thread objects in finalizer queue",
                Evidence: $"{threadCount:N0} System.Threading.Thread object(s) in the finalizer queue. " +
                          "Thread objects should be joined or tracked; abandonment leaves them in the finalizer queue until collection.",
                Recommendation: "Always call thread.Join() or use a managed thread pool (Task, ThreadPool) instead of " +
                                "raw Thread objects. Use CancellationToken to signal graceful thread exit.",
                Tags: ["threads", "finalizer", "queue", "thread-abandonment"],
                MetricValue: threadCount,
                MetricUnit: "objects"));
        }

        if (timerHolderCount >= TimerHolderQueueInfoThreshold)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: timerHolderCount >= TimerHolderQueueWarningThreshold ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Undisposed System.Threading.Timer instances in finalizer queue",
                Evidence: $"{timerHolderCount:N0} TimerHolder/TimerQueueTimer object(s) in the finalizer queue. " +
                          "System.Threading.Timer has a finalizer; undisposed instances accumulate in the queue " +
                          "and may fire callbacks after their intended lifetime.",
                Recommendation: "Dispose System.Threading.Timer instances (timer.Dispose() or using) when they are " +
                                "no longer needed. In .NET 6+, prefer PeriodicTimer which is designed for await loops.",
                Tags: ["timer", "finalizer", "queue", "dispose", "memory-leak"],
                MetricValue: timerHolderCount,
                MetricUnit: "objects"));
        }

        if (readerWriterLockCount >= ReaderWriterLockQueueWarningThreshold)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Threads",
                Severity: FindingSeverity.Warning,
                Title: "Abandoned System.Threading.ReaderWriterLock instances in finalizer queue",
                Evidence: $"{readerWriterLockCount:N0} System.Threading.ReaderWriterLock object(s) in the finalizer queue. " +
                          "The old (non-Slim) ReaderWriterLock has a finalizer and carries OS kernel resources.",
                Recommendation: "Replace System.Threading.ReaderWriterLock with System.Threading.ReaderWriterLockSlim " +
                                "which is lighter and has no finalizer. Ensure locks are not abandoned in error paths.",
                Tags: ["reader-writer-lock", "finalizer", "queue", "threading", "legacy"],
                MetricValue: readerWriterLockCount,
                MetricUnit: "objects"));
        }
    }

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
