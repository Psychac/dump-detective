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
                PrintAnalyzerStart(diagnosticsEvent.AnalyzerName);
                break;

            case AnalysisDiagnosticsEventType.AnalyzerProgress:
                PrintAnalyzerProgress(diagnosticsEvent);
                break;

            case AnalysisDiagnosticsEventType.AnalyzerCompleted:
                if (!string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
                {
                    TimeSpan elapsed = diagnosticsEvent.DurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(diagnosticsEvent.DurationMs.Value)
                        : TimeSpan.Zero;
                    ConsoleUx.ObjectScanComplete(diagnosticsEvent.AnalyzerName, diagnosticsEvent.ObjectScanCount, elapsed);

                    string durationSegment = diagnosticsEvent.DurationMs.HasValue
                        ? $" ({diagnosticsEvent.DurationMs.Value:F0} ms)"
                        : string.Empty;

                    long cacheTotal = diagnosticsEvent.CacheHits + diagnosticsEvent.CacheMisses;
                    double cacheHitRatio = cacheTotal == 0 ? 0 : diagnosticsEvent.CacheHits * 100.0 / cacheTotal;

                    ConsoleUx.Success($"{diagnosticsEvent.AnalyzerName} completed{durationSegment} · scans {diagnosticsEvent.ObjectScanCount:N0} · cache-hit {cacheHitRatio:F1}%");
                }

                CompleteAnalyzerInStage(diagnosticsEvent.AnalyzerName);
                break;

            case AnalysisDiagnosticsEventType.AnalyzerFailed:
                if (!string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
                {
                    TimeSpan elapsed = diagnosticsEvent.DurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(diagnosticsEvent.DurationMs.Value)
                        : TimeSpan.Zero;
                    ConsoleUx.ObjectScanComplete(diagnosticsEvent.AnalyzerName, diagnosticsEvent.ObjectScanCount, elapsed);
                    ConsoleUx.Error($"Analyzer failed: {diagnosticsEvent.AnalyzerName} ({diagnosticsEvent.ExceptionType}: {diagnosticsEvent.ExceptionMessage})");
                }

                CompleteAnalyzerInStage(diagnosticsEvent.AnalyzerName);
                break;

            case AnalysisDiagnosticsEventType.AnalyzerCanceled:
                if (!string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
                {
                    TimeSpan elapsed = diagnosticsEvent.DurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(diagnosticsEvent.DurationMs.Value)
                        : TimeSpan.Zero;
                    ConsoleUx.ObjectScanComplete(diagnosticsEvent.AnalyzerName, diagnosticsEvent.ObjectScanCount, elapsed);
                    ConsoleUx.Warning($"Analyzer canceled: {diagnosticsEvent.AnalyzerName}");
                }

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
            ConsoleUx.Info($"Analyzer {_startedAnalyzersInCurrentStage}/{stage.AnalyzerCount}: {analyzerName}");
        }
    }

    private void PrintAnalyzerProgress(AnalysisDiagnosticsEvent diagnosticsEvent)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsEvent.AnalyzerName))
        {
            return;
        }

        DateTime utcNow = DateTime.UtcNow;
        if ((utcNow - _lastScanRenderUtc).TotalMilliseconds < 125)
        {
            return;
        }

        _lastScanRenderUtc = utcNow;
        TimeSpan elapsed = diagnosticsEvent.DurationMs.HasValue
            ? TimeSpan.FromMilliseconds(diagnosticsEvent.DurationMs.Value)
            : TimeSpan.Zero;

        ConsoleUx.ObjectScanProgress($"{diagnosticsEvent.AnalyzerName}", diagnosticsEvent.ObjectScanCount, elapsed);
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
