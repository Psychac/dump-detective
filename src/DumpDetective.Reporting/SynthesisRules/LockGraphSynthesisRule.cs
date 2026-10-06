using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>LockGraphFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>ContestedLockCount</c>/<c>DeadlockCandidateCount</c> are reconstructed by counting matched
/// <c>lock.contested</c>/<c>thread.blocked</c> observations rather than a dedicated measure — both
/// are emitted one-per-entry with no cap, so the observation count already equals the old scalar.
/// <c>TotalHeldLocks</c> has no such stand-in (uncontested locks get no per-lock observation), so
/// it's the one measure the new whole-run <c>runtime.lock-pressure</c> observation carries.
/// </remarks>
internal sealed class LockGraphSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Lock Graph Analysis";
    // A lock re-entered this many times by its holder while other threads wait on it is a
    // re-entrancy signal worth calling out separately from ordinary contention.
    private const int HighRecursionThreshold = 3;

    public string RuleId => "lock-graph.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.lock-pressure", "lock.contested", "thread.blocked"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? pressure = null;
        int contestedLockCount = 0;
        int highRecursionLockCount = 0;
        var derivedFrom = new List<ObservationId>();
        var contestedIds = new List<ObservationId>();
        var blockedIds = new List<ObservationId>();
        var deadlockCandidates = new List<ThreadRef>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "runtime.lock-pressure":
                    pressure = observation;
                    break;
                case "lock.contested":
                    contestedLockCount++;
                    if (observation.Measures["recursion.count"].Value >= HighRecursionThreshold)
                        highRecursionLockCount++;
                    derivedFrom.Add(observation.Id);
                    contestedIds.Add(observation.Id);
                    break;
                case "thread.blocked":
                    deadlockCandidates.Add((ThreadRef)observation.Subjects[0]);
                    derivedFrom.Add(observation.Id);
                    blockedIds.Add(observation.Id);
                    break;
            }
        }

        if (pressure is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        derivedFrom.Insert(0, pressure.Id);

        int totalHeldLocks = (int)pressure.Measures["held-lock.count"].Value;
        int deadlockCandidateCount = deadlockCandidates.Count;

        Severity severity = contestedLockCount > 0 ? Severity.Warning : Severity.Info;

        string evidence = $"{totalHeldLocks:N0} inflated monitor lock(s) held; {contestedLockCount:N0} contested; {deadlockCandidateCount:N0} deadlock candidate(s).";

        if (deadlockCandidates.Count > 0)
        {
            var sb = new System.Text.StringBuilder(evidence);
            sb.Append(" Candidate thread(s): ");
            for (int i = 0; i < deadlockCandidates.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append($"thread {deadlockCandidates[i].ManagedThreadId} (OS: {deadlockCandidates[i].OsThreadId})");
            }
            sb.Append('.');
            evidence = sb.ToString();
        }

        if (highRecursionLockCount > 0)
            evidence += $" {highRecursionLockCount} contested lock(s) show a recursion count ≥ {HighRecursionThreshold} — the holder is re-entering the lock repeatedly while other threads wait, a potential re-entrancy issue.";

        var finding = new Claim
        {
            Fingerprint = Fingerprint("lock-contention"),
            Analyzer = AnalyzerName,
            Category = "Threading",
            Severity = severity,
            Title = "Lock contention and deadlock graph",
            Narrative = evidence,
            Recommendation = deadlockCandidateCount >= 2
                ? "Potential deadlock pattern detected. Review lock acquisition order and use cycle detection tools (e.g., !dlk in WinDbg) to confirm circular-wait."
                : contestedLockCount > 0
                    ? "Reduce lock scope on contested objects or switch to lock-free patterns."
                    : "No lock contention detected in this snapshot.",
            Tags = ["locks", "deadlock", "contention", "monitor"],
            MetricValue = deadlockCandidateCount,
            MetricUnit = "deadlock-candidates",
            DerivedFrom = derivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
            Assertion = BuildAssertion(contestedLockCount),
            Support = BuildSupport(pressure, totalHeldLocks, contestedIds, blockedIds),
            Counter = BuildCounter(pressure, blockedIds, contestedLockCount, deadlockCandidateCount),
            FalsifiedBy = BuildFalsifiedBy(contestedLockCount, deadlockCandidates),
        };

        return ValueTask.FromResult<IReadOnlyList<Claim>>([finding]);
    }

    private static Assertion BuildAssertion(int contestedLockCount) => new()
    {
        Predicate = contestedLockCount > 0 ? AssertionPredicate.Blocks : AssertionPredicate.Normal,
        Measure = ClaimParts.Count(contestedLockCount),
        Qualifiers = new AssertionQualifiers { Location = "contested monitor locks" },
    };

    private static IReadOnlyList<EvidenceStatement> BuildSupport(
        Observation pressure, int totalHeldLocks, List<ObservationId> contestedIds, List<ObservationId> blockedIds)
    {
        var support = new List<EvidenceStatement>(3)
        {
            new([pressure.Id], $"Counts {totalHeldLocks:N0} held inflated monitor lock(s)."),
        };

        if (contestedIds.Count > 0)
            support.Add(new(contestedIds, "Each is a held monitor lock that other threads are contending for."));
        if (blockedIds.Count > 0)
            support.Add(new(blockedIds, "Each is a thread blocked waiting on a held lock."));

        return support;
    }

    private static Counter BuildCounter(
        Observation pressure, List<ObservationId> blockedIds, int contestedLockCount, int deadlockCandidateCount)
    {
        if (contestedLockCount == 0)
            return Counter.None("Contention inflates a lock to a sync block, which is what was inspected, and none was contested.");

        if (deadlockCandidateCount == 0)
            return Counter.Of(new EvidenceStatement(
                [pressure.Id],
                "No thread was recorded as blocked on these locks, so there is no sign of a wait that persists."));

        if (deadlockCandidateCount == 1)
            return Counter.Of(new EvidenceStatement(
                blockedIds,
                "Only one blocked thread was found; a circular wait needs at least two."));

        return Counter.Of(new EvidenceStatement(
            blockedIds,
            "Deadlock detection here is a heuristic over held sync blocks and top-frame wait patterns, not a verified wait-for graph."));
    }

    private static IReadOnlyList<FalsificationTest> BuildFalsifiedBy(int contestedLockCount, List<ThreadRef> deadlockCandidates)
    {
        var tests = new List<FalsificationTest>(2)
        {
            new()
            {
                Kind = FalsificationKind.Windbg,
                Command = "!syncblk",
                Test = contestedLockCount > 0
                    ? $"If !syncblk shows no monitors with waiters, or a number other than {contestedLockCount:N0}, the contention reading is wrong."
                    : "If !syncblk lists any monitor with waiters, contention was missed.",
            },
        };

        if (deadlockCandidates.Count >= 2)
        {
            string threads = string.Join(", ", deadlockCandidates.Select(thread => thread.ManagedThreadId?.ToString() ?? thread.OsThreadId.ToString()));
            tests.Add(new()
            {
                Kind = FalsificationKind.Windbg,
                Command = "!dlk",
                Test = $"If !dlk finds no wait cycle among threads {threads}, they are blocked but not deadlocked.",
            });
        }

        return tests;
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
