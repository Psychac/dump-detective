using DumpDetective.Utilities;
using System.Diagnostics;

namespace DumpDetective.Analyzers
{
    internal class AnalysisPipeline
    {
        private readonly List<AnalysisStage> _stages = new();
        private MemorySnapshot _previousSnapshot;

        public AnalysisPipeline(MemorySnapshot initialSnapshot)
        {
            _previousSnapshot = initialSnapshot;
        }

        public AnalysisPipeline AddStage(string name, params IAnalyzer[] analyzers)
        {
            _stages.Add(new AnalysisStage(name, analyzers));
            return this;
        }

        public void Execute(AnalysisContext context)
        {
            var stageResults = new List<(string StageName, TimeSpan Duration, int AnalyzerCount)>();

            for (int i = 0; i < _stages.Count; i++)
            {
                var stage = _stages[i];
                ConsoleUx.StageStart(i + 1, _stages.Count, stage.Name);
                var stageStopwatch = Stopwatch.StartNew();

                for (int analyzerIndex = 0; analyzerIndex < stage.Analyzers.Length; analyzerIndex++)
                {
                    var analyzer = stage.Analyzers[analyzerIndex];
                    ConsoleUx.AnalyzerStart(analyzerIndex + 1, stage.Analyzers.Length, analyzer.Name);
                    var analyzerStopwatch = Stopwatch.StartNew();
                    analyzer.Execute(context);
                    analyzerStopwatch.Stop();
                    ConsoleUx.AnalyzerComplete(analyzer.Name, analyzerStopwatch);
                }

                stageStopwatch.Stop();

                var snapshot = MemoryDiagnostic.TakeSnapshot($"{i + 1}. After {stage.Name}");
                MemoryDiagnostic.PrintDeltaToConsole(_previousSnapshot, snapshot);
                _previousSnapshot = snapshot;

                ConsoleUx.StageComplete(stage.Name, stageStopwatch);
                stageResults.Add((stage.Name, stageStopwatch.Elapsed, stage.Analyzers.Length));
            }

            ConsoleUx.PipelineSummary(stageResults);
        }

        private record AnalysisStage(string Name, IAnalyzer[] Analyzers);
    }
}
