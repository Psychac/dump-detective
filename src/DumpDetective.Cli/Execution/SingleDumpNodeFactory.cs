using DumpDetective.Analysis.Dump;
using DumpDetective.Cli.Output;
using DumpDetective.Cli.Pipeline.Nodes;
using DumpDetective.Orchestration.Graph;
using DumpDetective.Reporting.Services;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Builds the single-dump analysis pipeline's nodes. Phase 4 step 1: still a fixed, hand-declared
/// set — see docs/refactor/modularity/phase-4-session-orchestration.md — replaced by
/// capability-derived node generation in a later step.
/// </summary>
internal sealed class SingleDumpNodeFactory(
    IDumpLoader dumpLoader,
    AnalyzerExecutionService analyzerExecutionService,
    ReportBuilderFacade reportBuilderFacade,
    ReportOutputWriter outputWriter)
{
    private readonly IDumpLoader _dumpLoader = dumpLoader;
    private readonly AnalyzerExecutionService _analyzerExecutionService = analyzerExecutionService;
    private readonly ReportBuilderFacade _reportBuilderFacade = reportBuilderFacade;
    private readonly ReportOutputWriter _outputWriter = outputWriter;

    public IReadOnlyList<IPipelineNode> BuildNodes() =>
        [
            new LoadDumpNode(_dumpLoader),
            new BuildHeapIndexNode(_analyzerExecutionService),
            new RunAnalyzersNode(_analyzerExecutionService),
            new BuildReportNode(_reportBuilderFacade),
            new WriteOutputNode(_outputWriter)
        ];
}
