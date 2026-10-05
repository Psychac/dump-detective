using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>TimerLeakFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// No new observation type or measure needed — <c>infra.timer</c> already backs every count both
/// findings read. Each of the six timer categories is an exact-type-name classification
/// (<c>TimerLeakAnalyzer.ClassifyType</c>), and every <c>infra.timer</c> observation's
/// <c>Subjects[0]</c> already carries that same type name, so re-deriving the category from the
/// canonical name reconstructs every count exactly — including the oddly-named
/// <c>LogicalTimerCount</c>, which the real analyzer sets equal to <c>TimerQueueTimerCount</c>
/// (not a sum of the "real" disposable timer categories, despite the name).
/// <para>
/// The logical-timers finding's confidence comes from the top timer type's sampled GC root path
/// (<see cref="RootPathEvidenceStrength"/>), carried as raw facts on <c>infra.timer-population</c>
/// — the same top type (<c>ByType[0]</c>, highest count) the old generator's
/// <c>EvidenceConfidence.Compute</c> read.
/// </para>
/// </remarks>
internal sealed class TimerLeakSynthesisRule : ISynthesisRule
{
    private const int LogicalTimerCountWarningThreshold = 100;
    private const int LogicalTimerCountCriticalThreshold = 250;
    private const int QueuePressureInfoThreshold = 50;
    private const int QueuePressureWarningThreshold = 150;

    private const string AnalyzerName = "Timer Leak Analysis";

    public string RuleId => "timer-leak.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.timer", "infra.timer-population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        long totalTimers = 0;
        long threadingTimerCount = 0, timersTimerCount = 0, timerQueueTimerCount = 0;
        long timerHolderCount = 0, periodicTimerCount = 0, otherTimerCount = 0;
        var allIds = new List<ObservationId>();
        var timerHolderAndQueueIds = new List<ObservationId>();

        Observation? population = null;

        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "infra.timer-population")
            {
                population = observation;
                continue;
            }

            long count = (long)observation.Measures["instance.count"].Value;
            totalTimers += count;
            allIds.Add(observation.Id);

            string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
            switch (typeName)
            {
                case "System.Threading.Timer":
                    threadingTimerCount += count;
                    break;
                case "System.Timers.Timer":
                    timersTimerCount += count;
                    break;
                case "System.Threading.TimerQueueTimer":
                    timerQueueTimerCount += count;
                    timerHolderAndQueueIds.Add(observation.Id);
                    break;
                case "System.Threading.TimerHolder":
                    timerHolderCount += count;
                    timerHolderAndQueueIds.Add(observation.Id);
                    break;
                case "System.Threading.PeriodicTimer":
                    periodicTimerCount += count;
                    break;
                default:
                    otherTimerCount += count;
                    break;
            }
        }

        if (totalTimers == 0) return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long logicalTimerCount = timerQueueTimerCount;
        var findings = new List<Claim>(2);

        // ── Logical timers (undisposed) ──────────────────────────────────────────
        if (logicalTimerCount >= LogicalTimerCountWarningThreshold)
        {
            Severity severity = logicalTimerCount >= LogicalTimerCountCriticalThreshold ? Severity.Critical : Severity.Warning;

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("logical-timers"),
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = severity,
                Title = $"{logicalTimerCount:N0} logical timers (undisposed) on managed heap",
                Narrative = $"Raw object count: {totalTimers:N0} (Threading.Timer={threadingTimerCount:N0}, Timers.Timer={timersTimerCount:N0}, " +
                            $"TimerQueueTimer={timerQueueTimerCount:N0}, TimerHolder={timerHolderCount:N0}, PeriodicTimer={periodicTimerCount:N0}, Other={otherTimerCount:N0}).",
                Recommendation = "Dispose timers explicitly when they are no longer needed. " +
                                  "Avoid creating per-request or per-entity timers; use shared scheduling services where possible.",
                Tags = ["infrastructure", "timer", "leak", "dispose"],
                MetricValue = logicalTimerCount,
                MetricUnit = "timers",
                DerivedFrom = population is null ? allIds : [.. allIds, population.Id],
                Confidence = population is null
                    ? ConfidenceBreakdown.MeasurementCertain
                    : RootPathEvidenceStrength.FromMeasures(population.Measures, "top-type."),
            });
        }

        // ── Timer queue/finalizer pressure ───────────────────────────────────────
        long queuePressure = timerHolderCount + timerQueueTimerCount;
        if (queuePressure >= QueuePressureInfoThreshold)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("queue-pressure"),
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = queuePressure >= QueuePressureWarningThreshold ? Severity.Warning : Severity.Info,
                Title = "Timer queue/finalizer pressure detected",
                Narrative = $"{queuePressure:N0} TimerHolder/TimerQueueTimer object(s) were observed, which typically indicates undisposed System.Threading.Timer usage.",
                Recommendation = "Track timer lifetimes and call Dispose() on completion or shutdown. " +
                                  "For periodic async loops, prefer PeriodicTimer or hosted background services.",
                Tags = ["timer", "timerqueue", "finalizer", "dispose"],
                MetricValue = queuePressure,
                MetricUnit = "timer queue objects",
                DerivedFrom = timerHolderAndQueueIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
