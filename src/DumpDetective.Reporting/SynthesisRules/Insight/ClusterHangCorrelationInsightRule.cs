using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectClusterHangCorrelation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// Needed two new observations, not one: <c>ThreadStackClusterAnalyzer</c>'s <c>thread.stack-cluster</c>
/// had no entity subject at all (a cluster spans many threads, so it was never given one), and its
/// dominant-cluster membership (<c>SampleOsThreadIds</c>) was domain-result-only — closed by a new
/// <c>thread.stack-cluster-member</c> observation (<c>Subjects=[ThreadRef]</c>) plus a shared
/// <c>cluster.rank</c> measure (0 = dominant/largest) on both it and <c>thread.stack-cluster</c>, so
/// this rule can find one cluster's whole-cluster measures and its members without a positional
/// assumption. <c>HangAnalyzer</c>'s <c>thread.wait-state</c> deliberately never carried
/// <c>WaitType</c>/<c>WaitReason</c> (categorical strings, no home in numeric <c>Measures</c>) — but
/// <c>WaitReason</c> is itself just a fixed 1:1 display label per <c>WaitType</c> case (see
/// <c>HangAnalyzer.DetectWaitPattern</c>), so a <c>wait-type.ordinal</c> measure carries everything
/// needed, the same <c>root-kind.ordinal</c> pattern <c>GCRootAnalyzer</c> already uses — this rule
/// keeps its own copy of the ordinal-to-label table (<see cref="WaitReasonByOrdinal"/>) since
/// <c>HangAnalyzer</c>'s <c>WaitType</c> enum is <c>internal</c> to a different assembly, the same
/// "re-derive, don't share the internal type" call <c>LeakCandidateSynthesisRule</c>'s remarks made
/// for <c>AllocationPatternPressureClassifier</c>. Deadlock-candidate lock-object-type names ride on
/// <c>thread.blocked</c>'s <c>Evidence</c> (narrative-only in the original, never filtered/scored on)
/// rather than a new <c>Subjects</c> shape. The wait-reason breakdown table from the original is
/// dropped, not ported — same "supplementary detail, not the whole payload" call
/// <c>ReferenceChainDominatorCorrelationInsightRule</c> made for its own table; the headline already
/// states the dominant reason.
/// </remarks>
internal sealed class ClusterHangCorrelationInsightRule : ISynthesisRule
{
    private const double OverlapWarningRatio = 0.60;

    // Mirrors HangAnalyzer.WaitType's declaration order exactly (MonitorWait=0 .. ThreadJoin=6) and
    // HangAnalyzer.DetectWaitPattern's 1:1 WaitReason label per case — WaitType is internal to a
    // different assembly, so this is a deliberate, documented copy, not a shared reference.
    private static readonly string[] WaitReasonByOrdinal =
    [
        "Waiting to acquire monitor lock",
        "Waiting for async task to complete (potential deadlock)",
        "Thread sleeping",
        "Waiting on semaphore",
        "Waiting on synchronization event",
        "Waiting on I/O operation",
        "Waiting for another thread to complete",
    ];

    public string RuleId => "insight.cluster-hang-correlation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes =
        [
            "thread.stack-cluster", "thread.stack-cluster-member", "runtime.thread-cluster-diversity",
            "thread.wait-state", "thread.blocked", "thread.state",
        ],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? dominantCluster = null;
        int dominantRank = int.MaxValue;
        var dominantMemberIds = new List<uint>();
        Observation? diversity = null;
        var waitingByOsThreadId = new Dictionary<uint, Observation>();
        var blockedByOsThreadId = new Dictionary<uint, Observation>();
        var stackRootByOsThreadId = new Dictionary<uint, int>();
        var memberObservationsByRank = new Dictionary<int, List<Observation>>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "thread.stack-cluster":
                {
                    int rank = (int)observation.Measures["cluster.rank"].Value;
                    if (rank < dominantRank)
                    {
                        dominantRank = rank;
                        dominantCluster = observation;
                    }
                    break;
                }
                case "thread.stack-cluster-member":
                {
                    int rank = (int)observation.Measures["cluster.rank"].Value;
                    if (!memberObservationsByRank.TryGetValue(rank, out List<Observation>? members))
                        memberObservationsByRank[rank] = members = [];
                    members.Add(observation);
                    break;
                }
                case "runtime.thread-cluster-diversity":
                    diversity = observation;
                    break;
                case "thread.wait-state":
                    waitingByOsThreadId[((ThreadRef)observation.Subjects[0]).OsThreadId] = observation;
                    break;
                case "thread.blocked":
                    blockedByOsThreadId[((ThreadRef)observation.Subjects[0]).OsThreadId] = observation;
                    break;
                case "thread.state":
                    stackRootByOsThreadId[((ThreadRef)observation.Subjects[0]).OsThreadId] =
                        (int)observation.Measures["stack-root.count"].Value;
                    break;
            }
        }

        if (dominantCluster is null || waitingByOsThreadId.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        if (!memberObservationsByRank.TryGetValue(dominantRank, out List<Observation>? dominantMembers) || dominantMembers.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        var derivedFrom = new List<ObservationId> { dominantCluster.Id };
        int overlapCount = 0;
        var overlappingOsThreadIds = new HashSet<uint>();
        var waitTypeOrdinalCounts = new Dictionary<int, int>();

        foreach (Observation member in dominantMembers)
        {
            uint osThreadId = ((ThreadRef)member.Subjects[0]).OsThreadId;
            derivedFrom.Add(member.Id);

            if (!waitingByOsThreadId.TryGetValue(osThreadId, out Observation? waitState))
                continue;

            overlapCount++;
            overlappingOsThreadIds.Add(osThreadId);
            derivedFrom.Add(waitState.Id);

            int waitTypeOrdinal = (int)waitState.Measures["wait-type.ordinal"].Value;
            waitTypeOrdinalCounts.TryGetValue(waitTypeOrdinal, out int count);
            waitTypeOrdinalCounts[waitTypeOrdinal] = count + 1;
        }

        double overlapRatio = overlapCount / (double)dominantMembers.Count;
        if (overlapRatio < OverlapWarningRatio)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        string? dominantWaitReason = null;
        int dominantWaitReasonCount = 0;
        foreach (KeyValuePair<int, int> kv in waitTypeOrdinalCounts)
        {
            if (kv.Value > dominantWaitReasonCount && kv.Key >= 0 && kv.Key < WaitReasonByOrdinal.Length)
            {
                dominantWaitReasonCount = kv.Value;
                dominantWaitReason = WaitReasonByOrdinal[kv.Key];
            }
        }

        long dominantCount = (long)dominantCluster.Measures["cluster.count"].Value;
        double aliveThreadCount = diversity is not null ? diversity.Measures["alive-thread.count"].Value : 0;
        if (diversity is not null)
            derivedFrom.Add(diversity.Id);
        double dominantPercentOfAlive = aliveThreadCount > 0 ? dominantCount * 100.0 / aliveThreadCount : 0;

        Severity severity = dominantPercentOfAlive >= 50 ? Severity.Critical : Severity.Warning;

        int overlappingDeadlockCandidateCount = 0;
        var deadlockLockTypes = new List<string>();
        var seenLockTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (uint osThreadId in overlappingOsThreadIds)
        {
            if (!blockedByOsThreadId.TryGetValue(osThreadId, out Observation? blocked))
                continue;

            overlappingDeadlockCandidateCount++;
            derivedFrom.Add(blocked.Id);

            string? lockTypesCsv = blocked.Evidence.Count > 0 ? blocked.Evidence[0].Description : null;
            if (lockTypesCsv is null)
                continue;
            foreach (string lockType in lockTypesCsv.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            {
                if (seenLockTypes.Add(lockType))
                    deadlockLockTypes.Add(lockType);
            }
        }

        if (overlappingDeadlockCandidateCount > 0)
            severity = Severity.Critical;

        int retainedStackRoots = 0;
        foreach (uint osThreadId in overlappingOsThreadIds)
        {
            if (stackRootByOsThreadId.TryGetValue(osThreadId, out int stackRoots))
                retainedStackRoots += stackRoots;
        }

        string reasonNote = dominantWaitReason is not null
            ? $", predominantly waiting on {dominantWaitReason}"
            : string.Empty;

        string retentionNote = retainedStackRoots > 0
            ? $" These threads also anchor {retainedStackRoots:N0} GC root(s) via their stacks, so resolving the block may relieve retained memory too."
            : string.Empty;

        string signature = dominantCluster.Evidence.Count > 0 ? dominantCluster.Evidence[0].Description ?? "" : "";

        string deadlockNote = string.Empty;
        var tags = new List<string> { "thread-cluster", "hang", "blocking", "contention", "cross-analyzer" };
        if (overlappingDeadlockCandidateCount > 0)
        {
            string lockTypeList = deadlockLockTypes.Count > 0 ? string.Join(", ", deadlockLockTypes) : "unresolved type(s)";
            deadlockNote = $" {overlappingDeadlockCandidateCount} of these threads are additionally flagged by " +
                           $"LockGraphAnalyzer as deadlock candidates, holding lock(s) on: {lockTypeList}.";
            tags.Add("deadlock-candidate");
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::cluster-hang-correlation",
                Analyzer = "InsightEngine",
                Category = "Threads",
                Severity = severity,
                Title = "Dominant thread-stack cluster correlates with HangAnalyzer's blocked threads",
                Narrative = $"{overlapCount} of {dominantMembers.Count} sampled threads in the dominant " +
                            $"stack cluster ({dominantCount:N0} threads, {dominantPercentOfAlive:F1}% of alive threads) " +
                            $"are also reported as waiting by the Hang analyzer{reasonNote}. " +
                            $"Cluster signature: {signature}{retentionNote}{deadlockNote}",
                Recommendation = overlappingDeadlockCandidateCount > 0
                    ? "Threads in this cluster are both blocked and independently confirmed to be holding contended " +
                      "locks. Review lock acquisition order across these threads and use cycle-detection tooling " +
                      "(e.g., !dlk in WinDbg) to confirm circular-wait before restarting the process."
                    : "A large group of threads sharing an identical stack and wait state strongly " +
                      "suggests a single contended resource or blocking call. Inspect the cluster's " +
                      "innermost frame together with the corresponding Hang analyzer wait details to " +
                      "identify the shared bottleneck.",
                Tags = tags,
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
