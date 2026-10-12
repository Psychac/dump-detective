using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Words a session's <see cref="CoverageRecord"/>s once, for every format. A record the session supplied stays apart from one
/// it never supplied: "no trace" is not a gap in the dump, and the reader should not have to work that out.
/// </summary>
internal static class CoverageProse
{
    public static CoverageView? Build(IReadOnlyList<CoverageRecord>? records)
    {
        if (records is null || records.Count == 0)
            return null;

        var complete = new List<CoverageRecord>();
        var limited = new List<CoverageRecord>();
        var failed = new List<CoverageRecord>();
        var unavailable = new List<CoverageRecord>();
        var notSupplied = new List<CoverageRecord>();

        foreach (CoverageRecord record in records)
        {
            switch (record.Status)
            {
                case CoverageStatus.Complete: complete.Add(record); break;
                case CoverageStatus.Failed: failed.Add(record); break;
                case CoverageStatus.Unavailable: unavailable.Add(record); break;
                case CoverageStatus.NotSupplied: notSupplied.Add(record); break;
                default: limited.Add(record); break;
            }
        }

        var groups = new List<CoverageGroupView>(5);
        AddGroup(groups, "failed", "Analyzers that failed", failed.ConvertAll(FailedLine));
        AddGroup(groups, "limited", "Limited by their own budgets", limited.ConvertAll(LimitedLine));
        AddGroup(groups, "unavailable", "Not provided by the supplied artifacts", unavailable.ConvertAll(UnavailableLine));
        AddGroup(groups, "not-supplied", "Not supplied", NotSuppliedLines(notSupplied));
        AddGroup(groups, "complete", "Complete", complete.ConvertAll(r => new CoverageLineView(r.Capability!.Value.Key, [])));

        int supplied = complete.Count + limited.Count + unavailable.Count;
        return new CoverageView(Headline(complete.Count, supplied, limited, failed, notSupplied), groups);
    }

    public static string BoundaryText(CoverageRecord record) => record.Boundary switch
    {
        { Total: 0 } boundary => $"no {boundary.Unit} available ({boundary.Limit})",
        { Affected: { } affected, Total: { } total } boundary => $"{affected:N0} of {total:N0} {boundary.Unit} {boundary.Limit}",
        { Affected: { } affected } boundary => $"{affected:N0} {boundary.Unit} {boundary.Limit}",
        { } boundary => $"{boundary.Unit} {boundary.Limit}",
        null => "limited",
    };

    private static string Headline(
        int complete, int supplied, List<CoverageRecord> limited, List<CoverageRecord> failed, List<CoverageRecord> notSupplied)
    {
        var parts = new List<string> { $"{complete} of {supplied} capabilities complete" };

        if (limited.Count > 0)
            parts.Add($"{limited.Count} limited ({string.Join("; ", limited.ConvertAll(r => $"{r.Capability}: {BoundaryText(r)}"))})");

        parts.Add(failed.Count == 0 ? "no analyzer failed" : $"{failed.Count} analyzer(s) failed ({string.Join(", ", failed.ConvertAll(r => r.Impacts[0].Analyzer))})");

        string headline = "Coverage: " + string.Join("; ", parts) + ".";
        if (notSupplied.Count > 0)
            headline += " " + NotSuppliedSummary(notSupplied);

        return headline;
    }

    private static CoverageLineView FailedLine(CoverageRecord record) =>
        new($"{record.Impacts[0].Analyzer} failed before producing results.", []);

    private static CoverageLineView LimitedLine(CoverageRecord record) =>
        new($"{record.Capability}: {BoundaryText(record)}. Limits {string.Join(", ", AnalyzerNames(record))}.", record.Claims ?? []);

    private static CoverageLineView UnavailableLine(CoverageRecord record) =>
        new($"{record.Capability}: the supplied artifacts do not provide it. {ImpactText(record)}", []);

    // Grouped by capability family, because the reader's question is "what kind of artifact was missing", not "which capability".
    private static List<CoverageLineView> NotSuppliedLines(List<CoverageRecord> records)
    {
        var lines = new List<CoverageLineView>();
        foreach (IGrouping<string, CoverageRecord> family in records.GroupBy(r => FamilyOf(r.Capability!.Value.Key), StringComparer.Ordinal))
        {
            var members = family.ToList();
            lines.Add(new CoverageLineView($"{FamilyLabel(family.Key)}: {NotSuppliedFamilyText(members)}", []));
        }

        return lines;
    }

    private static string NotSuppliedSummary(List<CoverageRecord> records)
    {
        var families = new List<string>();
        foreach (IGrouping<string, CoverageRecord> family in records.GroupBy(r => FamilyOf(r.Capability!.Value.Key), StringComparer.Ordinal))
            families.Add($"{family.Key} ({NotSuppliedFamilyText(family.ToList())})");

        return "Not supplied: " + string.Join("; ", families) + ".";
    }

    private static string NotSuppliedFamilyText(List<CoverageRecord> members)
    {
        var notRun = new SortedSet<string>(StringComparer.Ordinal);
        var reduced = new SortedSet<string>(StringComparer.Ordinal);
        foreach (CoverageRecord record in members)
        {
            foreach (CoverageImpact impact in record.Impacts)
                (impact.Kind == CoverageImpactKind.NotRun ? notRun : reduced).Add(impact.Analyzer);
        }

        string text = members.Count == 1 ? "1 capability" : $"{members.Count} capabilities";
        if (notRun.Count > 0)
            text += $", {notRun.Count} analyzer(s) not run";
        if (reduced.Count > 0)
            text += $", {reduced.Count} at reduced fidelity";

        return text;
    }

    private static string FamilyLabel(string family) => family switch
    {
        "trace" => "No trace was supplied",
        "dump" => "No dump was supplied",
        _ => $"No {family} artifact was supplied",
    };

    private static string ImpactText(CoverageRecord record)
    {
        var notRun = new List<string>();
        var reduced = new List<string>();
        foreach (CoverageImpact impact in record.Impacts)
            (impact.Kind == CoverageImpactKind.NotRun ? notRun : reduced).Add(impact.Analyzer);

        var sentences = new List<string>(2);
        if (notRun.Count > 0)
            sentences.Add($"Not run: {string.Join(", ", notRun)}.");
        if (reduced.Count > 0)
            sentences.Add($"Reduced fidelity: {string.Join(", ", reduced)}.");

        return string.Join(" ", sentences);
    }

    private static IEnumerable<string> AnalyzerNames(CoverageRecord record) => record.Impacts.Select(i => i.Analyzer);

    private static string FamilyOf(string capabilityKey)
    {
        int dot = capabilityKey.IndexOf('.');
        return dot < 0 ? capabilityKey : capabilityKey[..dot];
    }

    private static void AddGroup(List<CoverageGroupView> groups, string kind, string title, List<CoverageLineView> lines)
    {
        if (lines.Count > 0)
            groups.Add(new CoverageGroupView(kind, title, lines));
    }
}
