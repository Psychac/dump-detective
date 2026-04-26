namespace DumpDetective.Cli.Pipeline;

internal interface IAnalysisStage
{
    string Name { get; }
    Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken);
}
