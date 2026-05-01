using DumpDetective.Core.Abstractions;
using DumpDetective.Cli.Console;
using DumpDetective.Core.Models;
using System.Diagnostics;

namespace DumpDetective.Cli.Services;

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
                ConsoleUx.Info("Analysis run started.");
                if (_stages.Count > 0)
                {
                    int totalAnalyzers = _stages.Sum(s => s.AnalyzerCount);
                    ConsoleUx.Info($"Analyzer pipeline: {_stages.Count} logical stage(s), {totalAnalyzers} analyzer(s).");
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
                    (long cacheHits, long cacheMisses) = GetAnalyzerCacheDelta(diagnosticsEvent);
                    long cacheTotal = cacheHits + cacheMisses;
                    double cacheHitRatio = cacheTotal == 0 ? 0 : cacheHits * 100.0 / cacheTotal;
                    TimeSpan elapsed = diagnosticsEvent.DurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(diagnosticsEvent.DurationMs.Value)
                        : TimeSpan.Zero;
                    long analyzerScanCount = GetAnalyzerScanCount(diagnosticsEvent.AnalyzerName, diagnosticsEvent.ObjectScanCount);

                    List<string> details = [];
                    if (analyzerScanCount == 0)
                    {
                        details.Add("no heap walk");
                    }
                    if (cacheTotal >= 3 && analyzerScanCount > 0)
                    {
                        details.Add($"cache-hit {cacheHitRatio:F1}% ({cacheHits:N0}/{cacheMisses:N0})");
                    }

                    ConsoleUx.ObjectScanComplete(
                        diagnosticsEvent.AnalyzerName,
                        analyzerScanCount,
                        elapsed,
                        details.Count == 0 ? null : string.Join(" • ", details));
                }

                ResetAnalyzerTracking();
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
                    ConsoleUx.ObjectScanComplete(diagnosticsEvent.AnalyzerName, analyzerScanCount, elapsed, details);
                    ConsoleUx.Error($"Analyzer failed: {diagnosticsEvent.AnalyzerName} ({diagnosticsEvent.ExceptionType}: {diagnosticsEvent.ExceptionMessage})");
                }

                ResetAnalyzerTracking();
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
                    ConsoleUx.ObjectScanComplete(diagnosticsEvent.AnalyzerName, analyzerScanCount, elapsed, details);
                    ConsoleUx.Warning($"Analyzer canceled: {diagnosticsEvent.AnalyzerName}");
                }

                ResetAnalyzerTracking();
                CompleteAnalyzerInStage(diagnosticsEvent.AnalyzerName);
                break;

            case AnalysisDiagnosticsEventType.RunCompleted:
                CompleteCurrentStageIfOpen();
                ConsoleUx.Info($"Run diagnostics: scans={diagnosticsEvent.ObjectScanCount:N0}, cache-hits={diagnosticsEvent.CacheHits:N0}, cache-misses={diagnosticsEvent.CacheMisses:N0}");
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
            ConsoleUx.StageStart(stageIndex + 1, _stages.Count, $"Analyzer stage: {stage.Name} ({stage.AnalyzerCount})");
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
            ConsoleUx.StageComplete(stageIndex + 1, _stages.Count, $"Analyzer stage: {stage.Name}", _currentStageStopwatch?.Elapsed ?? TimeSpan.Zero);

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
            ConsoleUx.StageComplete(_currentStageIndex + 1, _stages.Count, $"Analyzer stage: {stage.Name}", _currentStageStopwatch?.Elapsed ?? TimeSpan.Zero);

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
                return;
            }

            _startedAnalyzersInCurrentStage++;
            AnalyzerStage stage = _stages[_currentStageIndex];
            ConsoleUx.AnalyzerStart(_startedAnalyzersInCurrentStage, stage.AnalyzerCount, analyzerName);
        }
    }

    private void PrintAnalyzerProgress(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
            return;

        DateTime utcNow = DateTime.UtcNow;
        if ((utcNow - _lastScanRenderUtc).TotalMilliseconds < 125)
            return;

        _lastScanRenderUtc = utcNow;
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

        if (!string.Equals(_currentAnalyzerPhase, basePhase, StringComparison.Ordinal))
        {
            _currentAnalyzerPhase = basePhase;
            ConsoleUx.AnalyzerPhase(basePhase);
        }

        // Parse detail out of the message if the analyzer embedded it as "phase • detail".
        string? detail = null;
        if (!string.IsNullOrWhiteSpace(diagnosticsEvent.Message))
        {
            int sep = diagnosticsEvent.Message.IndexOf(" • ", StringComparison.Ordinal);
            if (sep >= 0)
                detail = diagnosticsEvent.Message[(sep + 3)..];
        }

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

    private void PrintAnalyzerSubmoduleProgress(AnalysisDiagnosticsEvent diagnosticsEvent)
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

    private void StartAnalyzerTracking(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
        {
            return;
        }

        _currentAnalyzerName = diagnosticsEvent.AnalyzerName;
        _currentAnalyzerStartScanCount = diagnosticsEvent.ObjectScanCount;
        _currentAnalyzerStartCacheHits = diagnosticsEvent.CacheHits;
        _currentAnalyzerStartCacheMisses = diagnosticsEvent.CacheMisses;
        _currentAnalyzerLastScanCount = diagnosticsEvent.ObjectScanCount;
        _currentAnalyzerLastElapsedMs = 0;
        _currentAnalyzerLastNonZeroRate = 0;
        _currentAnalyzerNoGrowthTicks = 0;
        _currentAnalyzerPhase = "scanning heap";
        _currentSubmodule = null;
    }

    private long GetAnalyzerScanCount(string? analyzerName, long totalScanCount)
    {
        if (string.IsNullOrWhiteSpace(analyzerName) || !string.Equals(analyzerName, _currentAnalyzerName, StringComparison.Ordinal))
        {
            return totalScanCount;
        }

        long delta = totalScanCount - _currentAnalyzerStartScanCount;
        return Math.Max(0, delta);
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
    }

    private (long CacheHits, long CacheMisses) GetAnalyzerCacheDelta(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName) ||
            !string.Equals(diagnosticsEvent.AnalyzerName, _currentAnalyzerName, StringComparison.Ordinal))
        {
            return (diagnosticsEvent.CacheHits, diagnosticsEvent.CacheMisses);
        }

        long hitDelta = Math.Max(0, diagnosticsEvent.CacheHits - _currentAnalyzerStartCacheHits);
        long missDelta = Math.Max(0, diagnosticsEvent.CacheMisses - _currentAnalyzerStartCacheMisses);
        return (hitDelta, missDelta);
    }

    private static IReadOnlyList<AnalyzerStage> BuildStages(IReadOnlyList<IAnalyzer> analyzers, out Dictionary<string, int> analyzerStageByName)
    {
        IReadOnlyList<IAnalyzer> ordered = analyzers.ToList();

        List<AnalyzerStage> stages = [];
        analyzerStageByName = new Dictionary<string, int>(StringComparer.Ordinal);

        string? currentStageName = null;
        int currentCount = 0;
        int currentStageIndex = -1;

        foreach (IAnalyzer analyzer in ordered)
        {
            string stageName = ResolveStageName(analyzer);
            if (currentStageName is null)
            {
                currentStageName = stageName;
                currentStageIndex = 0;
                currentCount = 1;
                analyzerStageByName[analyzer.Name] = currentStageIndex;
                continue;
            }

            if (!string.Equals(currentStageName, stageName, StringComparison.OrdinalIgnoreCase))
            {
                stages.Add(new AnalyzerStage(currentStageName, currentCount));
                currentStageName = stageName;
                currentStageIndex++;
                currentCount = 1;
                analyzerStageByName[analyzer.Name] = currentStageIndex;
                continue;
            }

            currentCount++;
            analyzerStageByName[analyzer.Name] = currentStageIndex;
        }

        if (currentStageName is not null)
        {
            stages.Add(new AnalyzerStage(currentStageName, currentCount));
        }

        return stages;
    }

    private static string ResolveStageName(IAnalyzer analyzer)
    {
        string typeName = analyzer.GetType().Name;
        return typeName switch
        {
            nameof(DumpDetective.Analysis.Analyzers.MemoryAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.GCGenerationAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.ModuleAnalyzer)
                => "Running core memory analyzers",

            nameof(DumpDetective.Analysis.Analyzers.CrashAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.HangAnalyzer)
                => "Analyzing for crashes and hangs",

            nameof(DumpDetective.Analysis.Analyzers.MemoryLeakAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.CollectionAnalyzer)
                => "Detecting memory leaks",

            nameof(DumpDetective.Analysis.Analyzers.StaticRootLeakDetector)
            or nameof(DumpDetective.Analysis.Analyzers.ReferenceChainAnalyzer)
                => "Analyzing static roots and event handlers",

            nameof(DumpDetective.Analysis.Analyzers.GCHandleAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.DependentHandleAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.LohFragmentationAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.ThreadStackClusterAnalyzer)
                => "Performing ClrMD deep analysis",

            nameof(DumpDetective.Analysis.Analyzers.ThreadAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.LockGraphAnalyzer)
            or nameof(DumpDetective.Analysis.Analyzers.EventLeakAnalyzer)
                => "Analyzing threads and events",

            _ => $"{analyzer.Category} analysis"
        };
    }

    private sealed record AnalyzerStage(string Name, int AnalyzerCount);
}
