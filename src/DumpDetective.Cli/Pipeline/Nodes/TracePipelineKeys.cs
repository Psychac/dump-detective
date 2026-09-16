namespace DumpDetective.Cli.Pipeline.Nodes;

/// <summary>
/// Data keys a trace run's nodes declare through <c>Produces</c>/<c>Consumes</c> — the trace-side
/// analog of <see cref="SingleDumpPipelineKeys"/>. Not namespaced per-artifact like
/// <see cref="ArtifactPipelineKeys.ForArtifact"/> — a session accepts at most one trace artifact
/// (see <c>SessionValidator</c>), so there's never more than one trace state to collide with.
/// </summary>
internal static class TracePipelineKeys
{
    public const string State = "trace.state";
    public const string Container = "trace.container";
    public const string Observations = "trace.observations";
    public const string Output = "trace.output";
}
