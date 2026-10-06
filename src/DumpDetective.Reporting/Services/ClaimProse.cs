using System.Globalization;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Utilities;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// The one place a claim's structured fields become wording, so the HTML card, and later the Markdown and
/// text renderers, can never disagree about what a claim says (docs/ReportStructure/m1-claims-plan.md, D3).
/// Returns null for a claim whose rule has not authored an assertion yet, which renderers show as before.
/// </summary>
internal static class ClaimProse
{
    // Below this the headline confidence leaves the top band, which is when a reader needs to be told why.
    private const double TopBandFloor = 0.85;

    public static ClaimView? Build(FindingRecord finding)
    {
        if (finding.Assertion is not { } assertion)
            return null;

        // The headline chip reads the breakdown, not the finding's severity-defaulted score, so chip, reason
        // line and tooltip can never disagree.
        double? confidence = finding.ConfidenceBreakdown?.Composite;

        return new ClaimView(
            Predicate: assertion.Predicate.ToString(),
            Assertion: AssertionSentence(assertion),
            Confidence: confidence,
            ConfidenceBand: confidence is double score ? ConfidenceLadder.For(score).Key : null,
            ConfidenceReason: finding.ConfidenceBreakdown is { } breakdown ? ConfidenceReason(breakdown) : null,
            Support: EvidenceViews(finding.Support, finding.EvidenceSources?.Support),
            Counter: finding.Counter is { } counter ? CounterView(counter, finding.EvidenceSources?.Counter) : null,
            Tests: TestViews(finding.FalsifiedBy),
            Breakdown: finding.ConfidenceBreakdown is { } b ? BreakdownView(b) : null);
    }

    public static string AssertionSentence(Assertion assertion)
    {
        string subject = assertion.Qualifiers.Location ?? assertion.Subject?.JoinKey ?? "";
        string measure = assertion.Measure is { } m ? FormatMeasure(m) : "";

        // The predicate is shown separately as a label, and scope stays in report.json: this line is for a glance.
        string sentence = subject;
        if (measure.Length > 0)
            sentence += sentence.Length > 0 ? $" at {measure}" : measure;
        if (assertion.Reference is { } reference)
            sentence += $", against {FormatMeasure(reference.Value)} ({reference.Basis})";
        if (assertion.Qualifiers.Mechanism is { Length: > 0 } mechanism)
            sentence += $", via {mechanism}";
        return sentence + ".";
    }

    /// <summary>
    /// Names the binding limit and its remedy, or null while confidence stays in the top band. Which side is
    /// binding is read from the breakdown: capability, identity, temporal and evidence strength reflect the
    /// data; the conflict penalty reflects judgment. Evidence strength mixes corroboration (data) with the
    /// rule's own appraisal and cannot be split after the fact, so it counts as data. A rule that lowered its
    /// own composite with nothing else limiting it is making a judgment call.
    /// </summary>
    public static string? ConfidenceReason(ConfidenceBreakdown breakdown)
    {
        if (breakdown.Composite >= TopBandFloor)
            return null;

        double dataLimit = Math.Min(
            Math.Min(breakdown.CapabilityFidelity, breakdown.EvidenceStrength),
            Math.Min(breakdown.IdentityFidelityCap, breakdown.TemporalAlignmentCap));
        bool limitedByData = dataLimit < 1.0 && dataLimit <= breakdown.ConflictPenalty;

        string factors = breakdown.LimitingFactors.Count > 0 ? " " + string.Join(" ", breakdown.LimitingFactors) : "";
        string score = breakdown.Composite.ToString("0.00", CultureInfo.InvariantCulture);

        return limitedByData
            ? $"Limited by the data ({score}): get better data.{factors}"
            : $"Limited by the inference ({score}): the data is sound, the conclusion is uncertain; use judgment.{factors}";
    }

    private static List<ClaimEvidenceView> EvidenceViews(
        IReadOnlyList<EvidenceStatement>? statements, IReadOnlyList<IReadOnlyList<string>>? sources)
    {
        var views = new List<ClaimEvidenceView>(statements?.Count ?? 0);
        if (statements is null)
            return views;

        for (int i = 0; i < statements.Count; i++)
        {
            IReadOnlyList<string> statementSources = sources is not null && i < sources.Count ? sources[i] : [];
            views.Add(new ClaimEvidenceView(statements[i].Why, statements[i].Observations.Count, statementSources));
        }

        return views;
    }

    private static ClaimCounterView CounterView(Counter counter, IReadOnlyList<IReadOnlyList<string>>? sources)
        => new(EvidenceViews(counter.Statements, sources), counter.NoneFoundBecause);

    private static List<ClaimTestView> TestViews(IReadOnlyList<FalsificationTest>? tests)
    {
        var views = new List<ClaimTestView>(tests?.Count ?? 0);
        if (tests is null)
            return views;

        foreach (FalsificationTest test in tests)
            views.Add(new ClaimTestView(KindLabel(test.Kind), test.Test, test.Command));
        return views;
    }

    private static string KindLabel(FalsificationKind kind) => kind switch
    {
        FalsificationKind.Capture => "capture",
        FalsificationKind.Windbg => "windbg",
        FalsificationKind.CodeInspection => "code-inspection",
        _ => kind.ToString(),
    };

    private static ClaimBreakdownView BreakdownView(ConfidenceBreakdown b) => new(
        b.Composite, b.EvidenceStrength, b.IdentityFidelityCap, b.TemporalAlignmentCap, b.CapabilityFidelity, b.ConflictPenalty, b.LimitingFactors);

    private static string FormatMeasure(Measure measure) => measure.Unit switch
    {
        MeasureUnit.Percent => measure.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%",
        MeasureUnit.Bytes => FormatHelper.FormatBytes((ulong)Math.Max(0, measure.Value)),
        MeasureUnit.Count => measure.Value.ToString("N0", CultureInfo.InvariantCulture),
        MeasureUnit.Milliseconds => measure.Value.ToString("N0", CultureInfo.InvariantCulture) + " ms",
        MeasureUnit.PerSecond => measure.Value.ToString("N1", CultureInfo.InvariantCulture) + "/s",
        _ => measure.Value.ToString("0.##", CultureInfo.InvariantCulture),
    };
}
