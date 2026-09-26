using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// How strongly a retention finding is supported by the GC root path sampled for its top
/// candidate: none found is speculative, a truncated search is partial, a resolved path is strong,
/// with a small bonus per contributing signal. Reads the raw facts
/// <c>RootPathEvidenceMeasures</c> writes under a per-entity prefix; reproduces the retired
/// <c>EvidenceConfidence.Compute</c> exactly, including its operation order, so the scores match
/// the pre-Phase-5 generators bit for bit.
/// </summary>
internal static class RootPathEvidenceStrength
{
    private const double NoRootPathStrength = 0.4;
    private const double TruncatedRootPathStrength = 0.6;
    private const double ResolvedRootPathStrength = 0.8;
    private const double PerSignalBonus = 0.02;
    private const int MaxCountedSignals = 3;
    private const double MaxStrength = 0.9;

    public static ConfidenceBreakdown FromMeasures(IReadOnlyDictionary<string, Measure> measures, string prefix)
    {
        if (measures[prefix + "has-root-path"].Value == 0)
            return EvidenceLimited(NoRootPathStrength, "No GC root path was resolved for the top candidate.");

        bool searchTruncated = measures[prefix + "is-root-path-search-truncated"].Value != 0;
        int signalCount = (int)measures[prefix + "evidence-signal.count"].Value;

        double strength = searchTruncated ? TruncatedRootPathStrength : ResolvedRootPathStrength;
        strength += Math.Min(signalCount, MaxCountedSignals) * PerSignalBonus;
        strength = Math.Min(strength, MaxStrength);

        return EvidenceLimited(strength, searchTruncated
            ? "The GC root-path search for the top candidate was truncated."
            : "Retention is supported by one sampled GC root path.");
    }

    private static ConfidenceBreakdown EvidenceLimited(double strength, string limitingFactor) => new()
    {
        Composite = strength,
        EvidenceStrength = strength,
        IdentityFidelityCap = 1.0,
        TemporalAlignmentCap = 1.0,
        CapabilityFidelity = 1.0,
        ConflictPenalty = 1.0,
        LimitingFactors = [limitingFactor],
    };
}
