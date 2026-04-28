using DumpDetective.Analysis.Dump;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class LoadDumpStage(IDumpLoader dumpLoader) : IAnalysisStage
{
    public string Name => "Load dump";

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        state.LoadContext = await dumpLoader.LoadAsync(state.Resolved.DumpPath, cancellationToken);
    }
}
