namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// <c>InsightEngine.FormatBytes</c> moved here verbatim — deliberately not
/// <c>DumpDetective.Core.Utilities.FormatHelper.FormatBytes</c>, which rounds differently (always
/// <c>F1</c>, adds a TB tier). Every rule under phase 5 step 6.2
/// (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2) migrated from
/// <c>InsightEngine</c> shares this one copy, the same way they all shared
/// <c>InsightEngine.FormatBytes</c> before migration — unlike the per-analyzer rules elsewhere in
/// this folder, which each replace a distinct <c>FindingGenerator</c> that may have had its own
/// distinct formatting, these all descend from the one source method.
/// </summary>
internal static class InsightByteFormat
{
    public static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024UL * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024UL)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
