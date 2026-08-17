using System.Diagnostics;

using DumpDetective.Analysis.Insight;
using DumpDetective.Cli.Console;
using DumpDetective.Core.Models;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Renders single-dump analysis results (insights, memory, diagnostics) to the console.
/// </summary>
internal static class SingleDumpConsolePresenter
{
    public static void PrintInsights(IReadOnlyList<InsightFinding> insights, bool diagnosticMode)
    {
        ConsoleUx.InsightsHeader(insights.Count);
        foreach (InsightFinding f in insights)
            ConsoleUx.InsightLine(f.Severity, f.Title, f.Evidence, diagnosticMode);
    }

    public static void PrintMemorySummary(
        IReadOnlyList<AnalyzerRunResult> runs,
        List<(string StageName, AnalyzerMemoryStats Stats)> stageStats)
    {
        // ── Stage table ──────────────────────────────────────────────────────
        if (stageStats.Count > 0)
        {
            ConsoleUx.MemoryStageTableHeader();
            foreach ((string name, AnalyzerMemoryStats s) in stageStats)
                ConsoleUx.MemoryTableRow(name, s.WorkingSetDelta, s.WorkingSetAfter, s.ManagedHeapDelta);
        }

        // ── Analyzer table ───────────────────────────────────────────────────
        bool printedAnyAnalyzerRow = false;
        long firstRunWorkingSetBefore = 0;

        foreach (AnalyzerRunResult run in runs)
        {
            if (run.MemoryStats is null)
                continue;

            if (!printedAnyAnalyzerRow)
            {
                ConsoleUx.MemoryTableHeader();
                printedAnyAnalyzerRow = true;
                firstRunWorkingSetBefore = run.MemoryStats.WorkingSetBefore;
            }

            AnalyzerMemoryStats s = run.MemoryStats;
            ConsoleUx.MemoryTableRow(run.AnalyzerName, s.WorkingSetDelta, s.WorkingSetAfter, s.ManagedHeapDelta);
        }

        // ── Process peak across all measured scopes ──────────────────────────
        long baseline = stageStats.Count > 0
            ? stageStats[0].Stats.WorkingSetBefore
            : (printedAnyAnalyzerRow ? firstRunWorkingSetBefore : 0);

        // Process.PeakWorkingSet64 is the OS-tracked historical maximum for this process's whole
        // lifetime, continuously updated by the kernel — not just whatever value happened to be
        // current at a stage/analyzer *boundary*. The previous "max of WorkingSetAfter across
        // stage/analyzer checkpoints" approach only ever sampled a handful of points (5 stages,
        // ~30 analyzers), so it silently missed any spike that rose and fell *within* a single
        // analyzer's run — exactly the case observed with DominatorAnalyzer's reverse-index scan,
        // where the true mid-run peak was measurably higher than the value recorded once that
        // analyzer finished.
        Process currentProcess = Process.GetCurrentProcess();
        currentProcess.Refresh();
        long peak = currentProcess.PeakWorkingSet64;

        if (peak > 0)
            ConsoleUx.MemoryTableFooter(peak, baseline);
    }

    public static void PrintDiagnosticsSummary(IReadOnlyList<AnalyzerRunResult> runs)
    {
        if (runs.Count == 0)
            return;

        AnalyzerRunResult[] topSlow = runs
            .OrderByDescending(r => r.Duration)
            .Take(5)
            .ToArray();

        ConsoleUx.TopSlowAnalyzers(topSlow);
    }
}
