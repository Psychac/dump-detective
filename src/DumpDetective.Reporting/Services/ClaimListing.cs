using System.Globalization;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Utilities;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// The claims of a report as plain labelled parts, shared by the Markdown and text renderers so each adds only its own
/// markup (docs/ReportStructure/m1-claims-plan.md, D3). Wording comes from <see cref="ClaimProse"/>, the same source
/// as the HTML card. Findings whose rule has not authored an assertion (trend deltas, analyzer failures) are skipped.
/// </summary>
internal static class ClaimListing
{
    public static List<ClaimListingEntry> Build(IReadOnlyList<FindingRecord> findings)
    {
        var entries = new List<ClaimListingEntry>(findings.Count);
        foreach (FindingRecord finding in findings)
        {
            if (ClaimProse.Build(finding) is { } claim)
                entries.Add(EntryFor(finding, claim));
        }

        return entries;
    }

    private static ClaimListingEntry EntryFor(FindingRecord finding, ClaimView claim) => new(
        Severity: finding.Severity,
        Title: finding.Title,
        Analyzer: finding.Analyzer,
        Assertion: $"{claim.Predicate}: {claim.Assertion}",
        Confidence: ConfidenceLine(claim),
        ConfidenceReason: claim.ConfidenceReason,
        Support: EvidenceLines(claim.Support),
        Counter: CounterLines(claim.Counter),
        Tests: TestEntries(claim.Tests),
        Recommendation: string.IsNullOrWhiteSpace(finding.Recommendation) ? null : finding.Recommendation);

    private static string? ConfidenceLine(ClaimView claim)
        => claim.Confidence is double score
            ? $"{ConfidenceLadder.For(score).Label} ({score.ToString("0.00", CultureInfo.InvariantCulture)})"
            : null;

    private static List<string> EvidenceLines(IReadOnlyList<ClaimEvidenceView> statements)
    {
        var lines = new List<string>(statements.Count);
        foreach (ClaimEvidenceView statement in statements)
            lines.Add(statement.Why);
        return lines;
    }

    private static List<string> CounterLines(ClaimCounterView? counter)
    {
        if (counter is null)
            return [];

        List<string> lines = EvidenceLines(counter.Statements);
        if (counter.NoneBecause is { Length: > 0 } reason)
            lines.Add($"None found: {reason}");
        return lines;
    }

    private static List<ClaimListingTest> TestEntries(IReadOnlyList<ClaimTestView> tests)
    {
        var entries = new List<ClaimListingTest>(tests.Count);
        foreach (ClaimTestView test in tests)
            entries.Add(new ClaimListingTest($"[{test.Kind}] {test.Test}", test.Command));
        return entries;
    }
}

internal sealed record ClaimListingEntry(
    string Severity,
    string Title,
    string Analyzer,
    string Assertion,
    string? Confidence,
    string? ConfidenceReason,
    IReadOnlyList<string> Support,
    IReadOnlyList<string> Counter,
    IReadOnlyList<ClaimListingTest> Tests,
    string? Recommendation);

internal sealed record ClaimListingTest(string Description, string? Command);
