using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// Folds the measured limits of a finding's observation lineage into the confidence its rule
/// judged, per docs/refactor/modularity/observation-and-correlation-model.md § 4. A rule's own
/// <see cref="ConfidenceBreakdown"/> says how well its evidence supports its claim; this adds what
/// the observations underneath it can and can't vouch for:
/// <list type="bullet">
/// <item><b>Evidence strength</b> — noisy-OR over independent sources, one per distinct
/// (artifact, analyzer) pair, never per observation: two observations from the same analyzer on
/// the same artifact are one signal counted twice. A source is only as strong as its weakest
/// full-fidelity measurement.</item>
/// <item><b>Capability fidelity</b> — the weakest measurement confidence among observations whose
/// analyzer ran degraded. Kept apart from evidence strength so a degraded run isn't also counted
/// as weak corroboration.</item>
/// <item><b>Identity and temporal caps</b> — only when the lineage spans more than one artifact:
/// within one artifact every join is on the same runtime's own names at the same instant. Across
/// artifacts no amount of agreement lifts confidence above the weakest identity match or time
/// anchor the join rests on.</item>
/// </list>
/// A lineage of full-fidelity, fully confident observations from one artifact leaves the rule's
/// confidence untouched.
/// </summary>
public static class ConfidenceCalculator
{
    public static ConfidenceBreakdown Apply(ConfidenceBreakdown ruleConfidence, IReadOnlyList<Observation> lineage)
    {
        if (lineage.Count == 0)
            return ruleConfidence;

        var weakestMeasurementBySource = new Dictionary<(ArtifactId Artifact, string AnalyzerKey), double>();
        var artifacts = new HashSet<ArtifactId>();
        double weakestDegradedMeasurement = 1.0;
        string? weakestDegradedAnalyzer = null;
        MatchFidelity weakestIdentity = MatchFidelity.Exact;
        AnchorConfidence weakestAnchor = AnchorConfidence.Exact;

        foreach (Observation observation in lineage)
        {
            artifacts.Add(observation.Provenance.Artifact);

            if (observation.Provenance.Fidelity == FidelityLevel.Degraded)
            {
                if (observation.Confidence < weakestDegradedMeasurement)
                {
                    weakestDegradedMeasurement = observation.Confidence;
                    weakestDegradedAnalyzer = observation.Provenance.AnalyzerKey;
                }
            }
            else
            {
                var source = (observation.Provenance.Artifact, observation.Provenance.AnalyzerKey);
                weakestMeasurementBySource[source] = weakestMeasurementBySource.TryGetValue(source, out double weakest)
                    ? Math.Min(weakest, observation.Confidence)
                    : observation.Confidence;
            }

            foreach (EntityRef subject in observation.Subjects)
                weakestIdentity = weakestIdentity.Min(subject.Fidelity);

            weakestAnchor = Weaker(weakestAnchor, observation.When.Start.Confidence);
            if (observation.When.End is { } end)
                weakestAnchor = Weaker(weakestAnchor, end.Confidence);
        }

        var limitingFactors = new List<string>(ruleConfidence.LimitingFactors);

        double corroboratedMeasurement = CorroboratedMeasurement(weakestMeasurementBySource, limitingFactors);

        if (weakestDegradedAnalyzer is not null)
            limitingFactors.Add($"{weakestDegradedAnalyzer} ran without an optional capability (measurement confidence {weakestDegradedMeasurement:0.##}).");

        bool spansArtifacts = artifacts.Count > 1;
        double identityCap = spansArtifacts ? IdentityCap(weakestIdentity) : 1.0;
        double temporalCap = spansArtifacts ? TemporalCap(weakestAnchor) : 1.0;
        if (identityCap < 1.0)
            limitingFactors.Add($"Joined across artifacts on a {weakestIdentity}-fidelity identity match.");
        if (temporalCap < 1.0)
            limitingFactors.Add($"Joined across artifacts with {weakestAnchor} time alignment.");

        double evidenceStrength = ruleConfidence.EvidenceStrength * corroboratedMeasurement;
        double capabilityFidelity = ruleConfidence.CapabilityFidelity * weakestDegradedMeasurement;
        identityCap = Math.Min(ruleConfidence.IdentityFidelityCap, identityCap);
        temporalCap = Math.Min(ruleConfidence.TemporalAlignmentCap, temporalCap);

        double composite = ruleConfidence.Composite * corroboratedMeasurement * weakestDegradedMeasurement;
        composite = Math.Min(composite, Math.Min(identityCap, temporalCap));

        return new ConfidenceBreakdown
        {
            Composite = composite,
            EvidenceStrength = evidenceStrength,
            IdentityFidelityCap = identityCap,
            TemporalAlignmentCap = temporalCap,
            CapabilityFidelity = capabilityFidelity,
            ConflictPenalty = ruleConfidence.ConflictPenalty,
            LimitingFactors = limitingFactors,
        };
    }

    private static double CorroboratedMeasurement(
        Dictionary<(ArtifactId Artifact, string AnalyzerKey), double> weakestMeasurementBySource, List<string> limitingFactors)
    {
        if (weakestMeasurementBySource.Count == 0)
            return 1.0;

        double allSourcesWrong = 1.0;
        foreach (double measurement in weakestMeasurementBySource.Values)
            allSourcesWrong *= 1.0 - measurement;

        double corroborated = 1.0 - allSourcesWrong;
        if (corroborated < 1.0)
        {
            foreach (((ArtifactId _, string analyzerKey), double measurement) in weakestMeasurementBySource)
                limitingFactors.Add($"{analyzerKey} measured this with confidence {measurement:0.##}.");
        }

        return corroborated;
    }

    // Declaration order is the ranking for both enums — see MatchFidelity's own remarks; AnchorConfidence
    // runs strongest-first, so the weaker anchor is the later-declared one.
    private static AnchorConfidence Weaker(AnchorConfidence a, AnchorConfidence b) => a > b ? a : b;

    private static double IdentityCap(MatchFidelity fidelity) => fidelity switch
    {
        MatchFidelity.Exact => 1.0,
        MatchFidelity.High => 0.9,
        MatchFidelity.Medium => 0.7,
        MatchFidelity.Low => 0.4,
        _ => 0.0,
    };

    private static double TemporalCap(AnchorConfidence confidence) => confidence switch
    {
        AnchorConfidence.Exact => 1.0,
        AnchorConfidence.Approximate => 0.7,
        _ => 0.4,
    };
}
