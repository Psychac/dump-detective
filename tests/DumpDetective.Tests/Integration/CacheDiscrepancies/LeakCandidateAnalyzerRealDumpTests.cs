using DumpDetective.Analysis.Utilities;
using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Gc;
using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Analyzers.Leaks;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for <see cref="LeakCandidateAnalyzer"/>'s Tier-1 retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — the last analyzer in the
/// plan, and the only one whose retyping needed a cross-analyzer-dependency bridge (see
/// <see cref="LeakCandidateAnalyzer"/>'s own remarks). Unlike every other real-dump test in this
/// suite, this one's real point isn't the analyzer's own logic (already covered by the fake-backed
/// characterization suite) — it's proving <see cref="LeakCandidateAnalyzer.BuildSdkContext"/>'s
/// extraction from <c>context.CompletedRunResults</c> actually works end-to-end: real
/// <see cref="GCHandleAnalyzer"/>/<see cref="TimerLeakAnalyzer"/>/
/// <see cref="GCRootAnalyzer"/> results, manually sequenced the same way
/// <c>AnalysisPipeline</c>'s two-phase execution would, then handed to the deferred analyzer.
/// </summary>
public sealed class LeakCandidateAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public LeakCandidateAnalyzerRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task AnalyzeAsync_RealDump_ProducesInternallyConsistentResult()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        // Manually reproduce AnalysisPipeline's two-phase execution: run the three source
        // analyzers first (wave 1), snapshot their results into CompletedRunResults, then run the
        // deferred analyzer (wave 2) -- exactly what LeakCandidateAnalyzer's
        // IDeferredAnalyzer marker asks the real pipeline to do.
        using GCHandleAnalyzer gcHandleAdapter = new();
        await gcHandleAdapter.AnalyzeAsync(context, CancellationToken.None);
        AnalyzerDomainResult gcHandleResult = (AnalyzerDomainResult)gcHandleAdapter.LastResult!;

        using TimerLeakAnalyzer timerLeakAdapter = new();
        new HeapIndexScanDispatcher().Run(cache, contextRuntimeCtx, [timerLeakAdapter], CancellationToken.None);
        await timerLeakAdapter.AnalyzeAsync(context, CancellationToken.None);
        AnalyzerDomainResult timerLeakResult = (AnalyzerDomainResult)timerLeakAdapter.LastResult!;

        using GCRootAnalyzer gcRootAdapter = new();
        await gcRootAdapter.AnalyzeAsync(context, CancellationToken.None);
        AnalyzerDomainResult gcRootResult = (AnalyzerDomainResult)gcRootAdapter.LastResult!;

        contextRuntimeCtx.CompletedRunResults =
        [
            new AnalyzerRunResult(gcHandleAdapter.Name, AnalyzerExecutionStatus.Success, TimeSpan.Zero, gcHandleResult, null, null),
            new AnalyzerRunResult(timerLeakAdapter.Name, AnalyzerExecutionStatus.Success, TimeSpan.Zero, timerLeakResult, null, null),
            new AnalyzerRunResult(gcRootAdapter.Name, AnalyzerExecutionStatus.Success, TimeSpan.Zero, gcRootResult, null, null),
        ];

        using LeakCandidateAnalyzer adapter = new();
        DumpDetective.Sdk.Analysis.AnalysisContext leakCandidateContext = ((ICustomizesAnalysisContext)adapter).BuildAnalysisContext(contextRuntimeCtx);
        await adapter.AnalyzeAsync(leakCandidateContext, CancellationToken.None);
            var result = (LeakCandidateDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Leak Candidate Analysis");
        result.Category.Should().Be("Memory");

        result.TotalCandidates.Should().BeGreaterThan(0, "a real IIS crash dump has many candidate types once a heap index is built");
        result.TopCandidates.Should().HaveCount(result.TotalCandidates, "no Top-N cap on the returned population");

        var assessments = new LeakCandidateAssessment[result.TopCandidates.Count];
        var countByClass = new Dictionary<LeakClass, int>();
        for (int i = 0; i < assessments.Length; i++)
        {
            assessments[i] = LeakCandidateClassifier.Assess(result.TopCandidates[i], result.LogicalTimerCount);
            countByClass[assessments[i].Classification] = countByClass.GetValueOrDefault(assessments[i].Classification) + 1;
        }

        for (int i = 1; i < result.TopCandidates.Count; i++)
        {
            int prevScore = assessments[i - 1].SuspicionScore;
            int score = assessments[i].SuspicionScore;
            (score <= prevScore).Should().BeTrue("candidates are sorted by descending suspicion score");
        }

        int withRootChain = result.TopCandidates.Count(c => c.RootChain is not null);
        _output.WriteLine($"TotalCandidates={result.TotalCandidates:N0} WithRootChain={withRootChain:N0}/{Math.Min(result.TotalCandidates, 20):N0} (top-20 enrichment)");
        foreach (var kv in countByClass)
            _output.WriteLine($"  {kv.Key}: {kv.Value:N0}");
        for (int i = 0; i < Math.Min(10, result.TopCandidates.Count); i++)
        {
            LeakCandidateRecord candidate = result.TopCandidates[i];
            _output.WriteLine(
                $"  {candidate.TypeName}: score={assessments[i].SuspicionScore} class={assessments[i].Classification} " +
                $"severity={assessments[i].Severity} totalSize={candidate.TotalSize:N0} rootChain={candidate.RootChain ?? "n/a"}");
        }
    }
}
