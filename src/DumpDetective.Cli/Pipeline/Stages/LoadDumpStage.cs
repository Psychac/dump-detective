using DumpDetective.Cli.Services;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class LoadDumpStage(DumpLoader dumpLoader) : IAnalysisStage
{
    public string Name => "Load dump";

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        state.LoadContext = await dumpLoader.LoadAsync(state.Resolved.DumpPath, cancellationToken);
    }
}
