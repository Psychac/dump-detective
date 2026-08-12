using DumpDetective.Core.Abstractions;
using DumpDetective.Cli.Console;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Services;
using System.Diagnostics;
using DumpDetective.Cli.Execution;
using DumpDetective.Cli.Pipeline;

namespace DumpDetective.Cli.Diagnostics;

internal sealed class ConsoleDiagnosticsSink : IAnalysisDiagnosticsSink
{
    private readonly bool _enabled;
    private readonly IReadOnlyList<AnalyzerStage> _stages;
    private readonly Dictionary<string, int> _analyzerStageByName;
    private readonly Lock _gate = new();

    private int _currentStageIndex = -1;
    private int _startedAnalyzersInCurrentStage;
    private int _completedAnalyzersInCurrentStage;
    private Stopwatch? _currentStageStopwatch;
    private DateTime _lastScanRenderUtc = DateTime.MinValue;
    private DateTime _lastPhasePrintUtc = DateTime.MinValue;
    private string? _currentAnalyzerName;
    private long _currentAnalyzerStartScanCount;
    private long _currentAnalyzerStartCacheHits;
    private long _currentAnalyzerStartCacheMisses;
    private long _currentAnalyzerLastScanCount;
    private double _currentAnalyzerLastElapsedMs;
    private double _currentAnalyzerLastNonZeroRate;
    private int _currentAnalyzerNoGrowthTicks;
    private string _currentAnalyzerPhase = "scanning";
    private string? _currentSubmodule;
    // Tracks phase-transition durations for the currently-running analyzer/pass so completed
    // phases can be printed with how long they took, instead of only ever showing the phase that
    // happened to be active when the whole thing finished.
    private PhaseTimeline? _currentPhaseTimeline;

    public ConsoleDiagnosticsSink(bool enabled, IReadOnlyList<IAnalyzer> analyzers)
    {
        _enabled = enabled;
        _stages = BuildStages(analyzers, out Dictionary<string, int> analyzerStageByName);
        _analyzerStageByName = analyzerStageByName;
    }

    public void Publish(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        if (!_enabled)
        {
            PublishProgress(diagnosticsEvent);
            return;
        }

        PublishVerbose(diagnosticsEvent);
    }

    public void PublishProgress(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        switch (diagnosticsEvent.EventType)
        {
            case AnalysisDiagnosticsEventType.RunStarted:
                if (_stages.Count > 0)
                {
                    int totalAnalyzers = _stages.Sum(s => s.AnalyzerCount);
                    string groups = _stages.Count == 1 ? "1 group" : $"{_stages.Count} groups";
                    ConsoleUx.Info($"Running {totalAnalyzers} analyzer{(totalAnalyzers == 1 ? "" : "s")} in {groups}");
                }
                break;

            case AnalysisDiagnosticsEventType.AnalyzerStarted:
                StartStageIfNeeded(diagnosticsEvent.AnalyzerName);
                StartAnalyzerTracking(diagnosticsEvent);
                PrintAnalyzerStart(diagnosticsEvent.AnalyzerName);
                break;

            case AnalysisDiagnosticsEventType.AnalyzerProgress:
                PrintAnalyzerProgress(diagnosticsEvent);
                break;

            case AnalysisDiagnosticsEventType.AnalyzerSubmoduleProgress:
                PrintAnalyzerSubmoduleProgress(diagnosticsEvent);
                break;

            case AnalysisDiagnosticsEventType.AnalyzerCompleted:
                    if (!string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
                {
                    long cacheHits = diagnosticsEvent.CacheHits - _currentAnalyzerStartCacheHits;
                    long cacheMisses = diagnosticsEvent.CacheMisses - _currentAnalyzerStartCacheMisses;
                    long cacheTotal = cacheHits + cacheMisses;
                    double cacheHitRatio = cacheTotal == 0 ? 0 : cacheHits * 100.0 / cacheTotal;
                    TimeSpan elapsed = diagnosticsEvent.DurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(diagnosticsEvent.DurationMs.Value)
                        : TimeSpan.Zero;
                    long analyzerScanCount = GetAnalyzerScanCount(diagnosticsEvent.AnalyzerName, diagnosticsEvent.ObjectScanCount);

                    var details = new List<string>();
                    if (analyzerScanCount == 0)
                    {
                        details.Add("no heap walk");
                    }
                    if (cacheTotal >= 3 && analyzerScanCount > 0)
                    {
                        details.Add($"cache-hit {cacheHitRatio:F1}% ({cacheHits:N0}/{cacheMisses:N0})");
                    }

                    // Flush before resetting: the timeline is discarded by ResetAnalyzerTracking,
                    // and its still-open final phase's duration is only known now, against this
                    // event's elapsed — otherwise that last phase never gets a printed duration.
                    FlushPhaseTimeline(diagnosticsEvent.AnalyzerName, elapsed);

                    // Reset before printing: closes the window where a stale AnalyzerProgress
                    // callback (posted via Progress<T>/ThreadPool) could fire after ObjectScanComplete
                    // sets _scanLineActive=false but before _currentAnalyzerName is cleared.
                    // Any queued callback arriving after this point will fail the name guard.
                    ResetAnalyzerTracking();
                    ConsoleUx.ObjectScanComplete(
                        diagnosticsEvent.AnalyzerName,
                        analyzerScanCount,
                        elapsed,
                        details.Count == 0 ? null : string.Join(" • ", details));
                }
                else
                {
                    ResetAnalyzerTracking();
                }

                CompleteAnalyzerInStage(diagnosticsEvent.AnalyzerName);
                break;

            case AnalysisDiagnosticsEventType.AnalyzerFailed:
                if (!string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
                {
                    TimeSpan elapsed = diagnosticsEvent.DurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(diagnosticsEvent.DurationMs.Value)
                        : TimeSpan.Zero;
                    long analyzerScanCount = GetAnalyzerScanCount(diagnosticsEvent.AnalyzerName, diagnosticsEvent.ObjectScanCount);
                    string details = analyzerScanCount == 0 ? "no heap walk • status failed" : "status failed";
                    FlushPhaseTimeline(diagnosticsEvent.AnalyzerName, elapsed);
                    ResetAnalyzerTracking();
                    ConsoleUx.ObjectScanComplete(diagnosticsEvent.AnalyzerName, analyzerScanCount, elapsed, details);
                    ConsoleUx.Error($"Analyzer failed: {diagnosticsEvent.AnalyzerName} ({diagnosticsEvent.ExceptionType}: {diagnosticsEvent.ExceptionMessage})");
                }
                else
                {
                    ResetAnalyzerTracking();
                }

                CompleteAnalyzerInStage(diagnosticsEvent.AnalyzerName);
                break;

            case AnalysisDiagnosticsEventType.AnalyzerCanceled:
                if (!string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
                {
                    TimeSpan elapsed = diagnosticsEvent.DurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(diagnosticsEvent.DurationMs.Value)
                        : TimeSpan.Zero;
                    long analyzerScanCount = GetAnalyzerScanCount(diagnosticsEvent.AnalyzerName, diagnosticsEvent.ObjectScanCount);
                    string details = analyzerScanCount == 0 ? "no heap walk • status canceled" : "status canceled";
                    FlushPhaseTimeline(diagnosticsEvent.AnalyzerName, elapsed);
                    ResetAnalyzerTracking();
                    ConsoleUx.ObjectScanComplete(diagnosticsEvent.AnalyzerName, analyzerScanCount, elapsed, details);
                    ConsoleUx.Warning($"Analyzer canceled: {diagnosticsEvent.AnalyzerName}");
                }
                else
                {
                    ResetAnalyzerTracking();
                }

                CompleteAnalyzerInStage(diagnosticsEvent.AnalyzerName);
                break;

            case AnalysisDiagnosticsEventType.RunCompleted:
                ConsoleUx.AnalyzerRunSummary(diagnosticsEvent.ObjectScanCount, diagnosticsEvent.CacheHits, diagnosticsEvent.CacheMisses);
                CompleteCurrentStageIfOpen();
                break;

            default:
                break;
        }
    }

    private static void PublishVerbose(AnalysisDiagnosticsEvent diagnosticsEvent)
    {

        string analyzerSegment = string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName)
            ? string.Empty
            : $" {diagnosticsEvent.AnalyzerName}";

        string durationSegment = diagnosticsEvent.DurationMs.HasValue
            ? $", duration={diagnosticsEvent.DurationMs.Value:F0}ms"
            : string.Empty;

        string exceptionSegment = !string.IsNullOrWhiteSpace(diagnosticsEvent.ExceptionType)
            ? $", ex={diagnosticsEvent.ExceptionType}: {diagnosticsEvent.ExceptionMessage}"
            : string.Empty;

        ConsoleUx.WriteVerbose(
            $"{diagnosticsEvent.EventType}{analyzerSegment} | category={diagnosticsEvent.Category}{durationSegment}, scans={diagnosticsEvent.ObjectScanCount}, cacheHits={diagnosticsEvent.CacheHits}, cacheMisses={diagnosticsEvent.CacheMisses}{exceptionSegment} | {diagnosticsEvent.Message}");
    }

    private void StartStageIfNeeded(string? analyzerName)
    {
        if (string.IsNullOrWhiteSpace(analyzerName))
        {
            return;
        }

        if (!_analyzerStageByName.TryGetValue(analyzerName, out int stageIndex))
        {
            return;
        }

        lock (_gate)
        {
            if (_currentStageIndex == stageIndex)
            {
                return;
            }

            _currentStageIndex = stageIndex;
            _startedAnalyzersInCurrentStage = 0;
            _completedAnalyzersInCurrentStage = 0;
            _currentStageStopwatch = Stopwatch.StartNew();

            AnalyzerStage stage = _stages[stageIndex];
            ConsoleUx.AnalyzerGroupStart(stageIndex + 1, _stages.Count, stage.Name);
        }
    }

    private void CompleteAnalyzerInStage(string? analyzerName)
    {
        if (string.IsNullOrWhiteSpace(analyzerName))
        {
            return;
        }

        if (!_analyzerStageByName.TryGetValue(analyzerName, out int stageIndex))
        {
            return;
        }

        lock (_gate)
        {
            if (_currentStageIndex != stageIndex)
            {
                return;
            }

            _completedAnalyzersInCurrentStage++;
            AnalyzerStage stage = _stages[stageIndex];
            if (_completedAnalyzersInCurrentStage < stage.AnalyzerCount)
            {
                return;
            }

            _currentStageStopwatch?.Stop();
            ConsoleUx.AnalyzerGroupComplete(stageIndex + 1, _stages.Count, stage.Name, _currentStageStopwatch?.Elapsed ?? TimeSpan.Zero);

            _currentStageIndex = -1;
            _startedAnalyzersInCurrentStage = 0;
            _completedAnalyzersInCurrentStage = 0;
            _currentStageStopwatch = null;
        }
    }

    private void CompleteCurrentStageIfOpen()
    {
        lock (_gate)
        {
            if (_currentStageIndex < 0)
            {
                return;
            }

            AnalyzerStage stage = _stages[_currentStageIndex];
            _currentStageStopwatch?.Stop();
            ConsoleUx.AnalyzerGroupComplete(_currentStageIndex + 1, _stages.Count, stage.Name, _currentStageStopwatch?.Elapsed ?? TimeSpan.Zero);

            _currentStageIndex = -1;
            _startedAnalyzersInCurrentStage = 0;
            _completedAnalyzersInCurrentStage = 0;
            _currentStageStopwatch = null;
        }
    }

    private void PrintAnalyzerStart(string? analyzerName)
    {
        if (string.IsNullOrWhiteSpace(analyzerName))
        {
            return;
        }

        lock (_gate)
        {
            if (_currentStageIndex < 0 || _currentStageIndex >= _stages.Count)
            {
                // Not part of any tracked analyzer group (e.g. the shared heap-index scan
                // dispatcher) — still print an immediate start line so the console doesn't go
                // silent between the previous stage completing and this pass's first tick.
                ConsoleUx.AnalyzerStart(analyzerName);
                return;
            }

            _startedAnalyzersInCurrentStage++;
            AnalyzerStage stage = _stages[_currentStageIndex];
            ConsoleUx.AnalyzerStart(_startedAnalyzersInCurrentStage, stage.AnalyzerCount, analyzerName);
        }
    }

    private void PrintAnalyzerProgress(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
                return;

            // Drop stale progress events that arrive after the analyzer has already completed.
            // This happens when AnalyzerProgress is dispatched via Progress<T>/ThreadPool and a
            // queued callback fires after AnalyzerCompleted has already reset _currentAnalyzerName.
            if (!string.Equals(diagnosticsEvent.AnalyzerName, _currentAnalyzerName, StringComparison.Ordinal))
                return;

            TimeSpan elapsed = diagnosticsEvent.DurationMs.HasValue
                ? TimeSpan.FromMilliseconds(diagnosticsEvent.DurationMs.Value)
                : TimeSpan.Zero;

            long analyzerScans = GetAnalyzerScanCount(diagnosticsEvent.AnalyzerName, diagnosticsEvent.ObjectScanCount);
            long tickDelta = Math.Max(0, diagnosticsEvent.ObjectScanCount - _currentAnalyzerLastScanCount);
            _currentAnalyzerLastScanCount = diagnosticsEvent.ObjectScanCount;

            double elapsedMs = elapsed.TotalMilliseconds;
            double deltaMs = Math.Max(0, elapsedMs - _currentAnalyzerLastElapsedMs);
            if (tickDelta > 0 && deltaMs > 0)
                _currentAnalyzerLastNonZeroRate = tickDelta / (deltaMs / 1000.0);

            _currentAnalyzerLastElapsedMs = elapsedMs;

            if (tickDelta == 0)
                _currentAnalyzerNoGrowthTicks++;
            else
                _currentAnalyzerNoGrowthTicks = 0;

            // Extract the base phase (part before " • ") for change detection.
            // Only emit a ↳ phase line on genuine phase transitions — not when only the detail portion
            // changes (e.g. "42 wasteful" → "43 wasteful", or "3/10 types" → "4/10 types").
            // Without this guard those analyzers flood the console with a phase line every 125 ms,
            // which causes the visible display artefacts (rapid line strobing) the user hears as beeps.
            string basePhase;
            if (string.IsNullOrWhiteSpace(diagnosticsEvent.Message))
            {
                basePhase = _currentAnalyzerNoGrowthTicks >= 3 ? "processing results" : _currentAnalyzerPhase;
            }
            else
            {
                int phaseSep = diagnosticsEvent.Message.IndexOf(" • ", StringComparison.Ordinal);
                basePhase = phaseSep >= 0 ? diagnosticsEvent.Message[..phaseSep] : diagnosticsEvent.Message;
            }

            // Record every genuine phase transition immediately and unconditionally — never behind
            // the print throttle below. Gating the Record call on the same cooldown as the console
            // render used to silently drop transitions rather than defer them: if several phases
            // changed within one throttle window, only whichever was current when the cooldown next
            // expired ever got recorded, so fast-changing phases (e.g. sub-300ms participants in the
            // shared heap-index scan dispatcher) vanished instead of just being batched together.
            if (!string.Equals(_currentAnalyzerPhase, basePhase, StringComparison.Ordinal))
            {
                _currentAnalyzerPhase = basePhase;
                _currentPhaseTimeline?.Record(basePhase, elapsed);
            }

            // Parse detail out of the message if the analyzer embedded it as "phase • detail".
            // Falls back to the phase itself when there's no explicit detail (e.g. the dispatcher's
            // per-participant phases above), so the live line still shows what's currently running.
            string? detail = null;
            if (!string.IsNullOrWhiteSpace(diagnosticsEvent.Message))
            {
                int sep = diagnosticsEvent.Message.IndexOf(" • ", StringComparison.Ordinal);
                detail = sep >= 0 ? diagnosticsEvent.Message[(sep + 3)..] : basePhase;
            }

            // Rendering below is throttled for console-spam reasons — the bookkeeping above never
            // is, so drained phase segments and scan-count deltas stay accurate even on ticks whose
            // render gets skipped.
            DateTime utcNow = DateTime.UtcNow;
            if ((utcNow - _lastPhasePrintUtc).TotalMilliseconds >= 500)
            {
                ConsoleUx.PhaseBreakdown(_currentPhaseTimeline?.DrainCompletedSegments() ?? []);
                _lastPhasePrintUtc = utcNow;
            }

            if ((utcNow - _lastScanRenderUtc).TotalMilliseconds < 125)
                return;
            _lastScanRenderUtc = utcNow;

            double displayRate = _currentAnalyzerLastNonZeroRate;
            if (displayRate <= 0 && elapsed.TotalSeconds > 0)
                displayRate = analyzerScans / elapsed.TotalSeconds;

            ConsoleUx.ObjectScanProgress(
                diagnosticsEvent.AnalyzerName,
                analyzerScans,
                elapsed,
                detail,
                displayRate > 0 ? displayRate : null);
        }
    }

    private void PrintAnalyzerSubmoduleProgress(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
            {
                return;
            }

            if (!string.Equals(diagnosticsEvent.AnalyzerName, _currentAnalyzerName, StringComparison.Ordinal))
            {
                return;
            }

            string submodule = string.IsNullOrWhiteSpace(diagnosticsEvent.Message) ? "background walk" : diagnosticsEvent.Message;
            if (!string.Equals(_currentSubmodule, submodule, StringComparison.Ordinal))
            {
                _currentSubmodule = submodule;
                ConsoleUx.AnalyzerPhase(submodule);
            }
        }
    }

    // Build logical analyzer groups (stages) based on analyzer type names.
    // Mirrors the rank mapping used by AnalyzerFilterService so the console groups
    // match the order analyzers are executed in the pipeline.
    private static IReadOnlyList<AnalyzerStage> BuildStages(IReadOnlyList<IAnalyzer> analyzers, out Dictionary<string, int> analyzerStageByName)
    {
        // Group analyzers by report domain (SectionIdDomainMap). Order groups
        // using the canonical DomainsInOrder, then any remaining domains by
        // lowest pipeline rank so display matches both reporting and execution.
        var domainBuckets = new Dictionary<string, (int MinRank, List<string> Names)>(StringComparer.Ordinal);

        foreach (IAnalyzer a in analyzers)
        {
            string domain = SectionIdDomainMap.GetDomain(a.Name);
            if (string.IsNullOrWhiteSpace(domain))
                domain = a.Category ?? "Other";

            int rank = AnalyzerFilterService.GetStageRank(a);
            if (!domainBuckets.TryGetValue(domain, out var entry))
            {
                entry = (rank, new List<string>());
                domainBuckets[domain] = entry;
            }

            entry.Names.Add(a.Name);
            if (rank < entry.MinRank)
                entry.MinRank = rank;

            domainBuckets[domain] = entry;
        }

        var ordered = new List<KeyValuePair<string, (int MinRank, List<string> Names)>>();

        // First, include domains in the canonical reporting order
        foreach (string d in SectionIdDomainMap.DomainsInOrder)
        {
            if (domainBuckets.TryGetValue(d, out var entry))
            {
                ordered.Add(new KeyValuePair<string, (int, List<string>)>(d, entry));
                domainBuckets.Remove(d);
            }
        }

        // Then include any remaining domains ordered by MinRank then name
        var remaining = domainBuckets.OrderBy(kv => kv.Value.MinRank).ThenBy(kv => kv.Key, StringComparer.Ordinal);
        ordered.AddRange(remaining);

        var stages = new List<AnalyzerStage>();
        analyzerStageByName = new Dictionary<string, int>(StringComparer.Ordinal);
        int stageIndex = 0;

        foreach (var kv in ordered)
        {
            string domain = kv.Key;
            List<string> names = kv.Value.Names;
            string stageName = string.IsNullOrWhiteSpace(domain) ? "Other" : domain;
            stages.Add(new AnalyzerStage(Name: stageName, AnalyzerCount: names.Count));

            foreach (string name in names)
                analyzerStageByName[name] = stageIndex;

            stageIndex++;
        }

        if (stages.Count == 0)
        {
            stages.Add(new AnalyzerStage(Name: "Analyzers", AnalyzerCount: analyzers.Count));
            analyzerStageByName = analyzers.Select((a, i) => (a.Name, 0)).ToDictionary(t => t.Name, t => 0, StringComparer.Ordinal);
        }

        return stages;
    }


    private void StartAnalyzerTracking(AnalysisDiagnosticsEvent ev)
    {
        _currentAnalyzerName = ev.AnalyzerName;
        _currentAnalyzerStartScanCount = ev.ObjectScanCount;
        _currentAnalyzerStartCacheHits = ev.CacheHits;
        _currentAnalyzerStartCacheMisses = ev.CacheMisses;
        _currentAnalyzerLastScanCount = ev.ObjectScanCount;
        _currentAnalyzerLastElapsedMs = ev.DurationMs ?? 0;
        _currentAnalyzerLastNonZeroRate = 0;
        _currentAnalyzerNoGrowthTicks = 0;
        _currentAnalyzerPhase = "scanning";
        _currentSubmodule = null;
        _currentPhaseTimeline = new PhaseTimeline();
        _currentPhaseTimeline.Record(_currentAnalyzerPhase, TimeSpan.FromMilliseconds(_currentAnalyzerLastElapsedMs));
    }

    /// <summary>
    /// Prints the currently-open phase's final duration before its timeline is discarded by
    /// <see cref="ResetAnalyzerTracking"/>. Guarded by name, matching the same staleness check
    /// <see cref="PrintAnalyzerProgress"/> uses, since a completion event for one analyzer must
    /// never flush another analyzer's still-in-progress timeline.
    /// </summary>
    private void FlushPhaseTimeline(string? analyzerName, TimeSpan elapsed)
    {
        if (_currentPhaseTimeline is null)
            return;
        if (!string.Equals(analyzerName, _currentAnalyzerName, StringComparison.Ordinal))
            return;

        ConsoleUx.PhaseBreakdown(_currentPhaseTimeline.GetRemainingSegments(elapsed));
    }

    private void ResetAnalyzerTracking()
    {
        _currentAnalyzerName = null;
        _currentAnalyzerStartScanCount = 0;
        _currentAnalyzerStartCacheHits = 0;
        _currentAnalyzerStartCacheMisses = 0;
        _currentAnalyzerLastScanCount = 0;
        _currentAnalyzerLastElapsedMs = 0;
        _currentAnalyzerLastNonZeroRate = 0;
        _currentAnalyzerNoGrowthTicks = 0;
        _currentAnalyzerPhase = "scanning";
        _currentSubmodule = null;
        _currentPhaseTimeline = null;
    }



    private static long GetAnalyzerScanCount(string analyzerName, long reportedScanCount)
        => reportedScanCount;

    private sealed record AnalyzerStage(string Name, int AnalyzerCount);
}
