using System.Text;
using System.Text.RegularExpressions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Formatters;

internal static class ReportHtmlShared
{
    public static string Enc(string? v) => System.Net.WebUtility.HtmlEncode(v ?? string.Empty);

    /// <summary>
    /// Renders a compact per-snapshot severity progression strip as inline HTML badges.
    /// E.g. OK → Warning → Critical → Warning
    /// </summary>
    private static string RenderSeverityProgressionHtml(IReadOnlyList<DomainSeverity>? history)
    {
        if (history is null or { Count: 0 }) return "—";
        var sb = new StringBuilder();
        for (int i = 0; i < history.Count; i++)
        {
            string sev = history[i].ToString();
            string css = $"health-severity health-severity-{sev.ToLowerInvariant()}";
            string label = i == 0 ? $"#{i + 1} (baseline)" : i == history.Count - 1 ? $"#{i + 1} (current)" : $"#{i + 1}";
            sb.Append($"<span class=\"{css}\" title=\"Snapshot {label}\">{Enc(sev)}</span>");
            if (i < history.Count - 1)
                sb.Append("<span class=\"health-progress-arrow\">→</span>");
        }
        return sb.ToString();
    }

    public static string WrapAddr(string html) =>
        Regex.Replace(html, @"0x[0-9A-Fa-f]{4,}",
            m => $"<span class=\"addr\">{m.Value}<button class=\"copy-btn\" type=\"button\" aria-label=\"Copy {m.Value}\" data-copy=\"{m.Value}\" title=\"Copy\">&#x2398;</button></span>",
            RegexOptions.CultureInvariant);

    public static void RenderBlocksHtml(IReadOnlyList<SectionBlock> blocks, StringBuilder sb)
    {
        static string IndCss(int lvl) => lvl switch { 1 => " detail-indent-1", 2 => " detail-indent-2", >= 3 => " detail-indent-2", _ => string.Empty };

        foreach (SectionBlock block in blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    sb.AppendLine($"<div class=\"detail-subheading{IndCss(h.IndentLevel)}\">{Enc(h.Text)}</div>");
                    break;
                case MetricBlock m:
                    sb.AppendLine($"<div class=\"detail-line{IndCss(m.IndentLevel)}\"><span class=\"detail-key\">{Enc(m.Label)}:</span> <span class=\"detail-value wrap\">{WrapAddr(Enc(m.Value))}</span></div>");
                    break;
                case PathBlock p:
                    sb.AppendLine($"<div class=\"detail-line{IndCss(p.IndentLevel)}\"><span class=\"detail-key\">{Enc(p.Label)}:</span> <span class=\"detail-path wrap\">{WrapAddr(Enc(p.Path))}</span></div>");
                    break;
                case StackFrameBlock sf:
                    {
                        string fwCls = sf.IsFrameworkFrame ? " frame-fw" : " frame-app";
                        sb.AppendLine($"<div class=\"detail-line detail-frame{fwCls}{IndCss(sf.IndentLevel)}\"><code class=\"frame-code\">{WrapAddr(Enc("at " + sf.Frame))}</code></div>");
                        break;
                    }
                case TextBlock t:
                    sb.AppendLine($"<div class=\"detail-line{IndCss(t.IndentLevel)}\">{WrapAddr(Enc(t.Text))}</div>");
                    break;
                case ListItemBlock l:
                    sb.AppendLine($"<div class=\"detail-line{IndCss(l.IndentLevel)}\">&#x2022; {WrapAddr(Enc(l.Text))}</div>");
                    break;
                case DividerBlock:
                    sb.AppendLine("<div class=\"detail-divider\"></div>");
                    break;
                case BlankBlock:
                    sb.AppendLine("<div class=\"detail-gap\"></div>");
                    break;
                case TableBlock tbl:
                    RenderTableHtml(tbl, sb);
                    break;
                case ChartBlock chart:
                    sb.AppendLine($"<div class=\"detail-chart detail-indent-{Math.Min(chart.IndentLevel, 3)}\" data-chart-kind=\"{Enc(chart.Kind)}\" data-chart-payload=\"{Enc(chart.PayloadJson)}\" data-chart-title=\"{Enc(chart.Title)}\"></div>");
                    break;
                case ConfidenceBandBlock band:
                    {
                        string bandCss = band.Band.ToLowerInvariant();
                        sb.AppendLine("<div class=\"detail-confidence\">");
                        sb.AppendLine($"<span class=\"confidence-band confidence-{bandCss}\">{Enc(band.Symbol)} {Enc(band.Band)} confidence</span>");
                        if (band.Caveats.Length > 0)
                        {
                            sb.AppendLine("<ul class=\"confidence-caveats\">");
                            foreach (string caveat in band.Caveats)
                                sb.AppendLine($"<li>{Enc(caveat)}</li>");
                            sb.AppendLine("</ul>");
                        }
                        sb.AppendLine("</div>");
                        break;
                    }
                case CollapsibleSectionBeginBlock cs:
                    sb.AppendLine($"<details class=\"detail-nested\"><summary>{Enc(cs.Title)}</summary><div class=\"detail-nested-content\">");
                    break;
                case CollapsibleSectionEndBlock:
                    sb.AppendLine("</div></details>");
                    break;
                case SparklineBlock spark:
                    {
                        string valuesJson = System.Text.Json.JsonSerializer.Serialize(spark.Values);
                        sb.AppendLine($"<div class=\"sparkline\" data-metric=\"{Enc(spark.MetricKey)}\" data-unit=\"{Enc(spark.Unit)}\" data-direction=\"{Enc(spark.Direction)}\" data-values=\"{Enc(valuesJson)}\"></div>");
                        break;
                    }
            }
        }
    }

    public static string RenderHealthScorecard(HealthScorecard? scorecard)
    {
        if (scorecard is null || scorecard.Domains.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<section class=\"section-card health-scorecard\"><h2>Health Summary</h2>");
        bool hasTrendData = scorecard.Domains.Any(d => d.Change.HasValue);
        bool hasHistory   = hasTrendData && scorecard.Domains.Any(d => d.SeverityHistory is { Count: > 2 });
        sb.Append("<table>");
        if (hasTrendData)
        {
            if (hasHistory)
                sb.AppendLine("<thead><tr><th scope=\"col\">Domain</th><th scope=\"col\">Baseline</th><th scope=\"col\">Progression</th><th scope=\"col\">Current</th><th scope=\"col\">Change</th><th scope=\"col\">Movement</th><th scope=\"col\">Critical</th><th scope=\"col\">Warning</th></tr></thead><tbody>");
            else
                sb.AppendLine("<thead><tr><th scope=\"col\">Domain</th><th scope=\"col\">Baseline</th><th scope=\"col\">Current</th><th scope=\"col\">Change</th><th scope=\"col\">Movement</th><th scope=\"col\">Critical</th><th scope=\"col\">Warning</th></tr></thead><tbody>");
            foreach (DomainHealthEntry entry in scorecard.Domains)
            {
                string cur = entry.Severity.ToString();
                string bas = entry.BaselineSeverity?.ToString() ?? "—";
                string chg = entry.Change switch
                {
                    DomainSeverityChange.Regressed  => "⬆ Regressed",
                    DomainSeverityChange.Improved   => "⬇ Improved",
                    DomainSeverityChange.NewDomain  => "🆕 New",
                    DomainSeverityChange.Removed    => "🗑 Removed",
                    _                               => "= Stable"
                };
                string chgCss = entry.Change switch
                {
                    DomainSeverityChange.Regressed => "trend-regressed",
                    DomainSeverityChange.Improved  => "trend-improved",
                    _                              => string.Empty
                };
                string sevCss = $"health-severity health-severity-{cur.ToLowerInvariant()}";

                string movementHtml = "";
                if (entry.VelocityScore is double v)
                {
                    string state;
                    string cls;
                    if (v > 0.1) { state = "\u25b2\u00a0accel."; cls = "health-domain-move--accelerating"; }
                    else if (v < -0.1) { state = "\u25bc\u00a0recov."; cls = "health-domain-move--recovering"; }
                    else { state = "\u2192\u00a0stable"; cls = "health-domain-move--stable"; }
                    string vol = entry.VolatilityScore is double volv ? volv.ToString("F2") : "\u2014";
                    string conf = entry.ConfidenceTrend is not null ? $" \u00b7 conf: {entry.ConfidenceTrend}" : "";
                    movementHtml = $"<span class=\"health-domain-move {cls}\" role=\"status\" aria-label=\"Momentum\" title=\"\u0394v={v:F2} \u00b7 \u03c3={vol}{conf}\">{Enc(state)}</span>";
                }

                if (hasHistory)
                {
                    string progression = RenderSeverityProgressionHtml(entry.SeverityHistory);
                    sb.AppendLine($"<tr><td>{Enc(entry.Domain)}</td><td>{Enc(bas)}</td><td class=\"health-progression\">{progression}</td><td class=\"{sevCss}\">{Enc(cur)}</td><td class=\"{chgCss}\">{Enc(chg)}</td><td>{movementHtml}</td><td>{entry.CriticalCount}</td><td>{entry.WarningCount}</td></tr>");
                }
                else
                {
                    sb.AppendLine($"<tr><td>{Enc(entry.Domain)}</td><td>{Enc(bas)}</td><td class=\"{sevCss}\">{Enc(cur)}</td><td class=\"{chgCss}\">{Enc(chg)}</td><td>{movementHtml}</td><td>{entry.CriticalCount}</td><td>{entry.WarningCount}</td></tr>");
                }
            }
        }
        else
        {
            sb.AppendLine("<thead><tr><th scope=\"col\">Domain</th><th scope=\"col\">Severity</th><th scope=\"col\">Critical</th><th scope=\"col\">Warning</th></tr></thead><tbody>");
            foreach (DomainHealthEntry entry in scorecard.Domains)
            {
                string severity = entry.Severity.ToString();
                string severityCss = $"health-severity health-severity-{severity.ToLowerInvariant()}";
                sb.AppendLine($"<tr><td>{Enc(entry.Domain)}</td><td class=\"{severityCss}\">{Enc(severity)}</td><td>{entry.CriticalCount}</td><td>{entry.WarningCount}</td></tr>");
            }
        }
        sb.AppendLine("</tbody></table>");
        sb.AppendLine($"<div class=\"health-scorecard__overall\">Overall severity: {Enc(scorecard.OverallSeverity.ToString())}</div>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    public static void RenderTableHtml(TableBlock tbl, StringBuilder sb)
    {
        sb.Append("<table>");
        if (tbl.Caption is not null) sb.Append($"<caption>{Enc(tbl.Caption)}</caption>");
        sb.Append("<thead><tr>");
        foreach (string h in tbl.Headers) sb.Append($"<th scope=\"col\">{Enc(h)}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (TableRow row in tbl.Rows)
        {
            sb.Append("<tr>");
            foreach (TableCell cell in row.Cells)
            {
                string da = cell.RawValue.HasValue ? $" data-value=\"{cell.RawValue.Value}\"" : string.Empty;
                string display = cell.Display ?? string.Empty;
                // Sparkline payload token: __SPARK__<json> (legacy)
                if (display.StartsWith("__SPARK__", StringComparison.Ordinal))
                {
                    string payload = display.Substring("__SPARK__".Length);
                    sb.Append($"<td data-sparkline=\"{Enc(payload)}\"{da}></td>");
                    continue;
                }
                // Link target via TableCell.LinkTarget (preferred)
                if (cell.LinkTarget is { Length: > 0 } linkTarget)
                {
                    sb.Append($"<td{da}><a class=\"trend-jump\" href=\"#{Enc(linkTarget)}\">{Enc(display)}</a></td>");
                    continue;
                }
                // Legacy link token: <text>||__LINK__detail-<n>
                const string linkMarker = "||__LINK__";
                int li = display.IndexOf(linkMarker, StringComparison.Ordinal);
                if (li >= 0)
                {
                    string left = display.Substring(0, li);
                    string target = display.Substring(li + linkMarker.Length);
                    sb.Append($"<td{da}>{Enc(left)} <a class=\"trend-jump\" href=\"#{Enc(target)}\" aria-label=\"Jump to snapshot\">↳</a></td>");
                    continue;
                }

                sb.Append($"<td{da}>{Enc(display)}</td>");
            }
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table>");
    }

    public static string RenderFindings(IReadOnlyList<FindingRecord>? findings)
    {
        var sb = new StringBuilder();
        if (findings is null || findings.Count == 0)
            return string.Empty;

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord f = findings[i];
            string sevCss = f.Severity?.ToLowerInvariant() switch
            {
                "critical" => "severity-critical",
                "warning" => "severity-warning",
                _ => "severity-info"
            };
            string evSummary = f.EvidenceItems is { Count: > 0 } ? f.EvidenceItems[0] : f.Evidence ?? string.Empty;
            string summary = Enc(evSummary.Length > 200 ? evSummary[..200] : evSummary);
            sb.AppendLine($"<section id=\"finding-{i}\" class=\"section-card\" data-severity=\"{Enc(f.Severity?.ToLowerInvariant() ?? "info")}\" data-title=\"{Enc(f.Title)}\" data-summary=\"{summary}\">");
            sb.AppendLine($"<div class=\"section-header\"><span class=\"severity-badge {sevCss}\">{Enc(f.Severity)}</span><h2>{Enc(f.Title)} <a class=\"permalink\" href=\"#finding-{i}\" aria-label=\"Permalink\">🔗</a></h2><span class=\"category\">{Enc(f.Category)}</span></div>");

            if (f.EvidenceItems is { Count: > 1 })
            {
                sb.AppendLine("<div class=\"summary\">" + string.Join("<br/>", f.EvidenceItems.Select(e => Enc(e))) + "</div>");
                sb.AppendLine("<table><thead><tr><th scope=\"col\">Label</th><th scope=\"col\">Value</th></tr></thead><tbody>");
                sb.AppendLine($"<tr><td>Evidence</td><td class=\"wrap\"><ul>" + string.Join(string.Empty, f.EvidenceItems.Select(e => $"<li>{WrapAddr(Enc(e))}</li>")) + "</ul></td></tr>");
            }
            else
            {
                sb.AppendLine($"<p class=\"summary\">{Enc(f.Evidence ?? string.Empty)}</p>");
                sb.AppendLine("<table><thead><tr><th scope=\"col\">Label</th><th scope=\"col\">Value</th></tr></thead><tbody>");
                sb.AppendLine($"<tr><td>Evidence</td><td class=\"wrap\">{WrapAddr(Enc(f.Evidence ?? string.Empty))}</td></tr>");
            }

            if (!string.IsNullOrWhiteSpace(f.Cause))
                sb.AppendLine($"<tr><td>Cause</td><td class=\"wrap\">{WrapAddr(Enc(f.Cause))}</td></tr>");

            if (!string.IsNullOrWhiteSpace(f.Effect))
                sb.AppendLine($"<tr><td>Effect</td><td class=\"wrap\">{WrapAddr(Enc(f.Effect))}</td></tr>");

            if (f.ConfidenceScore is not null)
                sb.AppendLine($"<tr><td>Confidence</td><td class=\"wrap\">{Enc(f.ConfidenceScore.Value.ToString("F2"))}</td></tr>");

            if (!string.IsNullOrWhiteSpace(f.SuggestedOwner))
                sb.AppendLine($"<tr><td>Owner</td><td class=\"wrap\">{WrapAddr(Enc(f.SuggestedOwner))}</td></tr>");

            if (!string.IsNullOrWhiteSpace(f.Effort))
                sb.AppendLine($"<tr><td>Effort</td><td class=\"wrap\">{WrapAddr(Enc(f.Effort))}</td></tr>");

            if (!string.IsNullOrWhiteSpace(f.ValidationStep))
                sb.AppendLine($"<tr><td>Validation</td><td class=\"wrap\">{WrapAddr(Enc(f.ValidationStep))}</td></tr>");

            if (!string.IsNullOrWhiteSpace(f.TrackingStatus))
                sb.AppendLine($"<tr><td>Status</td><td class=\"wrap\">{WrapAddr(Enc(f.TrackingStatus))}</td></tr>");

            if (!string.IsNullOrWhiteSpace(f.Fix))
                sb.AppendLine($"<tr><td>Fix</td><td class=\"wrap\">{WrapAddr(Enc(f.Fix))}</td></tr>");

            if (f.RecommendationItems is { Count: > 0 })
            {
                sb.AppendLine($"<tr><td>Recommendation</td><td class=\"wrap\">{WrapAddr(Enc(string.Join("\n", f.RecommendationItems)))}</td></tr>");
            }
            else if (!string.IsNullOrWhiteSpace(f.Recommendation))
            {
                sb.AppendLine($"<tr><td>Recommendation</td><td class=\"wrap\">{WrapAddr(Enc(f.Recommendation))}</td></tr>");
            }
            sb.AppendLine("</tbody></table></section>");
        }
        return sb.ToString();
    }

    public static string RenderAnalyzerSections(IReadOnlyList<AnalyzerDetailSection>? sections)
    {
        var sb = new StringBuilder();
        if (sections is null || sections.Count == 0)
            return string.Empty;

        for (int i = 0; i < sections.Count; i++)
        {
            AnalyzerDetailSection section = sections[i];
            string colorClass = $"detail-color-{i % 6}";
            sb.AppendLine($"<section id=\"detail-{i}\" class=\"analyzer-section {colorClass}\">");
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{Enc(section.DisplayTitle)} <a class=\"permalink\" href=\"#detail-{i}\" aria-label=\"Permalink\">🔗</a></summary>");
            sb.AppendLine("<div class=\"detail-block\">");
            RenderBlocksHtml(section.Blocks, sb);
            sb.AppendLine("</div></details></section>");
        }
        return sb.ToString();
    }
}
