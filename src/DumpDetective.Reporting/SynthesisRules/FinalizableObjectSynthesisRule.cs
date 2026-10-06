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

    // Some entries fell back to shallow size, so the retained total is an underestimate — the same
    // 0.7 the analyzer used to put on the whole observation, now scoped to the one finding whose
    // claim is the retained total.
    private static readonly ConfidenceBreakdown EstimatedRetainedBytesConfidence = ConfidenceBreakdown.MeasurementCertain with
    {
        Composite = 0.7,
        EvidenceStrength = 0.7,
        LimitingFactors = ["Some finalizer-queue entries had no exact dominator-tree retained size; shallow size was used instead."],
    };

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.finalizable-population", "finalizer.queue-entry", "finalizer.queue-population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
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
                    if (observation.Measures["is-retained-exact"].Value == 0) isRetainedEstimatePartial = true;

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

        var findings = new List<Claim>();

        // ── Gen2 finalizable accumulation ─────────────────────────────────────
        if (gen2Count >= Gen2WarningThreshold)
        {
            Severity sev = gen2Count >= Gen2CriticalThreshold ? Severity.Critical : Severity.Warning;
            string topType = topGen2Type is not null ? ((TypeRef)topGen2Type.Subjects[0]).CanonicalName : "N/A";

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("gen2-accumulation"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(gen2Count),
                    Reference = ClaimParts.Budget(
                        ClaimParts.Count(sev == Severity.Critical ? Gen2CriticalThreshold : Gen2WarningThreshold),
                        sev == Severity.Critical ? "critical threshold" : "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "finalizable objects that survived into Gen2" },
                },
                Support = [new EvidenceStatement(populationIds, $"{gen2Count:N0} of {totalObjects:N0} finalizable objects are in Gen2; the top type is {topType}.")],
                Counter = Counter.None("A finalizable object in Gen2 may simply be live and in use; surviving into Gen2 does not mean it is waiting for finalization."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!finalizequeue",
                        Test = $"If !finalizequeue shows far fewer registered finalizable objects than {totalObjects:N0}, the population is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the objects are long-lived singletons or pooled resources disposed at shutdown, their Gen2 residency is by design.",
                    },
                ],
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

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("queue-backlog"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(queueCount),
                    Reference = ClaimParts.Budget(
                        ClaimParts.Count(sev == Severity.Critical ? QueueCountCriticalThreshold : QueueCountWarningThreshold),
                        sev == Severity.Critical ? "critical threshold" : "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "objects in the finalizer queue" },
                },
                Support = queuePopulationId is ObservationId queueObservationId
                    ? [new EvidenceStatement([queueObservationId], $"The finalizer queue holds {queueCount:N0} objects.")]
                    : [],
                Counter = Counter.None("The queue fills briefly after a burst of finalizable allocations and drains once the finalizer thread runs; one snapshot does not show it is stuck."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!finalizequeue",
                        Test = $"If !finalizequeue reports far fewer than {queueCount:N0} objects ready for finalization, the backlog is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!threads",
                        Test = "If the finalizer thread is running and not blocked, the queue is draining.",
                    },
                ],
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

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("queue-retained-bytes"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Retains,
                    Measure = ClaimParts.Bytes(queueRetainedBytes),
                    Reference = ClaimParts.Budget(
                        ClaimParts.Bytes(sev == Severity.Critical ? QueueRetainedCriticalBytes : QueueRetainedWarningBytes),
                        sev == Severity.Critical ? "critical threshold" : "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "memory retained by finalizer-queue entries" },
                },
                Support = [new EvidenceStatement(queueEntryIds, $"The top {queueEntryIds.Count} queue entries retain about {FormatBytes(queueRetainedBytes)}.")],
                Counter = isRetainedEstimatePartial
                    ? Counter.Of(new EvidenceStatement(queueEntryIds, "Some entries had no exact dominator-tree retained size and used shallow size instead, so the figure underestimates the real retention."))
                    : Counter.None("Exact dominator-tree sizes count only memory freed with the queued objects, so nothing observed qualifies the figure."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gcroot <queued object address>",
                        Test = "On a sampled queued object: if its sub-graph is also reachable from a normal root, the finalizer queue is not what retains it.",
                    },
                ],
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
                Confidence = isRetainedEstimatePartial ? EstimatedRetainedBytesConfidence : ConfidenceBreakdown.MeasurementCertain,
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

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("critical-finalizer-accumulation"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(criticalCount),
                    Reference = ClaimParts.Budget(
                        ClaimParts.Count(sev == Severity.Critical ? CriticalFinalizerCriticalThreshold : CriticalFinalizerWarningThreshold),
                        sev == Severity.Critical ? "critical threshold" : "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "CriticalFinalizerObject-derived objects in the finalizer queue", Mechanism = "SafeHandle and CriticalHandle wrappers" },
                },
                Support = [new EvidenceStatement(criticalIds, $"{criticalCount:N0} of {queueCount:N0} queued objects derive from CriticalFinalizerObject, retaining about {FormatBytes(criticalBytes)}.")],
                Counter = Counter.None("SafeHandle-derived objects are routine in code that owns OS handles; their number alone does not show unreleased handles."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -type SafeHandle -stat",
                        Test = $"If !dumpheap finds far fewer than {criticalCount:N0} SafeHandle-derived objects, the count is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = $"If the top type ({topCriticalType}) is disposed on the normal path, the queued instances are waiting on collection, not leaking handles.",
                    },
                ],
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("undisposed-in-queue"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(undisposedCount),
                    Qualifiers = new AssertionQualifiers { Location = "finalizer-queue entries implementing IDisposable with a false _disposed field" },
                },
                Support = [new EvidenceStatement(undisposedIds, $"{undisposedCount:N0} queued objects implement IDisposable and have a false _disposed field.")],
                Counter = Counter.None("The disposed state is read from the object's own field, which some IDisposable types do not maintain; for those it can read false after disposal."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!do <queued object address>",
                        Test = "On a sampled entry: if !do shows the _disposed field true, or the type keeps no such field, it was not left undisposed.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the type's Dispose does not set a _disposed field, the false reading says nothing about disposal.",
                    },
                ],
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

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    // Well-known problematic types actually sitting in the finalizer queue right now, each
    // indicating a specific resource-management anti-pattern (uncached dynamic code generation,
    // abandoned threads, undisposed timers, legacy lock abandonment).
    private static void AppendKnownQueuePatternFindings(
        List<Claim> findings, Dictionary<string, (long Count, List<ObservationId> Ids)> queueTypeCounts)
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("dynamic-resolver-accumulation"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(dynamicResolverCount),
                    Reference = dynamicResolverCount >= DynamicResolverQueueWarningThreshold
                        ? ClaimParts.Budget(ClaimParts.Count(DynamicResolverQueueWarningThreshold), "warning threshold")
                        : ClaimParts.Budget(ClaimParts.Count(DynamicResolverQueueInfoThreshold), "notice threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "DynamicResolver objects", Mechanism = "uncached dynamic code generation" },
                },
                Support = [new EvidenceStatement(dynamicResolverIds, $"{dynamicResolverCount:N0} DynamicResolver objects are in the finalizer queue.")],
                Counter = Counter.None("DynamicMethods that are compiled once and cached still leave resolvers awaiting finalization; the count does not show regeneration."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the compiled delegates and expression trees are cached in static fields, the resolvers are from one-time compilation.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the DynamicResolver count does not grow, code generation is not repeating.",
                    }
                ],
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("abandoned-thread-objects"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(threadCount),
                    Reference = threadCount >= ThreadQueueWarningThreshold
                        ? ClaimParts.Budget(ClaimParts.Count(ThreadQueueWarningThreshold), "warning threshold")
                        : ClaimParts.Budget(ClaimParts.Count(ThreadQueueInfoThreshold), "notice threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "Thread objects", Mechanism = "threads not joined or tracked" },
                },
                Support = [new EvidenceStatement(threadIds, $"{threadCount:N0} Thread objects are in the finalizer queue.")],
                Counter = Counter.None("A Thread object stays queued after its thread has exited normally; queued Thread objects do not show a thread was abandoned."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!threads",
                        Test = "If !threads shows these threads exited normally, the queued Thread objects are finished, not abandoned.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the code joins or tracks each thread it starts, the queued objects are completed threads.",
                    }
                ],
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("undisposed-timer-instances"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(timerHolderCount),
                    Reference = timerHolderCount >= TimerHolderQueueWarningThreshold
                        ? ClaimParts.Budget(ClaimParts.Count(TimerHolderQueueWarningThreshold), "warning threshold")
                        : ClaimParts.Budget(ClaimParts.Count(TimerHolderQueueInfoThreshold), "notice threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "TimerHolder and TimerQueueTimer objects", Mechanism = "undisposed System.Threading.Timer" },
                },
                Support = [new EvidenceStatement(timerHolderIds, $"{timerHolderCount:N0} TimerHolder and TimerQueueTimer objects are in the finalizer queue.")],
                Counter = Counter.None("A disposed timer's holder can still sit in the queue until finalization runs; presence does not show it was never disposed."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!do <timer address>",
                        Test = "On a sampled TimerQueueTimer: if its state shows it was disposed, it is not an undisposed timer.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the owning code disposes each timer on completion or shutdown, the queued holders are awaiting finalization of disposed timers.",
                    }
                ],
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
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("abandoned-reader-writer-lock"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(readerWriterLockCount),
                    Reference = ClaimParts.Budget(ClaimParts.Count(ReaderWriterLockQueueWarningThreshold), "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "ReaderWriterLock objects", Mechanism = "legacy lock with a finalizer and kernel resources" },
                },
                Support = [new EvidenceStatement(readerWriterLockIds, $"{readerWriterLockCount:N0} ReaderWriterLock objects are in the finalizer queue.")],
                Counter = Counter.None("A lock released correctly can still be queued for finalization until collection; presence does not show it was abandoned."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the code releases every lock in a finally block, the queued locks were released, only awaiting finalization.",
                    },
                ],
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
