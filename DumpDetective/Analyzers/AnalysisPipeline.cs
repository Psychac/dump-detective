using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class AnalysisPipeline
    {
        private readonly List<AnalysisStage> _stages = new();
        private readonly OutputWriter _writer;
        private MemorySnapshot _previousSnapshot;

        public AnalysisPipeline(OutputWriter writer, MemorySnapshot initialSnapshot)
        {
            _writer = writer;
            _previousSnapshot = initialSnapshot;
        }

        public AnalysisPipeline AddStage(string name, params IAnalyzer[] analyzers)
        {
            _stages.Add(new AnalysisStage(name, analyzers));
            return this;
        }

        public void Execute(AnalysisContext context)
        {
            foreach (var stage in _stages)
            {
                Console.WriteLine($"\n▶ {stage.Name}...");

                foreach (var analyzer in stage.Analyzers)
                {
                    analyzer.Execute(context);
                }

                var snapshot = MemoryDiagnostic.TakeSnapshot($"{_stages.IndexOf(stage) + 1}. After {stage.Name}");
                MemoryDiagnostic.PrintDeltaToConsole(_previousSnapshot, snapshot);
                _previousSnapshot = snapshot;
            }
        }

        private record AnalysisStage(string Name, IAnalyzer[] Analyzers);
    }
}
