using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds the T3 Regression Dashboard <see cref="AnalyzerDetailSection"/>:
/// severity escalations, new findings, and new leak signals.
/// </summary>
internal static class TrendRegressionDashboardBuilder
{
    private sealed record SeverityEscalationEntry(string Analyzer, string Title, FindingSeverity BaselineSeverity, FindingSeverity CurrentSeverity);

    public static AnalyzerDetailSection Build(
        TrendReportData trendData,
        IReadOnlyList<AnalysisSnapshot> snapshots,
        IReadOnlyList<FindingRecord>? mappedFindings = null)
    {
        var escalations = BuildSeverityEscalations(snapshots);
        var blocks = new List<SectionBlock>();

        blocks.Add(new HeadingBlock("Regression Dashboard"));

        // TV2-4: Show regression classification counts (if mapped findings provided)
        if (mappedFindings is not null)
        {
            int newRisk = mappedFindings.Count(f => string.Equals(f.RegressionClass, nameof(Models.RegressionClass.NewRisk), StringComparison.Ordinal));
            int amplified = mappedFindings.Count(f => string.Equals(f.RegressionClass, nameof(Models.RegressionClass.AmplifiedRisk), StringComparison.Ordinal));
            int volatileCount = mappedFindings.Count(f => string.Equals(f.RegressionClass, nameof(Models.RegressionClass.VolatileRisk), StringComparison.Ordinal));

            blocks.Add(new TextBlock($"Regression classes: NewRisk: {newRisk} • AmplifiedRisk: {amplified} • VolatileRisk: {volatileCount}"));
            blocks.Add(new BlankBlock());
        }

        // ── T3a: Severity Escalations ────────────────────────────────────────
        blocks.Add(new HeadingBlock("Severity Escalations", 1));
        if (escalations.Count > 0)
        {
            var rows = new List<TableRow>(escalations.Count);
            foreach (var esc in escalations)
            {
                rows.Add(new TableRow(new[]
                {
                    new TableCell(esc.Analyzer),
                    new TableCell(esc.Title),
                    new TableCell(esc.BaselineSeverity.ToString()),
                    new TableCell(esc.CurrentSeverity.ToString())
                }));
            }
            blocks.Add(new TableBlock(
                Caption: "Findings that escalated from Warning to Critical",
                Headers: new[] { "Analyzer", "Title", "Baseline", "Current" },
                Rows: rows));
        }
        else
        {
            blocks.Add(new TextBlock("No severity escalations detected."));
        }

        // ── T3b: New Findings ────────────────────────────────────────────────
        blocks.Add(new HeadingBlock("New Findings", 1));
        if (trendData.NewFindings.Count > 0)
        {
            int capped = Math.Min(trendData.NewFindings.Count, 20);
            var newFindingsSorted = trendData.NewFindings
                .OrderByDescending(f => f.Severity)
                .ToArray();

            var rows = new List<TableRow>(capped);
            for (int i = 0; i < capped; i++)
            {
                InsightFinding f = newFindingsSorted[i];
                string evidence = f.Evidence.Length > 120 ? f.Evidence[..117] + "…" : f.Evidence;
                rows.Add(new TableRow(new[]
                {
                    new TableCell(f.Severity.ToString()),
                    new TableCell(f.Analyzer),
                    new TableCell(f.Category),
                    new TableCell(f.Title),
                    new TableCell(evidence),
                    new TableCell($"{f.ConfidenceScore:P0}")
                }));
            }
            blocks.Add(new TableBlock(
                Caption: "Findings present in current dump but absent in baseline",
                Headers: new[] { "Severity", "Analyzer", "Category", "Title", "Evidence", "Confidence" },
                Rows: rows));

            if (trendData.NewFindings.Count > 20)
                blocks.Add(new TextBlock($"{trendData.NewFindings.Count - 20} additional new findings not shown."));
        }
        else
        {
            blocks.Add(new TextBlock("No new findings detected."));
        }

        // ── T3c: New Leak Signals ────────────────────────────────────────────
        blocks.Add(new HeadingBlock("New Leak Signals", 1));
        var allLeakSignals = trendData.NewLeakSignalsByAnalyzer
            .SelectMany(kvp => kvp.Value.Select(s => (AnalyzerName: kvp.Key, Signal: s)))
            .OrderByDescending(x => x.Signal.CurrentBytes)
            .Take(10)
            .ToArray();

        if (allLeakSignals.Length > 0)
        {
            var rows = new List<TableRow>(allLeakSignals.Length);
            foreach (var (analyzerName, signal) in allLeakSignals)
            {
                double growthPct = signal.BaselineBytes > 0
                    ? (signal.CurrentBytes - signal.BaselineBytes) / signal.BaselineBytes * 100.0
                    : double.NaN;
                string growthStr = double.IsNaN(growthPct) ? "N/A" : $"{growthPct:+0.0;-0.0}%";

                rows.Add(new TableRow(new[]
                {
                    new TableCell(signal.TypeName),
                    new TableCell(analyzerName),
                    new TableCell(FormatHelper.FormatMetricValue(signal.BaselineBytes, "bytes")),
                    new TableCell(FormatHelper.FormatMetricValue(signal.CurrentBytes, "bytes")),
                    new TableCell(growthStr)
                }));
            }
            blocks.Add(new TableBlock(
                Caption: "Types newly appearing or significantly growing in leak candidates",
                Headers: new[] { "TypeName", "Source Analyzer", "Baseline", "Current", "Growth%" },
                Rows: rows));
        }
        else
        {
            blocks.Add(new TextBlock("No new leak signals detected."));
        }

        // T3b Correlation Timeline: replaced by client-side compact timeline renderer
        // Correlation events are emitted in the trend JSON by TrendReportComposer
        // and rendered into T3 by the client timeline lane. Server-side table removed to avoid duplication.

        return new AnalyzerDetailSection(
            AnalyzerName:  "TrendRegressionDashboard",
            DisplayTitle:  "Regression Dashboard",
            SortOrder:     30,
            Blocks:        blocks,
            SectionId:     "T3",
            Domain:        "Trend");
    }

    private static IReadOnlyList<SeverityEscalationEntry> BuildSeverityEscalations(IReadOnlyList<AnalysisSnapshot> snapshots)
    {
        if (snapshots.Count < 2)
            return [];

        var baselineByFingerprint = snapshots[0].Findings
            .GroupBy(f => f.EffectiveFingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var escalations = new List<SeverityEscalationEntry>();
        foreach (InsightFinding current in snapshots[^1].Findings)
        {
            if (!baselineByFingerprint.TryGetValue(current.EffectiveFingerprint, out InsightFinding? baseline))
                continue;

            if (baseline.Severity == FindingSeverity.Warning && current.Severity == FindingSeverity.Critical)
                escalations.Add(new SeverityEscalationEntry(current.Analyzer, current.Title, baseline.Severity, current.Severity));
        }

        return escalations;
    }
}
