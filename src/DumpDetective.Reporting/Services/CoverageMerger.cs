using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Reporting.Services;

internal static class CoverageMerger
{
    /// <summary>
    /// A record from a coverage rule replaces the session record for the same capability and artifact, since a limit on the
    /// scan supersedes "available"; a rule record for anything else, including a failed analyzer (no capability), is added.
    /// </summary>
    public static IReadOnlyList<CoverageRecord> Merge(IReadOnlyList<CoverageRecord> session, IReadOnlyList<CoverageRecord> fromRules)
    {
        if (fromRules.Count == 0)
            return session;

        var merged = new List<CoverageRecord>(session);
        foreach (CoverageRecord record in fromRules)
        {
            int index = !record.Capability.HasValue
                ? -1
                : merged.FindIndex(existing => existing.Capability == record.Capability && existing.Artifact == record.Artifact);
            if (index < 0)
                merged.Add(record);
            else
                merged[index] = record;
        }

        return merged;
    }
}
