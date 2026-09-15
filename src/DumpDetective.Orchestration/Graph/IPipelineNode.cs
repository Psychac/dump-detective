namespace DumpDetective.Orchestration.Graph;

public interface IPipelineNode
{
    string Id { get; }
    IReadOnlyCollection<string> Produces { get; }
    IReadOnlyCollection<string> Consumes { get; }
    ValueTask ExecuteAsync(PipelineNodeContext context, CancellationToken cancellationToken);
}
