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

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? pressure = null;
        int contestedLockCount = 0;
        int highRecursionLockCount = 0;
        var derivedFrom = new List<ObservationId>();
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
                    break;
                case "thread.blocked":
                    deadlockCandidates.Add((ThreadRef)observation.Subjects[0]);
                    derivedFrom.Add(observation.Id);
                    break;
            }
        }

        if (pressure is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

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

        var finding = new Finding
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
        };

        return ValueTask.FromResult<IReadOnlyList<Finding>>([finding]);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
