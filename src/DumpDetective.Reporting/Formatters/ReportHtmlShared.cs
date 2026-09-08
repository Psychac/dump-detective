using System.Globalization;
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
                case InterpretationBlock interp:
                    sb.AppendLine($"<div class=\"detail-interpretation{IndCss(interp.IndentLevel)}\">💡 {WrapAddr(Enc(interp.Text))}</div>");
                    break;
                case NextStepsBlock steps:
                    sb.AppendLine("<div class=\"detail-next-steps\">");
                    sb.AppendLine("<div class=\"detail-next-steps__title\">Next steps</div>");
                    sb.AppendLine("<ul>");
                    foreach (NextStepLink link in steps.Links)
                        sb.AppendLine($"<li><a href=\"#{Enc(link.SectionId)}\">{Enc(link.Label)}</a></li>");
                    sb.AppendLine("</ul></div>");
                    break;
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
        sb.AppendLine("<section class=\"section-card health-scorecard\" id=\"health-scorecard\" data-component-id=\"health-scorecard\">");
        sb.AppendLine("<span class=\"section-anchor-legacy\" id=\"sec-health\" aria-hidden=\"true\"></span>");

        DomainSeverity overallSeverity = scorecard.OverallSeverity;
        string overallCss = SeverityCss(overallSeverity);
        sb.AppendLine($"<div class=\"health-scorecard__banner health-scorecard__banner--{overallCss}\">");
        sb.AppendLine("<div class=\"health-scorecard__banner-left\">");
        sb.AppendLine("<span class=\"health-scorecard__banner-title\">Health Summary</span>");
        sb.AppendLine($"<span class=\"health-scorecard__banner-verdict\">{Enc(SeverityMark(overallSeverity))}&nbsp;{Enc(SeverityLabel(overallSeverity))}</span>");
        sb.AppendLine("</div>");

        if (scorecard.Trend is { } trend)
        {
            RenderTrendBannerStats(sb, trend);
        }
        else
        {
            RenderSingleDumpBannerStats(sb, scorecard.Domains.Values);
        }

        sb.AppendLine("</div>");

        bool hasTrendData = scorecard.Domains.Values.Any(static d => d.Change.HasValue);
        bool hasHistory = hasTrendData && scorecard.Domains.Values.Any(static d => d.SeverityHistory is { Count: > 2 });
        if (hasHistory)
        {
            int snapshotCount = 0;
            foreach (DomainHealthEntry entry in scorecard.Domains.Values)
            {
                if (entry.SeverityHistory is { Count: > 2 } history)
                {
                    snapshotCount = history.Count;
                    break;
                }
            }

            sb.AppendLine("<div class=\"health-scorecard__legend\">");
            sb.AppendLine("<span class=\"health-scorecard__legend-bar\"><span class=\"health-timeline-seg health-timeline-seg--ok\"></span><span class=\"health-timeline-seg health-timeline-seg--warning\"></span><span class=\"health-timeline-seg health-timeline-seg--critical\"></span></span>");
            sb.AppendLine($"<span class=\"health-scorecard__legend-text\">Severity trend bar - Base -> D{snapshotCount} ({snapshotCount} dumps)</span>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("<div class=\"health-scorecard__grid\" role=\"list\">");
        foreach (DomainHealthEntry entry in scorecard.Domains.Values)
        {
            if (hasTrendData)
                RenderTrendHealthCard(sb, entry);
            else
                RenderSingleDumpHealthRow(sb, entry);
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static void RenderTrendBannerStats(StringBuilder sb, TrendSummary trend)
    {
        if (trend.DomainsRegressed <= 0 && trend.DomainsImproved <= 0 && trend.NetCriticalChange == 0 && trend.NetWarningChange == 0)
            return;

        sb.AppendLine("<div class=\"health-scorecard__banner-right\">");
        if (trend.DomainsRegressed > 0) RenderBannerStat(sb, "Regressed", trend.DomainsRegressed, "regressed");
        if (trend.DomainsImproved > 0) RenderBannerStat(sb, "Improved", trend.DomainsImproved, "improved");
        if (trend.NetCriticalChange != 0) RenderBannerStat(sb, "Critical Δ", trend.NetCriticalChange, "critical", signed: true);
        if (trend.NetWarningChange != 0) RenderBannerStat(sb, "Warning Δ", trend.NetWarningChange, "warning", signed: true);
        sb.AppendLine("</div>");
    }

    private static void RenderSingleDumpBannerStats(StringBuilder sb, IEnumerable<DomainHealthEntry> entries)
    {
        int totalCritical = 0;
        int totalWarning = 0;
        foreach (DomainHealthEntry entry in entries)
        {
            totalCritical += entry.CriticalCount;
            totalWarning += entry.WarningCount;
        }

        if (totalCritical <= 0 && totalWarning <= 0)
            return;

        sb.AppendLine("<div class=\"health-scorecard__banner-right\">");
        if (totalCritical > 0) RenderBannerStat(sb, "Critical", totalCritical, "critical");
        if (totalWarning > 0) RenderBannerStat(sb, "Warning", totalWarning, "warning");
        sb.AppendLine("</div>");
    }

    private static void RenderBannerStat(StringBuilder sb, string label, int value, string modifier, bool signed = false)
    {
        string display = signed && value > 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);
        sb.AppendLine($"<div class=\"health-scorecard__banner-stat health-scorecard__banner-stat--{modifier}\"><span class=\"health-scorecard__banner-stat-label\">{Enc(label)}</span><span class=\"health-scorecard__banner-stat-value\">{Enc(display)}</span></div>");
    }

    private static void RenderSingleDumpHealthRow(StringBuilder sb, DomainHealthEntry entry)
    {
        string severityCss = SeverityCss(entry.Severity);
        sb.Append($"<div class=\"health-domain-row health-domain-row--{severityCss}\" role=\"listitem\">");
        sb.Append($"<span class=\"health-domain-row__name\">{Enc(entry.Domain)}</span>");
        sb.Append($"<span class=\"health-domain-row__pill health-domain-row__pill--{severityCss}\">{Enc(SeverityMark(entry.Severity))}&nbsp;{Enc(SeverityLabel(entry.Severity))}</span>");
        if (entry.CriticalCount > 0 || entry.WarningCount > 0)
        {
            var parts = new List<string>(2);
            if (entry.CriticalCount > 0) parts.Add($"{entry.CriticalCount.ToString(CultureInfo.InvariantCulture)}&nbsp;crit");
            if (entry.WarningCount > 0) parts.Add($"{entry.WarningCount.ToString(CultureInfo.InvariantCulture)}&nbsp;warn");
            sb.Append($"<span class=\"health-domain-row__counts\">{string.Join("&ensp;&middot;&ensp;", parts)}</span>");
        }
        sb.AppendLine("</div>");
    }

    private static void RenderTrendHealthCard(StringBuilder sb, DomainHealthEntry entry)
    {
        string severityCss = SeverityCss(entry.Severity);
        sb.AppendLine($"<div class=\"health-domain-card health-domain-card--{severityCss}\" role=\"listitem\">");
        sb.AppendLine("<div class=\"health-domain-card__head\">");
        sb.AppendLine($"<span class=\"health-domain-card__name\">{Enc(entry.Domain)}</span>");
        if (entry.Change.HasValue)
        {
            (string label, string css) = ChangeInfo(entry.Change.Value);
            sb.AppendLine($"<span class=\"health-domain-card__change health-domain-card__change--{css}\">{Enc(label)}</span>");
        }
        if (entry.VelocityScore is double velocity)
            RenderMovementPill(sb, entry, velocity);
        sb.AppendLine("</div>");

        if (entry.SeverityHistory is { Count: > 2 } history)
            RenderSeverityTimeline(sb, history);
        else if (entry.BaselineSeverity.HasValue)
            RenderSeverityTransition(sb, entry.BaselineSeverity.Value, entry.Severity);

        sb.Append("<div class=\"health-domain-card__foot\">");
        sb.Append($"<span class=\"health-domain-card__sev health-domain-card__sev--{severityCss}\">{Enc(SeverityMark(entry.Severity))}&nbsp;{Enc(SeverityLabel(entry.Severity))}</span>");
        RenderDeltaChips(sb, entry);
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
    }

    private static void RenderMovementPill(StringBuilder sb, DomainHealthEntry entry, double velocity)
    {
        string label;
        string modifier;
        if (velocity > 0.1) { label = "▲&nbsp;accel."; modifier = "accelerating"; }
        else if (velocity < -0.1) { label = "▼&nbsp;recov."; modifier = "recovering"; }
        else { label = "→&nbsp;stable"; modifier = "stable"; }

        string volatility = entry.VolatilityScore is double value ? value.ToString("F2", CultureInfo.InvariantCulture) : "—";
        string confidence = entry.ConfidenceTrend is not null ? $" · conf: {entry.ConfidenceTrend}" : string.Empty;
        sb.AppendLine($"<span class=\"health-domain-move health-domain-move--{modifier}\" role=\"status\" aria-label=\"Momentum: {modifier}\" title=\"Δv={velocity.ToString("F2", CultureInfo.InvariantCulture)} · σ={Enc(volatility + confidence)}\">{label}</span>");
    }

    private static void RenderSeverityTimeline(StringBuilder sb, IReadOnlyList<DomainSeverity> history)
    {
        sb.AppendLine("<div class=\"health-domain-card__timeline-wrap\">");
        sb.AppendLine("<div class=\"health-domain-card__timeline\">");
        for (int i = 0; i < history.Count; i++)
        {
            DomainSeverity severity = history[i];
            string role = i == 0 ? "Baseline" : i == history.Count - 1 ? "Current" : "Dump";
            sb.AppendLine($"<span class=\"health-timeline-seg health-timeline-seg--{SeverityCss(severity)}\" title=\"{role} #{i + 1} - {Enc(SeverityLabel(severity))}\"></span>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"health-domain-card__timeline-indices\">");
        for (int i = 0; i < history.Count; i++)
        {
            string cls = i == 0 ? " health-timeline-idx--first" : string.Empty;
            string label = i == 0 ? "Base" : $"D{i + 1}";
            sb.AppendLine($"<span class=\"health-timeline-idx{cls}\">{label}</span>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
    }

    private static void RenderSeverityTransition(StringBuilder sb, DomainSeverity baselineSeverity, DomainSeverity currentSeverity)
    {
        sb.AppendLine("<div class=\"health-domain-card__transition\">");
        sb.AppendLine($"<span class=\"health-domain-card__trans-sev health-domain-card__trans-sev--{SeverityCss(baselineSeverity)}\" title=\"Baseline\">{Enc(SeverityLabel(baselineSeverity))}</span>");
        sb.AppendLine("<span class=\"health-domain-card__trans-arrow\">-&gt;</span>");
        sb.AppendLine($"<span class=\"health-domain-card__trans-sev health-domain-card__trans-sev--{SeverityCss(currentSeverity)} health-domain-card__trans-sev--current\" title=\"Current\">{Enc(SeverityLabel(currentSeverity))}</span>");
        sb.AppendLine("</div>");
    }

    private static void RenderDeltaChips(StringBuilder sb, DomainHealthEntry entry)
    {
        if ((entry.DeltaCritical is null or 0) && (entry.DeltaWarning is null or 0))
            return;

        sb.Append("<span class=\"health-domain-deltas\">");
        if (entry.DeltaCritical is int criticalDelta and not 0)
            RenderDeltaChip(sb, criticalDelta, "crit", "Criticals", entry.BaselineCriticalCount, entry.CriticalCount, entry.PeakCriticalCount, entry.PeakCriticalSnapshotIndex);
        if (entry.DeltaWarning is int warningDelta and not 0)
            RenderDeltaChip(sb, warningDelta, "warn", "Warnings", entry.BaselineWarningCount, entry.WarningCount, entry.PeakWarningCount, entry.PeakWarningSnapshotIndex);
        sb.Append("</span>");
    }

    private static void RenderDeltaChip(StringBuilder sb, int delta, string modifier, string noun, int? baseline, int current, int? peak, int? peakIndex)
    {
        string direction = delta > 0 ? "up" : "down";
        string displayDelta = delta > 0 ? $"+{delta}" : delta.ToString(CultureInfo.InvariantCulture);
        int baselineValue = baseline ?? 0;
        string title = $"{noun}: {baselineValue.ToString(CultureInfo.InvariantCulture)} -> {current.ToString(CultureInfo.InvariantCulture)} ({displayDelta})";
        if (peak.HasValue && peakIndex.HasValue && peak.Value > Math.Max(baselineValue, current))
            title += $" - peak {peak.Value.ToString(CultureInfo.InvariantCulture)} at D{peakIndex.Value + 1}";
        string ariaLabel = delta > 0 ? $"{noun} increased by {Math.Abs(delta).ToString(CultureInfo.InvariantCulture)}" : $"{noun} decreased by {Math.Abs(delta).ToString(CultureInfo.InvariantCulture)}";
        sb.Append($"<span class=\"delta-chip delta-chip--{modifier} delta-chip--{direction}\" title=\"{Enc(title)}\" aria-label=\"{Enc(ariaLabel)}\">{Enc(displayDelta)}&nbsp;{Enc(modifier)}</span>");
    }

    private static string SeverityCss(DomainSeverity severity)
        => severity switch
        {
            DomainSeverity.Critical => "critical",
            DomainSeverity.Warning => "warning",
            DomainSeverity.OK => "ok",
            _ => "unknown"
        };

    private static string SeverityLabel(DomainSeverity severity)
        => severity == DomainSeverity.OK ? "OK" : severity.ToString();

    private static string SeverityMark(DomainSeverity severity)
        => severity switch
        {
            DomainSeverity.Critical => "●",
            DomainSeverity.Warning => "●",
            DomainSeverity.OK => "✓",
            _ => "○"
        };

    private static (string Label, string Css) ChangeInfo(DomainSeverityChange change)
        => change switch
        {
            DomainSeverityChange.Improved => ("↑ Improved", "improved"),
            DomainSeverityChange.Regressed => ("↓ Regressed", "regressed"),
            DomainSeverityChange.NewDomain => ("★ New", "new"),
            DomainSeverityChange.Removed => ("✕ Removed", "resolved"),
            _ => ("→ Stable", "stable")
        };

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
                string da = cell.RawValue.HasValue ? $" data-value=\"{cell.RawValue.Value.ToString(CultureInfo.InvariantCulture)}\"" : string.Empty;
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
            string evSummary = f.GetSummaryText();
            string summary = Enc(evSummary.Length > 200 ? evSummary[..200] : evSummary);
            sb.AppendLine($"<section id=\"finding-{i}\" class=\"section-card\" data-severity=\"{Enc(f.Severity?.ToLowerInvariant() ?? "info")}\" data-title=\"{Enc(f.Title)}\" data-summary=\"{summary}\">");
            sb.AppendLine($"<div class=\"section-header\"><span class=\"severity-badge {sevCss}\">{Enc(f.Severity)}</span><h2>{Enc(f.Title)} <a class=\"permalink\" href=\"#finding-{i}\" aria-label=\"Permalink\">🔗</a></h2><span class=\"category\">{Enc(f.Category)}</span></div>");

            if (f.Details is { Count: > 1 })
            {
                sb.AppendLine("<div class=\"summary\">" + string.Join("<br/>", f.Details.Select(e => Enc(e))) + "</div>");
                sb.AppendLine("<table><thead><tr><th scope=\"col\">Label</th><th scope=\"col\">Value</th></tr></thead><tbody>");
                sb.AppendLine($"<tr><td>Details</td><td class=\"wrap\"><ul>" + string.Join(string.Empty, f.Details.Select(e => $"<li>{WrapAddr(Enc(e))}</li>")) + "</ul></td></tr>");
            }
            else
            {
                sb.AppendLine($"<p class=\"summary\">{Enc(evSummary)}</p>");
                sb.AppendLine("<table><thead><tr><th scope=\"col\">Label</th><th scope=\"col\">Value</th></tr></thead><tbody>");
                sb.AppendLine($"<tr><td>Details</td><td class=\"wrap\">{WrapAddr(Enc(evSummary))}</td></tr>");
            }

            if (f.Confidence is not null)
                sb.AppendLine($"<tr><td>Confidence</td><td class=\"wrap\">{Enc(f.Confidence.Value.ToString("F2"))}</td></tr>");

            if (f.Caveats is { Count: > 0 })
                sb.AppendLine($"<tr><td>Caveats</td><td class=\"wrap\">{WrapAddr(Enc(string.Join("\n", f.Caveats)))}</td></tr>");

            if (!string.IsNullOrWhiteSpace(f.Recommendation))
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
            // Same anchor scheme as MarkdownCanonicalReportFormatter (SectionId when present,
            // detail-{i} fallback otherwise) — was index-only here, so a cross-section link built
            // from a stable SectionId (e.g. "#A5") resolved in Markdown output but was dead in this
            // pre-rendered HTML path. The primary client-side renderer (report.renderers.sections.js)
            // already preferred SectionId; this only affects this server-side pre-render fallback,
            // used for very large reports (see HtmlReportRenderer's shouldPreRender gate).
            string anchor = string.IsNullOrEmpty(section.SectionId) ? $"detail-{i}" : section.SectionId;
            string colorClass = $"detail-color-{i % 6}";
            sb.AppendLine($"<section id=\"{Enc(anchor)}\" class=\"analyzer-section {colorClass}\">");
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{Enc(section.DisplayTitle)} <a class=\"permalink\" href=\"#{Enc(anchor)}\" aria-label=\"Permalink\">🔗</a></summary>");
            sb.AppendLine("<div class=\"detail-block\">");
            RenderBlocksHtml(section.Blocks, sb);
            sb.AppendLine("</div></details></section>");
        }
        return sb.ToString();
    }
}
