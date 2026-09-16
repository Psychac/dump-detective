namespace DumpDetective.Cli.Pipeline.Nodes;

/// <summary>
/// Data keys one artifact's load/index/analyze nodes produce and consume — see
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 4. Namespaced by
/// artifact id so N artifacts' node instances (one load/index/analyze triple each) can share one
/// <see cref="DumpDetective.Orchestration.Graph.PipelineNodeContext"/> without colliding.
/// </summary>
/// <remarks>
/// Report/output are deliberately not artifact-scoped keys here — exactly one report is produced
/// per session regardless of N, under the session-level <see cref="SingleDumpPipelineKeys.State"/>/
/// <see cref="SingleDumpPipelineKeys.ReportDocument"/>/<see cref="SingleDumpPipelineKeys.Output"/>
/// keys, which the single-dump node factory also reuses as its one artifact's own state (N=1
/// collapses the "per-artifact state" and "session state" into the same object).
/// </remarks>
internal sealed record ArtifactPipelineKeys(
    string State,
    string LoadContext,
    string HeapIndex,
    string AnalysisContext,
    string Pipeline,
    string AnalyzerRuns)
{
    public static readonly ArtifactPipelineKeys SingleDump = new(
        State: SingleDumpPipelineKeys.State,
        LoadContext: SingleDumpPipelineKeys.LoadContext,
        HeapIndex: SingleDumpPipelineKeys.HeapIndex,
        AnalysisContext: SingleDumpPipelineKeys.AnalysisContext,
        Pipeline: SingleDumpPipelineKeys.Pipeline,
        AnalyzerRuns: SingleDumpPipelineKeys.AnalyzerRuns);

    public static ArtifactPipelineKeys ForArtifact(string artifactId) => new(
        State: $"artifact:{artifactId}:state",
        LoadContext: $"artifact:{artifactId}:loadContext",
        HeapIndex: $"artifact:{artifactId}:heapIndex",
        AnalysisContext: $"artifact:{artifactId}:analysisContext",
        Pipeline: $"artifact:{artifactId}:pipeline",
        AnalyzerRuns: $"artifact:{artifactId}:analyzerRuns");
}
