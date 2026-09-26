using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>A data-quality flag an analyzer recorded (a capped scan, a sampled population, an
/// estimated size) and how much it lowers confidence in a finding built on the affected values.</summary>
internal readonly record struct DataQualityPenalty(bool Applies, double Penalty, string LimitingFactor);

/// <summary>
/// Lowers a finding's confidence for the data-quality flags that affect the values it reads —
/// applied per finding by its rule, so a flag only touches the findings whose claim depends on the
/// flagged values. The penalty model (subtract, floor at 0.2) is the one section builders used for
/// their confidence badges before those badges were derived from findings.
/// </summary>
internal static class DataQualityPenalties
{
    private const double Floor = 0.2;

    public static ConfidenceBreakdown Apply(ConfidenceBreakdown confidence, params DataQualityPenalty[] penalties)
    {
        double totalPenalty = 0;
        List<string>? limitingFactors = null;
        foreach (DataQualityPenalty penalty in penalties)
        {
            if (!penalty.Applies)
                continue;

            totalPenalty += penalty.Penalty;
            limitingFactors ??= [.. confidence.LimitingFactors];
            limitingFactors.Add(penalty.LimitingFactor);
        }

        if (limitingFactors is null)
            return confidence;

        return confidence with
        {
            Composite = Math.Clamp(confidence.Composite - totalPenalty, Floor, 1.0),
            EvidenceStrength = Math.Clamp(confidence.EvidenceStrength - totalPenalty, Floor, 1.0),
            LimitingFactors = limitingFactors,
        };
    }
}
