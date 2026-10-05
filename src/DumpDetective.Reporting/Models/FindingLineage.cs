using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Models;

internal sealed record FindingLineage(IReadOnlyList<ObservationId> DerivedFrom, ConfidenceBreakdown Confidence, RuleRef? Rule = null)
{
    public Assertion? Assertion { get; init; }

    public IReadOnlyList<EvidenceStatement> Support { get; init; } = [];

    public Counter? Counter { get; init; }

    public IReadOnlyList<FalsificationTest> FalsifiedBy { get; init; } = [];

    public static FindingLineage From(Claim claim) => new(claim.DerivedFrom, claim.Confidence, claim.Rule)
    {
        Assertion = claim.Assertion,
        Support = claim.Support,
        Counter = claim.Counter,
        FalsifiedBy = claim.FalsifiedBy,
    };

    /// <summary>
    /// Two findings sharing a fingerprint are merged: every contributing observation is kept and the
    /// less confident breakdown wins, so a merge never silently drops evidence or overstates confidence.
    /// </summary>
    public FindingLineage MergeWith(FindingLineage other)
    {
        var merged = new List<ObservationId>(DerivedFrom);
        var seen = new HashSet<ObservationId>(DerivedFrom);
        foreach (ObservationId id in other.DerivedFrom)
        {
            if (seen.Add(id))
                merged.Add(id);
        }

        ConfidenceBreakdown lowerConfidence = other.Confidence.Composite < Confidence.Composite ? other.Confidence : Confidence;
        // The structured statement is the first claim's own; merging only widens lineage and lowers confidence.
        return this with { DerivedFrom = merged, Confidence = lowerConfidence, Rule = Rule ?? other.Rule };
    }
}
