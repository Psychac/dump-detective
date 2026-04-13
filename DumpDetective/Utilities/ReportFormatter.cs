using DumpDetective.Configuration;
using DumpDetective.Models;
using System.Net;
using System.Text;

namespace DumpDetective.Utilities
{
    internal static class ReportFormatter
    {
        public static string Format(ReportFormat format, string detailedReport, IReadOnlyList<string> insights, string dumpPath, IReadOnlyList<InsightFinding> findings)
        {
            return format switch
            {
                ReportFormat.Markdown => ToMarkdown(detailedReport, insights, dumpPath, findings),
                ReportFormat.Html => ToHtml(detailedReport, insights, dumpPath, findings),
                _ => ToText(detailedReport, insights, dumpPath, findings)
            };
        }

        // ── Text ─────────────────────────────────────────────────────────────

        private static string ToText(string detailedReport, IReadOnlyList<string> insights, string dumpPath, IReadOnlyList<InsightFinding> findings)
        {
            var b = new StringBuilder();
            b.AppendLine("DumpDetective Analysis Report");
            b.AppendLine(new string('=', 80));
            b.AppendLine($"Dump File: {dumpPath}");
            b.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            b.AppendLine();
            AppendTextFindingsSummary(b, findings);
            b.AppendLine();
            b.AppendLine("INSIGHTS");
            b.AppendLine(new string('-', 80));
            foreach (string insight in insights)
                b.AppendLine($"  {insight}");
            b.AppendLine();
            b.AppendLine("DETAILED ANALYSIS");
            b.AppendLine(new string('-', 80));
            AppendTextSections(b, detailedReport);
            return b.ToString();
        }

        private static void AppendTextFindingsSummary(StringBuilder b, IReadOnlyList<InsightFinding> findings)
        {
            b.AppendLine("FINDINGS SUMMARY");
            b.AppendLine(new string('-', 80));
            if (findings.Count == 0)
            {
                b.AppendLine("No structured findings emitted by analyzers.");
                return;
            }
            b.AppendLine($"Critical: {findings.Count(f => f.Severity == FindingSeverity.Critical):N0}  Warning: {findings.Count(f => f.Severity == FindingSeverity.Warning):N0}  Info: {findings.Count(f => f.Severity == FindingSeverity.Info):N0}");
            b.AppendLine();
            foreach (var f in findings.Take(8))
            {
                b.AppendLine($"[{f.Severity.ToString().ToUpperInvariant()}] {f.Title}");
                b.AppendLine($"  Evidence:       {f.Evidence}");
                b.AppendLine($"  Recommendation: {f.Recommendation}");
                b.AppendLine();
            }
        }

        // ── Markdown ──────────────────────────────────────────────────────────

        private static string ToMarkdown(string detailedReport, IReadOnlyList<string> insights, string dumpPath, IReadOnlyList<InsightFinding> findings)
        {
            var b = new StringBuilder();
            b.AppendLine("# 🕵️ DumpDetective Analysis Report");
            b.AppendLine();
            b.AppendLine($"> 📁 **Dump:** `{dumpPath}`  ");
            b.AppendLine($"> 🕐 **Generated (UTC):** `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}`");
            b.AppendLine();
            AppendMarkdownFindingsSummary(b, findings);
            b.AppendLine();
            AppendMarkdownInsights(b, insights);
            b.AppendLine();

            var parsed = ParseDetailedReport(detailedReport);

            if (parsed.TrendSection != null)
            {
                AppendMarkdownTrendSection(b, parsed.TrendSection);
                b.AppendLine();
            }

            b.AppendLine("## 📊 Detailed Analysis");
            b.AppendLine();

            foreach (var block in parsed.Blocks)
            {
                if (block.Label != null)
                {
                    b.AppendLine("---");
                    b.AppendLine();
                    bool isLast = block == parsed.Blocks[^1];
                    b.AppendLine(isLast
                        ? $"### 📸 Snapshot {block.Label} *(current)*"
                        : $"### 📸 Snapshot {block.Label}");
                    b.AppendLine();
                }

                string groupHd = block.Label != null ? "####" : "###";
                foreach (var (groupName, groupIcon, groupSections) in GroupSections(block.Sections))
                {
                    b.AppendLine($"{groupHd} {groupIcon} {groupName}");
                    b.AppendLine();
                    foreach (var section in groupSections)
                    {
                        string icon = SectionIcon(section.Title);
                        b.AppendLine("<details>");
                        b.AppendLine($"<summary>{icon} <strong>{WebUtility.HtmlEncode(section.Title)}</strong></summary>");
                        b.AppendLine();
                        b.AppendLine("```text");
                        foreach (string line in section.Lines)
                            b.AppendLine(line);
                        b.AppendLine("```");
                        b.AppendLine("</details>");
                        b.AppendLine();
                    }
                }
            }

            return b.ToString();
        }

        private static void AppendMarkdownFindingsSummary(StringBuilder b, IReadOnlyList<InsightFinding> findings)
        {
            b.AppendLine("## 🚨 Findings Summary");
            b.AppendLine();
            if (findings.Count == 0)
            {
                b.AppendLine("> ℹ️ No structured findings emitted by analyzers.");
                return;
            }

            int critCount = findings.Count(f => f.Severity == FindingSeverity.Critical);
            int warnCount = findings.Count(f => f.Severity == FindingSeverity.Warning);
            int infoCount = findings.Count(f => f.Severity == FindingSeverity.Info);
            b.AppendLine($"🔴 **Critical: {critCount}** &nbsp;·&nbsp; 🟠 **Warning: {warnCount}** &nbsp;·&nbsp; 🔵 **Info: {infoCount}**");
            b.AppendLine();
            b.AppendLine("| &nbsp; | Severity | Analyzer | Title | Evidence | Recommendation |");
            b.AppendLine("|:---:|---|---|---|---|---|");

            var ordered = findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Analyzer);
            const int MdCap = 25;
            foreach (var f in ordered.Take(MdCap))
            {
                string icon = SeverityIcon(f.Severity);
                b.AppendLine($"| {icon} | {f.Severity} | {EscapePipe(f.Analyzer)} | {EscapePipe(f.Title)} | {EscapePipe(f.Evidence)} | {EscapePipe(f.Recommendation)} |");
            }
            if (findings.Count > MdCap)
                b.AppendLine($"> *…and {findings.Count - MdCap} more findings not shown.*");
        }

        private static void AppendMarkdownInsights(StringBuilder b, IReadOnlyList<string> insights)
        {
            b.AppendLine("## 🔍 Insights");
            b.AppendLine();
            foreach (string insight in insights)
                b.AppendLine($"- {InsightMarkdownIcon(insight)} {insight}");
        }

        private static void AppendMarkdownTrendSection(StringBuilder b, ReportSection trend)
        {
            b.AppendLine("## 📈 Trend Comparison");
            b.AppendLine();
            var tc = ParseTrendContent(trend);

            if (tc.SummaryKV.Count > 0)
            {
                b.AppendLine("| Metric | Value |");
                b.AppendLine("|---|:---:|");
                foreach (var (k, v) in tc.SummaryKV)
                    b.AppendLine($"| {k} | **{v}** |");
                b.AppendLine();
            }

            if (tc.TimelineGroups.Count > 0)
            {
                b.AppendLine("### 📊 Metric Timeline");
                b.AppendLine();
                foreach (var (analyzer, metrics) in tc.TimelineGroups.Where(g => g.Metrics.Count > 0))
                {
                    b.AppendLine($"**{analyzer}**");
                    b.AppendLine();
                    foreach (string m in metrics)
                        b.AppendLine($"- {m}");
                    b.AppendLine();
                }
            }

            if (tc.NewFindings.Count > 0)
            {
                b.AppendLine("### 🔺 New Findings");
                b.AppendLine();
                foreach (string f in tc.NewFindings) b.AppendLine($"- {f}");
                b.AppendLine();
            }

            if (tc.ResolvedFindings.Count > 0)
            {
                b.AppendLine("### ✅ Resolved Findings");
                b.AppendLine();
                foreach (string f in tc.ResolvedFindings) b.AppendLine($"- {f}");
                b.AppendLine();
            }
        }

        private static void AppendHtmlTrendSection(StringBuilder b, ReportSection trend)
        {
            var tc = ParseTrendContent(trend);
            b.AppendLine("<section class=\"trend-section\">");
            b.AppendLine("<h2>📈 Trend Comparison</h2>");

            if (tc.SummaryKV.Count > 0)
            {
                b.AppendLine("<div class=\"trend-stats\">");
                foreach (var (k, v) in tc.SummaryKV)
                {
                    b.AppendLine("  <div class=\"trend-stat\">");
                    b.AppendLine($"    <div class=\"trend-stat-lbl\">{HtmlEnc(k)}</div>");
                    b.AppendLine($"    <div class=\"trend-stat-val\">{HtmlEnc(v)}</div>");
                    b.AppendLine("  </div>");
                }
                b.AppendLine("</div>");
            }

            if (tc.TimelineGroups.Count > 0)
            {
                b.AppendLine("<h3 class=\"sub-h3\">📊 Metric Timeline</h3>");
                b.AppendLine("<div class=\"timeline\">");
                foreach (var (analyzer, metrics) in tc.TimelineGroups.Where(g => g.Metrics.Count > 0))
                {
                    b.AppendLine("  <div class=\"timeline-group\">");
                    b.AppendLine($"    <div class=\"timeline-group-name\">{HtmlEnc(analyzer)}</div>");
                    b.AppendLine("    <ul class=\"timeline-metrics\">");
                    foreach (string m in metrics)
                        b.AppendLine($"      <li>{HtmlEnc(m)}</li>");
                    b.AppendLine("    </ul>");
                    b.AppendLine("  </div>");
                }
                b.AppendLine("</div>");
            }

            if (tc.NewFindings.Count > 0)
            {
                b.AppendLine("<h3 class=\"sub-h3\">🔺 New Findings</h3>");
                b.AppendLine("<ul class=\"trend-list\">");
                foreach (string f in tc.NewFindings)
                    b.AppendLine($"  <li class=\"trend-list-new\">{HtmlEnc(f)}</li>");
                b.AppendLine("</ul>");
            }

            if (tc.ResolvedFindings.Count > 0)
            {
                b.AppendLine("<h3 class=\"sub-h3\">✅ Resolved Findings</h3>");
                b.AppendLine("<ul class=\"trend-list\">");
                foreach (string f in tc.ResolvedFindings)
                    b.AppendLine($"  <li class=\"trend-list-resolved\">{HtmlEnc(f)}</li>");
                b.AppendLine("</ul>");
            }

            b.AppendLine("</section>");
        }

        private static TrendContent ParseTrendContent(ReportSection trend)
        {
            var summaryKV = new List<(string K, string V)>();
            var timelineGroups = new List<(string Analyzer, List<string> Metrics)>();
            var newFindings = new List<string>();
            var resolvedFindings = new List<string>();
            string? currentAnalyzer = null;
            var currentMetrics = new List<string>();
            string state = "summary";

            foreach (string raw in trend.Lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                if (raw.StartsWith("PER-ANALYZER METRIC TIMELINE", StringComparison.Ordinal))
                { state = "timeline"; continue; }

                if (raw.TrimStart().StartsWith("New findings:", StringComparison.Ordinal))
                {
                    if (currentAnalyzer != null) { timelineGroups.Add((currentAnalyzer, currentMetrics)); currentAnalyzer = null; currentMetrics = []; }
                    state = "new"; continue;
                }

                if (raw.TrimStart().StartsWith("Resolved findings:", StringComparison.Ordinal))
                { state = "resolved"; continue; }

                switch (state)
                {
                    case "summary":
                        if (!raw.StartsWith(" ", StringComparison.Ordinal))
                        {
                            int ci = raw.IndexOf(':');
                            if (ci > 0) summaryKV.Add((raw[..ci].Trim(), raw[(ci + 1)..].Trim()));
                        }
                        break;

                    case "timeline":
                        if (raw.StartsWith("  [", StringComparison.Ordinal) && raw.TrimEnd().EndsWith("]"))
                        {
                            if (currentAnalyzer != null) timelineGroups.Add((currentAnalyzer, currentMetrics));
                            currentAnalyzer = raw.Trim()[1..^1];
                            currentMetrics = [];
                        }
                        else if (raw.StartsWith("    ", StringComparison.Ordinal) && currentAnalyzer != null)
                        {
                            currentMetrics.Add(raw.Trim());
                        }
                        break;

                    case "new":
                        if (raw.TrimStart().StartsWith("-", StringComparison.Ordinal))
                            newFindings.Add(raw.TrimStart()[1..].Trim());
                        break;

                    case "resolved":
                        if (raw.TrimStart().StartsWith("-", StringComparison.Ordinal))
                            resolvedFindings.Add(raw.TrimStart()[1..].Trim());
                        break;
                }
            }
            if (currentAnalyzer != null) timelineGroups.Add((currentAnalyzer, currentMetrics));

            return new TrendContent(summaryKV, timelineGroups, newFindings, resolvedFindings);
        }

        // ── HTML ──────────────────────────────────────────────────────────────

        private static string ToHtml(string detailedReport, IReadOnlyList<string> insights, string dumpPath, IReadOnlyList<InsightFinding> findings)
        {
            var parsed = ParseDetailedReport(detailedReport);
            var b = new StringBuilder(capacity: 128 * 1024);

            int critCount = findings.Count(f => f.Severity == FindingSeverity.Critical);
            int warnCount = findings.Count(f => f.Severity == FindingSeverity.Warning);
            int infoCount = findings.Count(f => f.Severity == FindingSeverity.Info);

            b.AppendLine(HtmlHead());
            b.AppendLine("<body>");

            // ── Top bar ──────────────────────────────────────────────────
            b.AppendLine("""
                <header class="topbar">
                  <span class="topbar-logo">🕵️ DumpDetective</span>
                  <span class="topbar-sub">Memory Dump Analysis Report</span>
                </header>
                """);

            b.AppendLine("<main class=\"page\">");

            // ── Meta card ────────────────────────────────────────────────
            b.AppendLine($$"""
                <div class="meta-card">
                  <div class="meta-row"><span class="meta-key">📁 Dump File</span><code class="meta-val">{{HtmlEnc(dumpPath)}}</code></div>
                  <div class="meta-row"><span class="meta-key">🕐 Generated (UTC)</span><code class="meta-val">{{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}}</code></div>
                </div>
                """);

            // ── Severity bar ─────────────────────────────────────────────
            b.AppendLine($$"""
                <div class="sev-bar">
                  <div class="sev-pill sev-crit">🔴 <strong>{{critCount}}</strong> Critical</div>
                  <div class="sev-pill sev-warn">🟠 <strong>{{warnCount}}</strong> Warning</div>
                  <div class="sev-pill sev-info">🔵 <strong>{{infoCount}}</strong> Info</div>
                </div>
                """);

            // ── Insights ─────────────────────────────────────────────────
            b.AppendLine("<section>");
            b.AppendLine("<h2>🔍 Insights</h2>");
            b.AppendLine("<ul class=\"insights\">");
            foreach (string insight in insights)
            {
                string cls = InsightHtmlClass(insight);
                string icon = InsightMarkdownIcon(insight);
                b.AppendLine($"  <li class=\"insight {cls}\">{icon} {HtmlEnc(insight)}</li>");
            }
            b.AppendLine("</ul>");
            b.AppendLine("</section>");

            // ── Findings ─────────────────────────────────────────────────
            b.AppendLine("<section>");
            b.AppendLine("<h2>🚨 Findings</h2>");
            if (findings.Count == 0)
            {
                b.AppendLine("<p class=\"empty\">No structured findings emitted by analyzers.</p>");
            }
            else
            {
                b.AppendLine("<div class=\"table-wrap\">");
                b.AppendLine("<table class=\"findings-table\">");
                b.AppendLine("  <thead><tr><th></th><th>Severity</th><th>Analyzer</th><th>Title</th><th>Evidence</th><th>Recommendation</th><th>Tags</th></tr></thead>");
                b.AppendLine("  <tbody>");
                foreach (var f in findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Analyzer))
                {
                    string rowCls = f.Severity switch
                    {
                        FindingSeverity.Critical => "row-crit",
                        FindingSeverity.Warning  => "row-warn",
                        _                        => "row-info"
                    };
                    string badge = f.Severity switch
                    {
                        FindingSeverity.Critical => "<span class=\"badge badge-crit\">Critical</span>",
                        FindingSeverity.Warning  => "<span class=\"badge badge-warn\">Warning</span>",
                        _                        => "<span class=\"badge badge-info\">Info</span>"
                    };
                    string tags = string.Join(" ", f.Tags.Select(t => $"<span class=\"tag\">{HtmlEnc(t)}</span>"));
                    b.AppendLine($"  <tr class=\"{rowCls}\">");
                    b.AppendLine($"    <td class=\"icon-cell\">{SeverityIcon(f.Severity)}</td>");
                    b.AppendLine($"    <td>{badge}</td>");
                    b.AppendLine($"    <td class=\"dim\">{HtmlEnc(f.Analyzer)}</td>");
                    b.AppendLine($"    <td class=\"bold\">{HtmlEnc(f.Title)}</td>");
                    b.AppendLine($"    <td>{HtmlEnc(f.Evidence)}</td>");
                    b.AppendLine($"    <td>{HtmlEnc(f.Recommendation)}</td>");
                    b.AppendLine($"    <td class=\"tags-cell\">{tags}</td>");
                    b.AppendLine("  </tr>");
                }
                b.AppendLine("  </tbody>");
                b.AppendLine("</table>");
                b.AppendLine("</div>");
            }
            b.AppendLine("</section>");

            // ── Trend comparison ──────────────────────────────────────────────────────
            if (parsed.TrendSection != null)
                AppendHtmlTrendSection(b, parsed.TrendSection);

            // ── Detailed analysis ─────────────────────────────────────────
            b.AppendLine("<section>");
            b.AppendLine("<h2>📊 Detailed Analysis</h2>");

            foreach (var block in parsed.Blocks)
            {
                if (block.Label != null)
                {
                    bool isLast = block == parsed.Blocks[^1];
                    b.AppendLine("<div class=\"snapshot-hdr\">");
                    b.AppendLine($"  <span class=\"snapshot-hdr-title\">📸 Snapshot {HtmlEnc(block.Label)}</span>");
                    if (isLast)
                        b.AppendLine("  <span class=\"snapshot-current-badge\">current</span>");
                    b.AppendLine("</div>");
                }

                foreach (var (groupName, groupIcon, groupSections) in GroupSections(block.Sections))
                {
                    b.AppendLine("<div class=\"analysis-group\">");
                    b.AppendLine($"  <div class=\"group-hdr\">{groupIcon} {HtmlEnc(groupName)}</div>");
                    foreach (var section in groupSections)
                    {
                        string icon = SectionIcon(section.Title);
                        b.AppendLine("  <details class=\"section\">");
                        b.AppendLine($"    <summary>{icon} {HtmlEnc(section.Title)}</summary>");
                        b.AppendLine("    <div class=\"section-body\"><pre>");
                        foreach (var line in section.Lines)
                            b.AppendLine(HtmlEnc(line));
                        b.AppendLine("    </pre></div>");
                        b.AppendLine("  </details>");
                    }
                    b.AppendLine("</div>");
                }
            }

            b.AppendLine("</section>");

            b.AppendLine("</main>");
            b.AppendLine("</body>");
            b.AppendLine("</html>");
            return b.ToString();
        }

        private static string HtmlHead() => """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>DumpDetective Analysis Report</title>
              <style>
                :root {
                  --crit-fg:#cf222e; --crit-bg:#fff0f0; --crit-bd:#f5c6c8;
                  --warn-fg:#9a6700; --warn-bg:#fffbe0; --warn-bd:#e8d49e;
                  --info-fg:#0550ae; --info-bg:#f0f6ff; --info-bd:#c8daff;
                  --bd:#d0d7de; --subtle:#f6f8fa; --text:#1f2328;
                  --code-bg:#0d1117; --code-fg:#c9d1d9;
                  --radius:8px;
                }
                *, *::before, *::after { box-sizing: border-box; }
                body { margin:0; font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Arial,sans-serif; color:var(--text); background:#fff; font-size:14px; line-height:1.6; }

                /* Top bar */
                .topbar { background:#161b22; color:#f0f6ff; padding:14px 28px; display:flex; align-items:baseline; gap:14px; }
                .topbar-logo { font-size:1.2rem; font-weight:700; letter-spacing:.02em; }
                .topbar-sub { font-size:.82rem; color:#8b949e; }

                /* Layout */
                .page { max-width:1380px; margin:0 auto; padding:24px 28px; }
                section { margin-bottom:32px; }
                h2 { font-size:1rem; font-weight:700; margin:0 0 12px 0; padding-bottom:6px; border-bottom:1px solid var(--bd); display:flex; align-items:center; gap:6px; }

                /* Meta card */
                .meta-card { background:var(--subtle); border:1px solid var(--bd); border-radius:var(--radius); padding:14px 18px; margin-bottom:20px; display:flex; flex-wrap:wrap; gap:16px; }
                .meta-row { display:flex; align-items:center; gap:8px; font-size:.88rem; }
                .meta-key { color:#57606a; }
                .meta-val { background:#fff; border:1px solid var(--bd); border-radius:4px; padding:2px 8px; font-size:.82rem; word-break:break-all; }

                /* Severity bar */
                .sev-bar { display:flex; gap:10px; flex-wrap:wrap; margin-bottom:28px; }
                .sev-pill { display:flex; align-items:center; gap:6px; padding:8px 18px; border-radius:var(--radius); font-size:.88rem; border:1px solid; }
                .sev-crit { background:var(--crit-bg); border-color:var(--crit-bd); color:var(--crit-fg); }
                .sev-warn { background:var(--warn-bg); border-color:var(--warn-bd); color:var(--warn-fg); }
                .sev-info { background:var(--info-bg); border-color:var(--info-bd); color:var(--info-fg); }

                /* Insights */
                .insights { list-style:none; padding:0; margin:0; display:flex; flex-direction:column; gap:4px; }
                .insight { padding:8px 14px; border-radius:6px; border-left:3px solid; font-size:.88rem; }
                .ins-crit { background:var(--crit-bg); border-color:var(--crit-fg); }
                .ins-warn { background:var(--warn-bg); border-color:var(--warn-fg); }
                .ins-info { background:var(--info-bg); border-color:var(--info-fg); }

                /* Findings table */
                .table-wrap { overflow-x:auto; border:1px solid var(--bd); border-radius:var(--radius); }
                .findings-table { border-collapse:collapse; width:100%; font-size:.83rem; }
                .findings-table thead tr { background:var(--subtle); }
                .findings-table th, .findings-table td { border-bottom:1px solid var(--bd); padding:7px 10px; vertical-align:top; }
                .findings-table th { font-weight:600; white-space:nowrap; text-align:left; }
                .findings-table tbody tr:last-child td { border-bottom:none; }
                .row-crit { background:var(--crit-bg); }
                .row-warn { background:var(--warn-bg); }
                .row-info { background:var(--info-bg); }
                .icon-cell { text-align:center; width:30px; }
                .dim { color:#57606a; white-space:nowrap; }
                .bold { font-weight:500; }
                .tags-cell { white-space:nowrap; }

                /* Badges */
                .badge { display:inline-block; padding:2px 9px; border-radius:20px; font-size:.7rem; font-weight:700; text-transform:uppercase; letter-spacing:.04em; white-space:nowrap; }
                .badge-crit { background:var(--crit-fg); color:#fff; }
                .badge-warn { background:var(--warn-fg); color:#fff; }
                .badge-info { background:var(--info-fg); color:#fff; }

                /* Tag chips */
                .tag { display:inline-block; background:var(--subtle); border:1px solid var(--bd); border-radius:20px; padding:1px 7px; font-size:.7rem; margin:1px; color:#57606a; }

                /* Detail sections */
                .section { margin:4px 0; border:1px solid var(--bd); border-radius:var(--radius); overflow:hidden; }
                .section summary { cursor:pointer; padding:10px 16px; font-weight:600; font-size:.88rem; background:var(--subtle); user-select:none; display:flex; align-items:center; gap:6px; }
                .section summary:hover { background:#eaecef; }
                .section-body { padding:0; }
                .section-body pre { margin:0; background:var(--code-bg); color:var(--code-fg); padding:16px; overflow-x:auto; font-family:"Cascadia Code",Consolas,"Courier New",monospace; font-size:.79rem; line-height:1.55; }

                /* Sub-headings inside sections */
                .sub-h3 { font-size:.8rem; font-weight:700; text-transform:uppercase; letter-spacing:.05em; color:#57606a; margin:16px 0 8px 0; }

                /* Trend section */
                .trend-section { margin-bottom:32px; }
                .trend-stats { display:flex; gap:10px; flex-wrap:wrap; margin-bottom:20px; }
                .trend-stat { background:var(--subtle); border:1px solid var(--bd); border-radius:var(--radius); padding:10px 18px; min-width:130px; }
                .trend-stat-lbl { font-size:.72rem; color:#57606a; text-transform:uppercase; letter-spacing:.04em; margin-bottom:3px; }
                .trend-stat-val { font-size:1.2rem; font-weight:700; }

                /* Metric timeline */
                .timeline { display:flex; flex-direction:column; gap:8px; margin-bottom:20px; }
                .timeline-group { border:1px solid var(--bd); border-radius:var(--radius); overflow:hidden; }
                .timeline-group-name { padding:8px 14px; font-weight:600; font-size:.85rem; background:var(--subtle); }
                .timeline-metrics { list-style:none; padding:6px 14px 8px 14px; margin:0; display:flex; flex-direction:column; gap:2px; }
                .timeline-metrics li { font-size:.82rem; font-family:"Cascadia Code",Consolas,monospace; padding:2px 0; }

                /* Trend finding lists */
                .trend-list { list-style:none; padding:0; margin:4px 0 16px 0; display:flex; flex-direction:column; gap:3px; }
                .trend-list li { padding:6px 12px; border-radius:6px; border-left:3px solid; font-size:.85rem; }
                .trend-list-new      { background:var(--warn-bg); border-color:var(--warn-fg); }
                .trend-list-resolved { background:var(--info-bg); border-color:var(--info-fg); }

                /* Snapshot header (multi-dump) */
                .snapshot-hdr { display:flex; align-items:center; gap:10px; margin:28px 0 12px 0; padding-bottom:8px; border-bottom:2px solid var(--bd); }
                .snapshot-hdr-title { font-size:.95rem; font-weight:700; }
                .snapshot-current-badge { font-size:.68rem; font-weight:700; text-transform:uppercase; letter-spacing:.05em; background:var(--info-fg); color:#fff; padding:2px 8px; border-radius:20px; }

                /* Analysis groups */
                .analysis-group { margin-bottom:16px; }
                .group-hdr { font-size:.75rem; font-weight:700; text-transform:uppercase; letter-spacing:.07em; color:#57606a; margin:20px 0 6px 0; padding-bottom:4px; border-bottom:1px dashed var(--bd); display:flex; align-items:center; gap:6px; }

                .empty { color:#57606a; font-style:italic; }
              </style>
            </head>
            """;

        // ── Shared helpers ─────────────────────────────────────────────────────

        private static string SeverityIcon(FindingSeverity severity) => severity switch
        {
            FindingSeverity.Critical => "🔴",
            FindingSeverity.Warning  => "🟠",
            _                        => "🔵"
        };

        private static string InsightMarkdownIcon(string insight)
        {
            if (insight.StartsWith("[CRITICAL]", StringComparison.OrdinalIgnoreCase)) return "🔴";
            if (insight.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase))  return "🟠";
            return "🔵";
        }

        private static string InsightHtmlClass(string insight)
        {
            if (insight.StartsWith("[CRITICAL]", StringComparison.OrdinalIgnoreCase)) return "ins-crit";
            if (insight.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase))  return "ins-warn";
            return "ins-info";
        }

        private static string HtmlEnc(string s) => WebUtility.HtmlEncode(s);

        private static string EscapePipe(string value) =>
            value.Replace("|", "\\|", StringComparison.Ordinal);

        private static void AppendTextSections(StringBuilder b, string detailedReport)
        {
            foreach (var section in ParseSections(detailedReport))
            {
                b.AppendLine(section.Title);
                b.AppendLine(new string('-', 80));
                foreach (var line in section.Lines)
                    b.AppendLine(line);
                b.AppendLine();
            }
        }

        private static List<ReportSection> ParseSections(string detailedReport) =>
            ParseSectionsFromLines(detailedReport.Replace("\r\n", "\n").Split('\n'));

        private static List<ReportSection> ParseSectionsFromLines(IEnumerable<string> rawLines)
        {
            var sections = new List<ReportSection>();
            string currentTitle = "General";
            var currentLines = new List<string>();

            foreach (string raw in rawLines)
            {
                string line = raw.TrimEnd();
                if (IsSeparatorLine(line)) continue;

                if (IsSectionHeader(line))
                {
                    AddSectionIfNotEmpty(sections, currentTitle, currentLines);
                    currentTitle = line.TrimEnd(':').Trim();
                    currentLines = [];
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line) && currentLines.Count == 0) continue;
                currentLines.Add(line);
            }

            AddSectionIfNotEmpty(sections, currentTitle, currentLines);
            return sections;
        }

        private static ParsedReport ParseDetailedReport(string rawReport)
        {
            string[] rawLines = rawReport.Replace("\r\n", "\n").Split('\n');

            // Split into blocks by "ANALYSIS SNAPSHOT N/M: path" boundaries
            var rawBlocks = new List<(string? Label, List<string> Lines)>();
            string? currentLabel = null;
            var buffer = new List<string>();

            foreach (string raw in rawLines)
            {
                if (IsSnapshotHeader(raw.TrimEnd()))
                {
                    rawBlocks.Add((currentLabel, buffer));
                    currentLabel = ExtractSnapshotLabel(raw.TrimEnd());
                    buffer = [];
                }
                else
                {
                    buffer.Add(raw);
                }
            }
            rawBlocks.Add((currentLabel, buffer));

            ReportSection? trendSection = null;
            var dumpBlocks = new List<ParsedDumpBlock>();

            foreach (var (label, lines) in rawBlocks)
            {
                var sections = ParseSectionsFromLines(lines);

                // Extract trend section from the first unlabeled block
                if (label == null && trendSection == null)
                {
                    int idx = sections.FindIndex(s =>
                        s.Title.Equals("TREND COMPARISON", StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) { trendSection = sections[idx]; sections.RemoveAt(idx); }
                }

                // Skip the "General" section — dump path is already shown in the report meta card
                sections.RemoveAll(s => s.Title.Equals("General", StringComparison.Ordinal));

                if (sections.Count > 0)
                    dumpBlocks.Add(new ParsedDumpBlock(label, sections));
            }

            if (dumpBlocks.Count == 0)
                dumpBlocks.Add(new ParsedDumpBlock(null, []));

            return new ParsedReport(trendSection, dumpBlocks);
        }

        private static bool IsSnapshotHeader(string line) =>
            line.StartsWith("ANALYSIS SNAPSHOT ", StringComparison.Ordinal);

        private static string ExtractSnapshotLabel(string line)
        {
            const int prefixLen = 18; // "ANALYSIS SNAPSHOT ".Length
            int colonIdx = line.IndexOf(':', prefixLen);
            if (colonIdx < 0) return line[prefixLen..].Trim();
            string progress = line[prefixLen..colonIdx].Trim();
            string path = line[(colonIdx + 1)..].Trim();
            return $"{progress} — {Path.GetFileName(path)}";
        }

        private static string SectionIcon(string title)
        {
            string u = title.ToUpperInvariant();
            if (u.Contains("MEMORY LEAK"))      return "💧";
            if (u.Contains("MEMORY"))           return "🧠";
            if (u.Contains("GC GENERATION"))    return "♻️";
            if (u.Contains("GC HANDLE"))        return "🔗";
            if (u.Contains("CRASH"))            return "💥";
            if (u.Contains("HANG"))             return "⏸️";
            if (u.Contains("COLLECTION"))       return "📦";
            if (u.Contains("THREAD STACK"))     return "📚";
            if (u.Contains("THREAD"))           return "🧵";
            if (u.Contains("EVENT"))            return "📡";
            if (u.Contains("LOH"))              return "🧩";
            if (u.Contains("DEPENDENT HANDLE")) return "⛓️";
            if (u.Contains("STATIC ROOT"))      return "🌱";
            if (u.Contains("REFERENCE CHAIN"))  return "🔍";
            if (u.Contains("MODULE"))           return "📚";
            if (u.Contains("CLR VERSION"))      return "🔧";
            return "📋";
        }

        private static (string Name, string Icon) SectionGroupInfo(string title)
        {
            string u = title.ToUpperInvariant();
            // More-specific patterns checked before broad ones
            if (u.Contains("MEMORY LEAK") || u.Contains("FINALIZER") || u.Contains("DUPLICATE") ||
                u.Contains("STATIC ROOT")  || u.Contains("REFERENCE CHAIN") ||
                u.Contains("COLLECTION")   || u.Contains("EVENT LEAK"))
                return ("Leak Detection", "💧");

            if (u.Contains("CRASH") || u.Contains("EXCEPTION") || u.Contains("HANG"))
                return ("Stability", "🩺");

            if (u.Contains("MEMORY") || u.Contains("HEAP")  || u.Contains("LOH") ||
                u.Contains("GC GENERATION") || u.Contains("OVERALL") || u.Contains("TOP TYPES"))
                return ("Memory Health", "🧠");

            if (u.Contains("GC HANDLE") || u.Contains("DEPENDENT HANDLE"))
                return ("Handles & Roots", "🔗");

            if (u.Contains("THREAD"))
                return ("Threading", "🧵");

            if (u.Contains("MODULE") || u.Contains("ASSEMBLY") ||
                u.Contains("CLR VERSION") || u.Contains("VERSION CONFLICT"))
                return ("Infrastructure", "🏗️");

            return ("General", "📋");
        }

        private static int GroupSortOrder(string groupName) => groupName switch
        {
            "Stability"       => 0,
            "Leak Detection"  => 1,
            "Memory Health"   => 2,
            "Handles & Roots" => 3,
            "Threading"       => 4,
            "Infrastructure"  => 5,
            _                 => 6
        };

        private static IEnumerable<(string Name, string Icon, IEnumerable<ReportSection> Sections)> GroupSections(
            IReadOnlyList<ReportSection> sections)
        {
            return sections
                .Select(s => (Section: s, Info: SectionGroupInfo(s.Title)))
                .GroupBy(x => x.Info)
                .OrderBy(g => GroupSortOrder(g.Key.Name))
                .Select(g => (g.Key.Name, g.Key.Icon, g.Select(x => x.Section)));
        }

        private static void AddSectionIfNotEmpty(List<ReportSection> sections, string title, List<string> lines)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
                lines.RemoveAt(lines.Count - 1);

            if (lines.Count > 0)
                sections.Add(new ReportSection(title, lines));
        }

        private static bool IsSectionHeader(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.EndsWith(':')) return false;
            string core = line.TrimEnd(':').Trim();
            if (core.Length < 3) return false;
            bool hasLetter = false;
            foreach (char c in core)
            {
                if (char.IsLetter(c))
                {
                    hasLetter = true;
                    if (char.IsLower(c)) return false;
                }
            }
            return hasLetter;
        }

        private static bool IsSeparatorLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            foreach (char c in line)
                if (c != '=' && c != '-' && c != '_') return false;
            return line.Length >= 8;
        }

        private sealed record ReportSection(string Title, List<string> Lines);
        private sealed record ParsedReport(ReportSection? TrendSection, IReadOnlyList<ParsedDumpBlock> Blocks);
        private sealed record ParsedDumpBlock(string? Label, IReadOnlyList<ReportSection> Sections);
        private sealed record TrendContent(
            List<(string K, string V)> SummaryKV,
            List<(string Analyzer, List<string> Metrics)> TimelineGroups,
            List<string> NewFindings,
            List<string> ResolvedFindings);
    }
}

