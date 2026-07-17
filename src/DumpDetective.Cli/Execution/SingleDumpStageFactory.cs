using DumpDetective.Analysis.Dump;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Cli.Pipeline.Stages;
using DumpDetective.Reporting.Services;
using DumpDetective.Cli.Output;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Builds the fixed stage sequence for a single-dump analysis pipeline run.
/// </summary>
internal sealed class SingleDumpStageFactory(
    IDumpLoader dumpLoader,
    AnalyzerExecutionService analyzerExecutionService,
    ReportBuilderFacade reportBuilderFacade,
    ReportOutputWriter outputWriter)
{
    private readonly IDumpLoader _dumpLoader = dumpLoader;
    private readonly AnalyzerExecutionService _analyzerExecutionService = analyzerExecutionService;
    private readonly ReportBuilderFacade _reportBuilderFacade = reportBuilderFacade;
    private readonly ReportOutputWriter _outputWriter = outputWriter;

    public IReadOnlyList<IAnalysisStage> BuildStages() =>
    [
        new LoadDumpStage(_dumpLoader),
        new BuildHeapIndexStage(),
        new RunAnalyzersPipelineStage(_analyzerExecutionService),
        new BuildReportStage(_reportBuilderFacade),
        new WriteOutputStage(_outputWriter)
    ];
}
