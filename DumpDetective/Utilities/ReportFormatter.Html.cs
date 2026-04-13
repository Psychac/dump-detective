using DumpDetective.Models;
using System.Net;
using System.Text;

namespace DumpDetective.Utilities
{
    internal static partial class ReportFormatter
    {
        private static string ToHtml(string detailedReport, IReadOnlyList<string> insights, string dumpPath, IReadOnlyList<InsightFinding> findings)
        {
            var parsed = ParseDetailedReport(detailedReport);
            var b = new StringBuilder(capacity: 128 * 1024);

            int critCount = findings.Count(f => f.Severity == FindingSeverity.Critical);
            int warnCount = findings.Count(f => f.Severity == FindingSeverity.Warning);
            int infoCount = findings.Count(f => f.Severity == FindingSeverity.Info);

            b.AppendLine(HtmlHead());
            b.AppendLine("<body>");

            b.AppendLine("""
                <header class="topbar">
                  <span class="topbar-logo">🕵️ DumpDetective</span>
                  <span class="topbar-sub">Memory Dump Analysis Report</span>
                </header>
                """);

            b.AppendLine("<main class=\"page\">");

            b.AppendLine($$"""
                <div class="meta-card">
                  <div class="meta-row"><span class="meta-key">📁 Dump File</span><code class="meta-val">{{HtmlEnc(dumpPath)}}</code></div>
                  <div class="meta-row"><span class="meta-key">🕐 Generated (UTC)</span><code class="meta-val">{{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}}</code></div>
                </div>
                """);

            b.AppendLine($$"""
                <div class="sev-bar">
                  <div class="sev-pill sev-crit">🔴 <strong>{{critCount}}</strong> Critical</div>
                  <div class="sev-pill sev-warn">🟠 <strong>{{warnCount}}</strong> Warning</div>
                  <div class="sev-pill sev-info">🔵 <strong>{{infoCount}}</strong> Info</div>
                </div>
                """);

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

            if (parsed.TrendSection != null)
                AppendHtmlTrendSection(b, parsed.TrendSection);

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
    }
}
