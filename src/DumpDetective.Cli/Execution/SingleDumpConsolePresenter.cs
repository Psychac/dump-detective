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
        long peakFromAnalyzers = 0;

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
            if (s.WorkingSetAfter > peakFromAnalyzers) peakFromAnalyzers = s.WorkingSetAfter;
        }

        // ── Process peak across all measured scopes ──────────────────────────
        long baseline = stageStats.Count > 0
            ? stageStats[0].Stats.WorkingSetBefore
            : (printedAnyAnalyzerRow ? firstRunWorkingSetBefore : 0);

        long peakFromStages = stageStats.Count > 0 ? stageStats.Max(s => s.Stats.WorkingSetAfter) : 0;
        long peak = Math.Max(peakFromStages, peakFromAnalyzers);

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
