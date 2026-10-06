using DumpDetective.Sdk.Synthesis;
using System.Linq;

using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Maps <see cref="Claim"/> → <see cref="FindingRecord"/>, attaching evidence refs
/// derived from artifacts and tag-based metric keys.
/// </summary>
internal static class ReportFindingMapper
{
    public static FindingRecord MapFinding(Claim f, IReadOnlyList<ReportArtifact>? artifacts = null, int? snapshotIndex = null)
    {
        IReadOnlyList<string>? details = SplitLines(f.Narrative);

        FindingRecord record = new(
            Id: f.Fingerprint,
            Analyzer: f.Analyzer,
            Category: f.Category,
            Severity: f.Severity.ToString(),
            Title: f.Title,
            Details: details,
            Recommendation: f.Recommendation,
            Tags: f.Tags)
        {
            Confidence = BuildConfidenceScore(f),
            Refs = BuildEvidenceRefs(f, artifacts, snapshotIndex),
            Caveats = f.Caveats.Count > 0 ? f.Caveats : null
        };

        return f.DerivedFrom.Count == 0 ? record : record with
        {
            DerivedFrom = f.DerivedFrom,
            ConfidenceBreakdown = f.Confidence,
            Rule = f.Rule,
            Assertion = f.Assertion,
            Support = f.Support.Count > 0 ? f.Support : null,
            Counter = f.Counter,
            FalsifiedBy = f.FalsifiedBy.Count > 0 ? f.FalsifiedBy : null,
        };
    }

    private static IReadOnlyList<EvidenceRef> BuildEvidenceRefs(
        Claim finding,
        IReadOnlyList<ReportArtifact>? artifacts,
        int? snapshotIndex)
    {
        string? metricKey = BuildMetricKey(finding);
        ReportArtifact[]? matchingArtifacts = artifacts?
            .Where(a => string.Equals(a.Analyzer, finding.Analyzer, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingArtifacts is { Length: > 0 })
        {
            return matchingArtifacts
                .Select(a => new EvidenceRef(
                    Analyzer: finding.Analyzer,
                    MetricKey: metricKey,
                    Addresses: null,
                    ArtifactPath: a.FilePath ?? a.FileName,
                    SnapshotIndex: snapshotIndex))
                .ToArray();
        }

        return new[]
        {
            new EvidenceRef(
                Analyzer: finding.Analyzer,
                MetricKey: metricKey,
                Addresses: null,
                ArtifactPath: null,
                SnapshotIndex: snapshotIndex)
        };
    }

    private static string? BuildMetricKey(Claim finding)
    {
        for (int i = 0; i < finding.Tags.Count; i++)
        {
            string tag = finding.Tags[i];
            if (tag.Contains('.', StringComparison.Ordinal) || tag.Contains('_', StringComparison.Ordinal))
                return tag;
        }

        return null;
    }

    private static IReadOnlyList<string>? SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string[] parts = text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    private static double BuildConfidenceScore(Claim finding) => finding.ReportConfidence();
}
