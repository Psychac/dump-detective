namespace DumpDetective.Cli.Pipeline.Nodes;

/// <summary>
/// Data keys the single-dump node chain declares through <c>Produces</c>/<c>Consumes</c>. All
/// nodes also share the one <see cref="SingleDumpPipelineState"/> instance under
/// <see cref="State"/> — the keys below are hand-declared per
/// docs/refactor/modularity/phase-4-session-orchestration.md step 1, reflecting real data flow
/// even though, for now, they're all satisfied through that one shared state object.
/// </summary>
internal static class SingleDumpPipelineKeys
{
    public const string State = "single-dump.state";
    public const string LoadContext = "dump.loadContext";
    public const string HeapIndex = "dump.heapIndex";
    public const string AnalysisContext = "dump.analysisContext";
    public const string Pipeline = "dump.pipeline";
    public const string AnalyzerRuns = "dump.analyzerRuns";
    public const string ReportDocument = "dump.reportDocument";
    public const string Output = "dump.output";
}
