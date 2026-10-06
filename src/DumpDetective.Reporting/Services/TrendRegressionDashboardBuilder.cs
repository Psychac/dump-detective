using DumpDetective.Sdk.Synthesis;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Trend;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Builds the T3 Regression Dashboard <see cref="AnalyzerDetailSection"/>: severity escalations and
/// new findings. Both are read directly off <see cref="FindingLifecycleComparer"/>'s output
/// (via <see cref="TrendReportData"/>) rather than recomputed here — this used to independently
/// re-join baseline/current findings by fingerprint just for the Warning→Critical case, duplicating
/// what <c>FindingLifecycleComparer</c> and <c>TrendReportComposer</c>'s own escalation check each
/// did too. One computation, three former call sites.
/// </summary>
internal static class TrendRegressionDashboardBuilder
{
    public static AnalyzerDetailSection Build(
        TrendReportData trendData,
        IReadOnlyList<FindingRecord>? mappedFindings = null)
    {
        IReadOnlyList<FindingSeverityChange> escalations = trendData.SeverityChanges
            .Where(c => c.IsEscalation)
            .ToArray();
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
            foreach (FindingSeverityChange esc in escalations)
            {
                rows.Add(new TableRow(new[]
                {
                    new TableCell(esc.Current.Analyzer),
                    new TableCell(esc.Current.Title),
                    new TableCell(esc.BaselineSeverity.ToString()),
                    new TableCell(esc.Current.Severity.ToString())
                }));
            }
            blocks.Add(new TableBlock(
                Caption: "Findings whose severity increased since the baseline",
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
                Claim f = newFindingsSorted[i];
                string evidence = f.Narrative.Length > 120 ? f.Narrative[..117] + "…" : f.Narrative;
                rows.Add(new TableRow(new[]
                {
                    new TableCell(f.Severity.ToString()),
                    new TableCell(f.Analyzer),
                    new TableCell(f.Category),
                    new TableCell(f.Title),
                    new TableCell(evidence),
                    new TableCell($"{f.ReportConfidence():P0}")
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

        // T3c "New Leak Signals" removed - it duplicated T3a/T3b (a leak-shaped finding that's new
        // or escalated already shows up there, generically, for every analyzer, not just the 3
        // this used to hand-pick). See docs/refactor/modularity/phase-5-observations-synthesis.md
        // ## Work step 4.

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
}
