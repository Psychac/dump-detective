using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>FinalizableObjectFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Unlike <c>AsyncTaskSynthesisRule</c>, no aggregate/top-signal cap — the old generator emits up
/// to nine independent findings unconditionally (one per branch), so this rule does the same.
/// Three observation types back it: <c>type.finalizable-population</c> (whole finalizable-type
/// population, needed for the Gen2 branch — <c>finalizer.queue-entry</c> only covers the queue,
/// not the whole finalizable population), <c>finalizer.queue-entry</c> (per valid queue entry —
/// gained <c>shallow.bytes</c>/<c>is-critical-finalizer</c> here to back the CriticalFinalizerObject
/// branch), and <c>finalizer.queue-population</c> (whole-run queue count including invalid
/// "&lt;unknown&gt;"-typed entries <c>finalizer.queue-entry</c> can't observe individually).
/// </remarks>
internal sealed class FinalizableObjectSynthesisRule : ISynthesisRule
{
    private const int Gen2WarningThreshold = 1_000;
    private const int Gen2CriticalThreshold = 10_000;
    private const int QueueCountWarningThreshold = 1_000;
    private const int QueueCountCriticalThreshold = 10_000;
    private const ulong QueueRetainedWarningBytes = 10_000_000UL; // 10 MB
    private const ulong QueueRetainedCriticalBytes = 100_000_000UL; // 100 MB
    private const int CriticalFinalizerWarningThreshold = 100;
    private const int CriticalFinalizerCriticalThreshold = 1_000;
    private const int DynamicResolverQueueInfoThreshold = 10;
    private const int DynamicResolverQueueWarningThreshold = 100;
    private const int ThreadQueueInfoThreshold = 5;
    private const int ThreadQueueWarningThreshold = 50;
    private const int TimerHolderQueueInfoThreshold = 5;
    private const int TimerHolderQueueWarningThreshold = 50;
    private const int ReaderWriterLockQueueWarningThreshold = 3;

    private const string AnalyzerName = "Finalizable Object Analysis";

    public string RuleId => "finalizable-object.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.finalizable-population", "finalizer.queue-entry", "finalizer.queue-population"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        long totalObjects = 0;
        ulong totalBytes = 0;
        long gen2Count = 0;
        Observation? topGen2Type = null;
        var populationIds = new List<ObservationId>();

        ulong queueRetainedBytes = 0;
        bool isRetainedEstimatePartial = false;
        int undisposedCount = 0;
        var undisposedIds = new List<ObservationId>();
        int criticalCount = 0;
        ulong criticalBytes = 0;
        var criticalIds = new List<ObservationId>();
        var queueEntryIds = new List<ObservationId>();

        var queueTypeCounts = new Dictionary<string, (long Count, List<ObservationId> Ids)>(StringComparer.Ordinal);
        var criticalTypeCounts = new Dictionary<string, (long Count, List<ObservationId> Ids)>(StringComparer.Ordinal);

        long queueCount = 0;
        ObservationId? queuePopulationId = null;

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "type.finalizable-population":
                {
                    populationIds.Add(observation.Id);
                    totalObjects += (long)observation.Measures["instance.count"].Value;
                    totalBytes += (ulong)observation.Measures["shallow.bytes"].Value;
                    long thisGen2 = (long)observation.Measures["gen2.count"].Value;
                    gen2Count += thisGen2;
                    if (topGen2Type is null || thisGen2 > (long)topGen2Type.Measures["gen2.count"].Value)
                        topGen2Type = observation;
                    break;
                }
                case "finalizer.queue-entry":
                {
                    queueEntryIds.Add(observation.Id);
                    queueRetainedBytes += (ulong)observation.Measures["retained.bytes"].Value;
                    if (observation.Confidence < 1.0) isRetainedEstimatePartial = true;

                    bool disposedButLive = observation.Measures["disposed-but-live"].Value >= 1.0;
                    if (disposedButLive)
                    {
                        undisposedCount++;
                        undisposedIds.Add(observation.Id);
                    }

                    string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
                    if (!queueTypeCounts.TryGetValue(typeName, out var existing))
                        existing = (0, []);
                    existing.Count++;
                    existing.Ids.Add(observation.Id);
                    queueTypeCounts[typeName] = existing;

                    bool isCritical = observation.Measures["is-critical-finalizer"].Value >= 1.0;
                    if (isCritical)
                    {
                        criticalCount++;
                        criticalBytes += (ulong)observation.Measures["shallow.bytes"].Value;
                        criticalIds.Add(observation.Id);

                        if (!criticalTypeCounts.TryGetValue(typeName, out var existingCritical))
                            existingCritical = (0, []);
                        existingCritical.Count++;
                        existingCritical.Ids.Add(observation.Id);
                        criticalTypeCounts[typeName] = existingCritical;
                    }
                    break;
                }
                case "finalizer.queue-population":
                {
                    queueCount += (long)observation.Measures["queue.count"].Value;
                    queuePopulationId = observation.Id;
                    break;
                }
            }
        }

        var findings = new List<Finding>();

        // ── Gen2 finalizable accumulation ─────────────────────────────────────
        if (gen2Count >= Gen2WarningThreshold)
        {
            Severity sev = gen2Count >= Gen2CriticalThreshold ? Severity.Critical : Severity.Warning;
            string topType = topGen2Type is not null ? ((TypeRef)topGen2Type.Subjects[0]).CanonicalName : "N/A";

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("gen2-accumulation"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = sev,
                Title = $"High Gen2 finalizable object count: {gen2Count:N0} objects",
                Narrative = $"{gen2Count:N0} finalizable objects survived to Gen2 " +
                            $"out of {totalObjects:N0} total ({FormatBytes(totalBytes)}). " +
                            $"Top type: {topType}.",
                Recommendation = "Gen2 finalizable objects extend memory pressure by at least two GC cycles. " +
                                  "Implement IDisposable + using to enable early cleanup and call GC.SuppressFinalize " +
                                  "in the Dispose method to prevent unnecessary finalization.",
                Tags = ["finalizer", "gen2", "gc", "dispose"],
                MetricValue = gen2Count,
                MetricUnit = "objects",
                DerivedFrom = populationIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Finalizer queue size backlog ────────────────────────────────────────
        if (queueCount > QueueCountWarningThreshold)
        {
            Severity sev = queueCount > QueueCountCriticalThreshold ? Severity.Critical : Severity.Warning;

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("queue-backlog"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = sev,
                Title = sev == Severity.Critical
                    ? $"Critical finalizer queue backlog — {queueCount:N0} objects queued"
                    : $"Elevated finalizer queue — {queueCount:N0} objects pending finalization",
                Narrative = $"Finalizer queue holds {queueCount:N0} objects retaining ~{FormatBytes(queueRetainedBytes)}." +
                            (sev == Severity.Critical
                                ? " The finalizer thread may be blocked or unable to drain the queue."
                                : string.Empty),
                Recommendation = sev == Severity.Critical
                    ? "Implement IDisposable + GC.SuppressFinalize in Dispose() to prevent queuing. Check whether the finalizer thread is blocked (see §D1 Thread Overview)."
                    : "Review finalizable types for IDisposable compliance and call GC.SuppressFinalize after Dispose().",
                Tags = ["finalizer", "queue", "backlog", "dispose"],
                MetricValue = queueCount,
                MetricUnit = "objects",
                DerivedFrom = queuePopulationId is not null ? [queuePopulationId.Value] : [],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Finalizer queue retained bytes ─────────────────────────────────────
        if (queueRetainedBytes >= QueueRetainedWarningBytes)
        {
            Severity sev = queueRetainedBytes >= QueueRetainedCriticalBytes ? Severity.Critical : Severity.Warning;

            string estimateQualifier = isRetainedEstimatePartial
                ? " (exact dominator-tree retained bytes unavailable for some entries—shallow size used instead, an underestimate)"
                : " (exact, from the dominator tree)";

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("queue-retained-bytes"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = sev,
                Title = $"Finalizer queue retaining ~{FormatBytes(queueRetainedBytes)} in sub-graphs",
                Narrative = $"{queueCount:N0} objects in finalizer queue. " +
                            $"Top {queueEntryIds.Count} entries retain an estimated " +
                            $"~{FormatBytes(queueRetainedBytes)}{estimateQualifier}." +
                            (undisposedCount > 0 ? " Sampled entries contain undisposed IDisposable types." : string.Empty),
                Recommendation = "Objects in the finalizer queue block sub-graph collection until finalization completes. " +
                                  "Implement IDisposable and call GC.SuppressFinalize in Dispose() to prevent queuing.",
                Tags = ["finalizer", "queue", "retention", "dispose"],
                MetricValue = queueRetainedBytes,
                MetricUnit = "bytes",
                DerivedFrom = queueEntryIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── CriticalFinalizerObject / SafeHandle accumulation ───────────────────
        if (criticalCount >= CriticalFinalizerWarningThreshold)
        {
            Severity sev = criticalCount >= CriticalFinalizerCriticalThreshold ? Severity.Critical : Severity.Warning;

            string topCriticalType = "N/A";
            long topCriticalCount = -1;
            foreach ((string typeName, (long count, List<ObservationId> _)) in criticalTypeCounts)
            {
                if (count > topCriticalCount)
                {
                    topCriticalCount = count;
                    topCriticalType = typeName;
                }
            }

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("critical-finalizer-accumulation"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = sev,
                Title = $"CriticalFinalizerObject accumulation in finalizer queue: {criticalCount:N0} objects",
                Narrative = $"{criticalCount:N0} of {queueCount:N0} queued objects derive from " +
                            $"CriticalFinalizerObject (e.g. SafeHandle/CriticalHandle), retaining ~{FormatBytes(criticalBytes)}. " +
                            $"Top type: {topCriticalType}.",
                Recommendation = "CriticalFinalizerObject-derived types wrap OS resource handles (sockets, file descriptors, " +
                                  "registry keys) with guaranteed finalization priority — accumulation implies unreleased native " +
                                  "handles rather than ordinary managed memory pressure. Investigate the top type for missing " +
                                  "Dispose()/Close() calls on the underlying handle-owning object.",
                Tags = ["finalizer", "criticalfinalizerobject", "safehandle", "handle-leak"],
                MetricValue = criticalCount,
                MetricUnit = "objects",
                DerivedFrom = criticalIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Undisposed IDisposable in queue ────────────────────────────────────
        if (undisposedCount > 0)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("undisposed-in-queue"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = $"{undisposedCount} undisposed IDisposable object(s) detected in finalizer queue",
                Narrative = $"{undisposedCount} of the top finalizer queue entries implement IDisposable " +
                            $"but have a '_disposed' field that is false, indicating they were not disposed " +
                            $"before being collected.",
                Recommendation = "Use using statements or explicit Dispose() calls to ensure all IDisposable " +
                                  "objects are disposed before GC collection. This eliminates finalizer queue " +
                                  "pressure for these types.",
                Tags = ["finalizer", "idisposable", "undisposed", "leak"],
                MetricValue = undisposedCount,
                MetricUnit = "objects",
                DerivedFrom = undisposedIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        AppendKnownQueuePatternFindings(findings, queueTypeCounts);

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    // Well-known problematic types actually sitting in the finalizer queue right now, each
    // indicating a specific resource-management anti-pattern (uncached dynamic code generation,
    // abandoned threads, undisposed timers, legacy lock abandonment).
    private static void AppendKnownQueuePatternFindings(
        List<Finding> findings, Dictionary<string, (long Count, List<ObservationId> Ids)> queueTypeCounts)
    {
        long dynamicResolverCount = 0;
        long threadCount = 0;
        long timerHolderCount = 0;
        long readerWriterLockCount = 0;
        var dynamicResolverIds = new List<ObservationId>();
        var threadIds = new List<ObservationId>();
        var timerHolderIds = new List<ObservationId>();
        var readerWriterLockIds = new List<ObservationId>();

        foreach ((string typeName, (long count, List<ObservationId> ids)) in queueTypeCounts)
        {
            if (typeName.Contains("DynamicResolver", StringComparison.OrdinalIgnoreCase))
            {
                dynamicResolverCount += count;
                dynamicResolverIds.AddRange(ids);
            }
            else if (typeName is "System.Threading.Thread")
            {
                threadCount += count;
                threadIds.AddRange(ids);
            }
            else if (typeName.Contains("TimerHolder", StringComparison.OrdinalIgnoreCase) ||
                     typeName.Contains("TimerQueueTimer", StringComparison.OrdinalIgnoreCase))
            {
                timerHolderCount += count;
                timerHolderIds.AddRange(ids);
            }
            else if (typeName is "System.Threading.ReaderWriterLock")
            {
                readerWriterLockCount += count;
                readerWriterLockIds.AddRange(ids);
            }
        }

        if (dynamicResolverCount >= DynamicResolverQueueInfoThreshold)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("dynamic-resolver-accumulation"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = dynamicResolverCount >= DynamicResolverQueueWarningThreshold ? Severity.Warning : Severity.Info,
                Title = "DynamicResolver accumulation in finalizer queue — uncached dynamic code generation",
                Narrative = $"{dynamicResolverCount:N0} DynamicResolver object(s) in the finalizer queue. " +
                            "DynamicResolver is the CLR internal finalizable backing for DynamicMethod and compiled expressions.",
                Recommendation = "Cache results of Expression.Compile<T>() and Delegate.CreateDelegate() in static fields. " +
                                  "Consider using a compile-once / reuse pattern for serializers, mappers, and validators.",
                Tags = ["dynamic-method", "expression-compile", "finalizer", "queue", "memory-leak"],
                MetricValue = dynamicResolverCount,
                MetricUnit = "objects",
                DerivedFrom = dynamicResolverIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        if (threadCount >= ThreadQueueInfoThreshold)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("abandoned-thread-objects"),
                Analyzer = AnalyzerName,
                Category = "Threads",
                Severity = threadCount >= ThreadQueueWarningThreshold ? Severity.Warning : Severity.Info,
                Title = "Abandoned Thread objects in finalizer queue",
                Narrative = $"{threadCount:N0} System.Threading.Thread object(s) in the finalizer queue. " +
                            "Thread objects should be joined or tracked; abandonment leaves them in the finalizer queue until collection.",
                Recommendation = "Always call thread.Join() or use a managed thread pool (Task, ThreadPool) instead of " +
                                  "raw Thread objects. Use CancellationToken to signal graceful thread exit.",
                Tags = ["threads", "finalizer", "queue", "thread-abandonment"],
                MetricValue = threadCount,
                MetricUnit = "objects",
                DerivedFrom = threadIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        if (timerHolderCount >= TimerHolderQueueInfoThreshold)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("undisposed-timer-instances"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = timerHolderCount >= TimerHolderQueueWarningThreshold ? Severity.Warning : Severity.Info,
                Title = "Undisposed System.Threading.Timer instances in finalizer queue",
                Narrative = $"{timerHolderCount:N0} TimerHolder/TimerQueueTimer object(s) in the finalizer queue. " +
                            "System.Threading.Timer has a finalizer; undisposed instances accumulate in the queue " +
                            "and may fire callbacks after their intended lifetime.",
                Recommendation = "Dispose System.Threading.Timer instances (timer.Dispose() or using) when they are " +
                                  "no longer needed. In .NET 6+, prefer PeriodicTimer which is designed for await loops.",
                Tags = ["timer", "finalizer", "queue", "dispose", "memory-leak"],
                MetricValue = timerHolderCount,
                MetricUnit = "objects",
                DerivedFrom = timerHolderIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        if (readerWriterLockCount >= ReaderWriterLockQueueWarningThreshold)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("abandoned-reader-writer-lock"),
                Analyzer = AnalyzerName,
                Category = "Threads",
                Severity = Severity.Warning,
                Title = "Abandoned System.Threading.ReaderWriterLock instances in finalizer queue",
                Narrative = $"{readerWriterLockCount:N0} System.Threading.ReaderWriterLock object(s) in the finalizer queue. " +
                            "The old (non-Slim) ReaderWriterLock has a finalizer and carries OS kernel resources.",
                Recommendation = "Replace System.Threading.ReaderWriterLock with System.Threading.ReaderWriterLockSlim " +
                                  "which is lighter and has no finalizer. Ensure locks are not abandoned in error paths.",
                Tags = ["reader-writer-lock", "finalizer", "queue", "threading", "legacy"],
                MetricValue = readerWriterLockCount,
                MetricUnit = "objects",
                DerivedFrom = readerWriterLockIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
