using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>AsyncStateMachineFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Unlike <c>ArraySynthesisRule</c>, this one <b>must</b> re-rank its observations rather than
/// trust emission order: <c>AsyncStateMachineAnalyzer</c> emits both
/// <c>type.async-state-machine</c> and <c>type.async-captured-closure</c> inline, per type, inside
/// the same loop that builds <c>TopStateMachineTypes</c>/<c>TopByCapturedSize</c> — and only sorts
/// those lists <em>after</em> the loop finishes. So observation emission order is plain type
/// iteration order, never the sorted order the old generator's <c>[0]</c> narrative picks relied
/// on. (Contrast <c>ArrayFindingGenerator</c>, where the equivalent lists were already fully
/// sorted before a separate, later emission pass ran over them — hence that rule correctly trusts
/// order and this one cannot.) Checked, not assumed, before writing this rule.
/// </remarks>
internal sealed class AsyncStateMachineSynthesisRule : ISynthesisRule
{
    private const int FireAndForgetThreshold = 100;
    private const int HighCountWarning = 1_000;
    private const int HighCountCritical = 10_000;
    private const int MaxFireAndForgetFindings = 3;
    private const int AsyncVoidCountThreshold = 10;
    private const ulong LargeCaptureWarning = 50_000_000UL; // 50 MB
    private const ulong LargeCaptureCritical = 200_000_000UL; // 200 MB

    private const string AnalyzerName = "Async State Machine Analysis";

    public string RuleId => "async-state-machine.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.async-state-machine", "type.async-captured-closure"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        var stateMachines = new List<Observation>();
        var capturedClosures = new List<Observation>();
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "type.async-state-machine") stateMachines.Add(observation);
            else if (observation.ObservationType == "type.async-captured-closure") capturedClosures.Add(observation);
        }

        var findings = new List<Claim>(4);
        EmitHighTotalCount(stateMachines, findings);
        EmitFireAndForget(stateMachines, findings);
        EmitAsyncVoid(stateMachines, findings);
        EmitLargeCapturedClosures(capturedClosures, findings);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static void EmitHighTotalCount(List<Observation> stateMachines, List<Claim> findings)
    {
        long totalCount = 0;
        ulong totalBytes = 0;
        Observation? topByCount = null;
        foreach (Observation sm in stateMachines)
        {
            long count = (long)sm.Measures["instance.count"].Value;
            totalCount += count;
            totalBytes += (ulong)sm.Measures["shallow.bytes"].Value;
            if (topByCount is null || count > (long)topByCount.Measures["instance.count"].Value)
                topByCount = sm;
        }

        if (totalCount < HighCountWarning) return;

        Severity sev = totalCount >= HighCountCritical ? Severity.Critical : Severity.Warning;
        string topType = topByCount is null ? "N/A" : MethodSubject(topByCount).Name;

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("high-async-state-machine-count"),
            Analyzer = AnalyzerName,
            Category = "Memory",
            Severity = sev,
            Title = $"High async state machine count: {totalCount:N0} suspended state machines",
            Narrative = $"{totalCount:N0} async state machine objects found on heap " +
                        $"consuming {FormatHelper.FormatBytes(totalBytes)}. " +
                        $"Top method: {topType}.",
            Recommendation = "Each suspended async method holds an allocation on the heap for the duration of the " +
                              "await. A high count indicates many in-flight async operations. Review for " +
                              "fire-and-forget patterns, unbounded parallelism, or awaited operations that never complete.",
            Tags = ["async", "state-machine", "memory", "suspend"],
            MetricValue = totalCount,
            MetricUnit = "objects",
            DerivedFrom = stateMachines.Select(o => o.Id).ToArray(),
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });
    }

    private static void EmitFireAndForget(List<Observation> stateMachines, List<Claim> findings)
    {
        var byMethod = new Dictionary<(string DeclaringType, string Method), (long Count, ulong Bytes, List<ObservationId> Sources)>();
        foreach (Observation sm in stateMachines)
        {
            MethodRef method = MethodSubject(sm);
            var key = (method.DeclaringType.CanonicalName, method.Name);
            long count = (long)sm.Measures["instance.count"].Value;
            ulong bytes = (ulong)sm.Measures["shallow.bytes"].Value;

            if (byMethod.TryGetValue(key, out var existing))
            {
                existing.Count += count;
                existing.Bytes += bytes;
                existing.Sources.Add(sm.Id);
                byMethod[key] = existing;
            }
            else
            {
                byMethod[key] = (count, bytes, [sm.Id]);
            }
        }

        var candidates = byMethod
            .Select(kv => (kv.Key.DeclaringType, kv.Key.Method, kv.Value.Count, kv.Value.Bytes, kv.Value.Sources))
            .Where(c => c.Count >= FireAndForgetThreshold)
            .OrderByDescending(c => c.Count)
            .ToList();

        int emitted = 0;
        foreach (var candidate in candidates)
        {
            if (emitted >= MaxFireAndForgetFindings) break;

            Severity sev = candidate.Count >= HighCountCritical ? Severity.Critical : Severity.Warning;

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint($"fire-and-forget-{emitted}"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = sev,
                Title = $"Potential fire-and-forget leak: '{candidate.Method}' has {candidate.Count:N0} suspended instances",
                Narrative = $"{candidate.Count:N0} suspended instances of async method '{candidate.Method}' " +
                            $"declared on '{candidate.DeclaringType}' found on heap " +
                            $"(total {FormatHelper.FormatBytes(candidate.Bytes)}). " +
                            "A large count for a single method suggests callers are not awaiting completion.",
                Recommendation = "Ensure all async methods are properly awaited. Fire-and-forget patterns using " +
                                  "Task.Run or async void are common sources of unbounded state machine accumulation. " +
                                  "Consider using a managed work queue or TaskScheduler.",
                Tags = ["async", "fire-and-forget", "leak", "state-machine"],
                MetricValue = candidate.Count,
                MetricUnit = "objects",
                DerivedFrom = candidate.Sources,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });

            emitted++;
        }
    }

    private static void EmitAsyncVoid(List<Observation> stateMachines, List<Claim> findings)
    {
        var asyncVoidMethods = new List<(string Method, string Type, long Count, ObservationId Source)>();
        foreach (Observation sm in stateMachines)
        {
            if (sm.Measures["is-async-void"].Value < 1.0) continue;
            long count = (long)sm.Measures["instance.count"].Value;
            if (count < AsyncVoidCountThreshold) continue;

            MethodRef method = MethodSubject(sm);
            asyncVoidMethods.Add((method.Name, method.DeclaringType.CanonicalName, count, sm.Id));
        }

        if (asyncVoidMethods.Count == 0) return;

        asyncVoidMethods.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        (string Method, string Type, long Count, ObservationId Source) top = asyncVoidMethods[0];
        string topAsyncVoid = $"{top.Method} (on {top.Type})";

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("async-void-method"),
            Analyzer = AnalyzerName,
            Category = "Memory",
            Severity = Severity.Warning,
            Title = $"Async void method detected: {topAsyncVoid}",
            Narrative = $"{asyncVoidMethods.Count} async void method(s) detected with state machines on heap. " +
                        $"Top: {topAsyncVoid} with {top.Count:N0} suspended instances.",
            Recommendation = "Async void methods should be avoided except for event handlers. They fire-and-forget " +
                              "by construction, make exception handling impossible, and prevent callers from knowing " +
                              "when the operation completes. Use async Task methods instead and await the result.",
            Tags = ["async", "async-void", "memory", "antipattern"],
            MetricValue = asyncVoidMethods.Count,
            MetricUnit = "methods",
            DerivedFrom = asyncVoidMethods.Select(m => m.Source).ToArray(),
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });
    }

    private static void EmitLargeCapturedClosures(List<Observation> capturedClosures, List<Claim> findings)
    {
        if (capturedClosures.Count == 0) return;

        ulong totalCaptured = 0;
        Observation? top = null;
        foreach (Observation closure in capturedClosures)
        {
            ulong bytes = (ulong)closure.Measures["captured.bytes"].Value;
            totalCaptured += bytes;
            if (top is null || bytes > (ulong)top.Measures["captured.bytes"].Value)
                top = closure;
        }

        if (totalCaptured < LargeCaptureWarning) return;

        Severity sev = totalCaptured >= LargeCaptureCritical ? Severity.Critical : Severity.Warning;
        string topCapture = top is null
            ? "N/A"
            : $"{((TypeRef)top.Subjects[0]).CanonicalName} ({FormatHelper.FormatBytes((ulong)top.Measures["captured.bytes"].Value)})";

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("large-captured-closures"),
            Analyzer = AnalyzerName,
            Category = "Memory",
            Severity = sev,
            Title = $"Async state machines capturing large closures: {FormatHelper.FormatBytes(totalCaptured)} total",
            Narrative = $"Top state machine instances retain an estimated {FormatHelper.FormatBytes(totalCaptured)} " +
                        "via captured reference fields. " +
                        $"Largest: {topCapture}.",
            Recommendation = "State machines capture all variables referenced across await boundaries. " +
                              "Avoid capturing large objects (DbContext, HttpClient, large arrays) in async methods. " +
                              "Consider null-ing captured references after use or restructuring the method to reduce capture scope.",
            Tags = ["async", "closure", "capture", "memory"],
            MetricValue = totalCaptured,
            MetricUnit = "bytes",
            DerivedFrom = capturedClosures.Select(o => o.Id).ToArray(),
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });
    }

    private static MethodRef MethodSubject(Observation observation)
    {
        foreach (var subject in observation.Subjects)
        {
            if (subject is MethodRef method) return method;
        }

        throw new InvalidOperationException(
            $"Observation '{observation.Id}' of type '{observation.ObservationType}' has no MethodRef subject.");
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
