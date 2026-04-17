using DumpDetective.Core.Utilities;
using DumpDetective.Core.Models;
using System.Diagnostics;

namespace DumpDetective.Analysis.Pipeline
{
    internal class AnalysisPipeline
    {
        private readonly List<AnalysisStage> _stages = new();
        private readonly bool _enableMemoryDiagnostics;
        private readonly bool _forceGCBetweenStages;
        private MemorySnapshot? _previousSnapshot;

        public AnalysisPipeline(MemorySnapshot? initialSnapshot, bool enableMemoryDiagnostics, bool forceGCBetweenStages = false)
        {
            _previousSnapshot = initialSnapshot;
            _enableMemoryDiagnostics = enableMemoryDiagnostics;
            _forceGCBetweenStages = forceGCBetweenStages;
        }

        public AnalysisPipeline AddStage(string name, params IAnalyzer[] analyzers)
        {
            _stages.Add(new AnalysisStage(name, analyzers));
            return this;
        }

        public (IReadOnlyList<InsightFinding> Findings, IReadOnlyDictionary<string, AnalyzerDomainResult> DomainResults) Execute(AnalysisContext context)
        {
            var stageResults = new List<(string StageName, TimeSpan Duration, int AnalyzerCount)>();
            var findings = new List<InsightFinding>(capacity: 64);
            var domainResults = new Dictionary<string, AnalyzerDomainResult>(StringComparer.Ordinal);
            var pipelineStopwatch = Stopwatch.StartNew();

            for (int i = 0; i < _stages.Count; i++)
            {
                var stage = _stages[i];
                TimeSpan? etaBeforeStart = null;
                if (stageResults.Count > 0)
                {
                    double avgStageSeconds = stageResults.Average(s => s.Duration.TotalSeconds);
                    int stagesRemainingIncludingCurrent = _stages.Count - i;
                    etaBeforeStart = TimeSpan.FromSeconds(avgStageSeconds * stagesRemainingIncludingCurrent);
                }

                ConsoleUx.StageStart(i + 1, _stages.Count, stage.Name, etaBeforeStart);
                var stageStopwatch = Stopwatch.StartNew();

                for (int analyzerIndex = 0; analyzerIndex < stage.Analyzers.Length; analyzerIndex++)
                {
                    var analyzer = stage.Analyzers[analyzerIndex];
                    ConsoleUx.AnalyzerStart(analyzerIndex + 1, stage.Analyzers.Length, analyzer.Name);
                    var analyzerStopwatch = Stopwatch.StartNew();
                    bool succeeded = false;
                    try
                    {
                        AnalyzerExecutionResult result = analyzer.Execute(context);
                        if (result.Findings.Count > 0)
                        {
                            findings.AddRange(result.Findings);
                        }
                        if (result.DomainResult != null)
                        {
                            domainResults[analyzer.Name] = result.DomainResult;
                        }

                        succeeded = true;
                    }
                    catch (Exception ex)
                    {
                        findings.Add(CreateAnalyzerFailureFinding(stage.Name, analyzer.Name, ex));
                        ConsoleUx.Error($"Analyzer {analyzerIndex + 1}/{stage.Analyzers.Length} failed: {analyzer.Name} ({ex.GetType().Name}: {ex.Message})");
                    }
                    finally
                    {
                        analyzerStopwatch.Stop();
                    }

                    if (succeeded)
                    {
                        ConsoleUx.AnalyzerComplete(analyzerIndex + 1, stage.Analyzers.Length, analyzer.Name, analyzerStopwatch);
                    }
                }

                stageStopwatch.Stop();

                if (_enableMemoryDiagnostics && _previousSnapshot != null)
                {
                    var snapshot = MemoryDiagnostic.TakeSnapshot($"{i + 1}. After {stage.Name}");
                    MemoryDiagnostic.PrintDeltaToConsole(_previousSnapshot, snapshot);
                    _previousSnapshot = snapshot;
                }

                if (_forceGCBetweenStages)
                {
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                }

                ConsoleUx.StageComplete(stage.Name, stageStopwatch);
                stageResults.Add((stage.Name, stageStopwatch.Elapsed, stage.Analyzers.Length));

                int completedStages = i + 1;
                int remainingStages = _stages.Count - completedStages;
                TimeSpan? etaAfterStage = null;
                if (remainingStages > 0)
                {
                    double avgStageSeconds = stageResults.Average(s => s.Duration.TotalSeconds);
                    etaAfterStage = TimeSpan.FromSeconds(avgStageSeconds * remainingStages);
                }

                ConsoleUx.PipelineProgress(completedStages, _stages.Count, pipelineStopwatch.Elapsed, etaAfterStage);
            }

            pipelineStopwatch.Stop();
            ConsoleUx.PipelineSummary(stageResults);
            return (findings, domainResults);
        }

        private static InsightFinding CreateAnalyzerFailureFinding(string stageName, string analyzerName, Exception exception)
        {
            string exceptionType = exception.GetType().Name;
            string message = string.IsNullOrWhiteSpace(exception.Message) ? "No exception message provided." : exception.Message;

            return new InsightFinding(
                Analyzer: analyzerName,
                Category: "Pipeline",
                Severity: FindingSeverity.Warning,
                Title: "Analyzer execution failed",
                Evidence: $"Stage '{stageName}' analyzer '{analyzerName}' threw {exceptionType}: {message}",
                Recommendation: "Inspect analyzer logic and dump compatibility, then re-run analysis to recover full report coverage.",
                Tags: ["pipeline", "analyzer-failure", "resilience"]);
        }

        private record AnalysisStage(string Name, IAnalyzer[] Analyzers);
    }
}


