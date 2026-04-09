using DumpDetective.Utilities;

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
            for (int i = 0; i < _stages.Count; i++)
            {
                var stage = _stages[i];
                Console.WriteLine($"\n▶ {stage.Name}...");

                foreach (var analyzer in stage.Analyzers)
                {
                    analyzer.Execute(context);
                }

                var snapshot = MemoryDiagnostic.TakeSnapshot($"{i + 1}. After {stage.Name}");
                MemoryDiagnostic.PrintDeltaToConsole(_previousSnapshot, snapshot);
                _previousSnapshot = snapshot;
            }
        }

        private record AnalysisStage(string Name, IAnalyzer[] Analyzers);
    }
}
