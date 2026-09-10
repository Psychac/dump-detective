using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using DumpDetective.Cli.Services;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Models;

namespace DumpDetective.Cli.Execution;

internal sealed class AnalyzerExecutionService(FindingGenerationPipeline findingGenerationPipeline)
{
    private readonly FindingGenerationPipeline _findingGenerationPipeline = findingGenerationPipeline;

    public RuntimeAnalysisContext BuildContext(
        ResolvedExecutionOptions resolved,
        DumpLoadContext loadContext,
        IHeapAnalysisCache heapCache,
        IReadOnlyList<IAnalyzer> activeAnalyzers)
    {
        return new RuntimeAnalysisContext
        {
            Runtime = loadContext.Runtime,
            Cache = heapCache,
            RuntimeFacade = new RuntimeFacade(loadContext.Runtime, loadContext.Heap),
            // Every per-analyzer options property on AnalysisOptions keeps its own fixed default —
            // see docs/refactor/analysis-options-removal-plan.md. Nothing external can override
            // them, so there's nothing left to thread through from `resolved` here.
            AnalysisOptions = new AnalysisOptions(),
            Diagnostics = resolved.Diagnostics,
            DiagnosticsSink = new ConsoleDiagnosticsSink(resolved.DiagnosticMode, activeAnalyzers)
        };
    }

    public AnalysisPipeline CreatePipeline(IReadOnlyList<IAnalyzer> activeAnalyzers) =>
        new(activeAnalyzers, _findingGenerationPipeline);

    /// <summary>
    /// Runs the shared heap-index/thread-stack scan passes for <paramref name="pipeline"/>.
    /// Callers that want this work attributed to the indexing phase rather than "running
    /// analyzers" should call this before that phase transition; safe to call again (or not at
    /// all) before <see cref="ExecuteAsync"/> — the pipeline only runs the shared scans once.
    /// </summary>
    public void RunSharedScans(AnalysisPipeline pipeline, RuntimeAnalysisContext context, CancellationToken cancellationToken) =>
        pipeline.RunSharedScans(context, cancellationToken);

    public async Task<IReadOnlyList<AnalyzerRunResult>> ExecuteAsync(
        AnalysisPipeline pipeline,
        RuntimeAnalysisContext context,
        CancellationToken cancellationToken)
    {
        return await pipeline.ExecuteAsync(context, cancellationToken);
    }
}
