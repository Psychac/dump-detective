using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Models;

internal sealed record FindingLineage(IReadOnlyList<ObservationId> DerivedFrom, ConfidenceBreakdown Confidence)
{
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
        return new FindingLineage(merged, lowerConfidence);
    }
}
