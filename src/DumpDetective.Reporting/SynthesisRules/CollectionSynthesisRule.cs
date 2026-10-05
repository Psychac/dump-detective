using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>CollectionFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>collection.kind-waste</c>/<c>collection.element-type-waste</c> are exact scan-time
/// accumulators over the entire wasteful-collection population (per their own registry notes),
/// unlike <c>collection.wasteful</c> which is capped by <c>TopWastefulCollectionsToShow</c> — so the
/// per-kind/per-element-type breakdown and dominant-waste lookups read from those two observation
/// types, never from <c>collection.wasteful</c>. <c>collection.wasteful</c> is used only for its
/// <c>Kind</c> evidence, as the old generator's own fallback when no kind-waste observations exist
/// at all.
/// </remarks>
internal sealed class CollectionSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Collection Analysis";
    private static readonly ulong SummaryWarnBytes = 50UL * 1024 * 1024;

    public string RuleId => "collection.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["collection.waste-summary", "collection.kind-waste", "collection.element-type-waste", "collection.wasteful"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? summary = null;
        var kindWaste = new List<Observation>();
        var elementTypeWaste = new List<Observation>();
        var wastefulCollections = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "collection.waste-summary":
                    summary = observation;
                    break;
                case "collection.kind-waste":
                    kindWaste.Add(observation);
                    break;
                case "collection.element-type-waste":
                    elementTypeWaste.Add(observation);
                    break;
                case "collection.wasteful":
                    wastefulCollections.Add(observation);
                    break;
            }
        }

        if (summary is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        int totalCollections = (int)summary.Measures["total-collection.count"].Value;
        ulong totalWastedMemory = (ulong)summary.Measures["total-wasted.bytes"].Value;
        int wastefulCollectionCount = (int)summary.Measures["wasteful-collection.count"].Value;

        Severity severity = totalWastedMemory >= SummaryWarnBytes ? Severity.Warning : Severity.Info;

        // ── Per-kind breakdown, ordered by wasted bytes so the dominant kind reads first ─────────
        string perKindBreakdown = string.Empty;
        string? dominantWasteKind = null;
        if (kindWaste.Count > 0)
        {
            var kindsByWaste = kindWaste
                .OrderByDescending(o => (ulong)o.Measures["waste.bytes"].Value)
                .ThenBy(o => o.Evidence[0].Description, StringComparer.Ordinal)
                .ToList();

            perKindBreakdown = " (" + string.Join(", ", kindsByWaste.Select(o =>
                $"{o.Evidence[0].Description}: {(long)o.Measures["waste.count"].Value:N0} / {FormatHelper.FormatBytes((ulong)o.Measures["waste.bytes"].Value)}")) + ")";

            dominantWasteKind = kindsByWaste[0].Evidence[0].Description;
        }

        // ── Dominant element type, ordered by wasted bytes ────────────────────────────────────────
        string dominantElementTypeNote = string.Empty;
        if (elementTypeWaste.Count > 0)
        {
            Observation topElementType = elementTypeWaste
                .OrderByDescending(o => (ulong)o.Measures["waste.bytes"].Value)
                .ThenBy(o => ((TypeRef)o.Subjects[0]).CanonicalName, StringComparer.Ordinal)
                .First();
            ulong topElementBytes = (ulong)topElementType.Measures["waste.bytes"].Value;
            dominantElementTypeNote = $" Dominant wasted element type: {((TypeRef)topElementType.Subjects[0]).CanonicalName} ({FormatHelper.FormatBytes(topElementBytes)}).";
        }

        string evidence = $"{totalCollections:N0} collections scanned; estimated unused capacity {FormatHelper.FormatBytes(totalWastedMemory)} across {wastefulCollectionCount:N0} wasteful collections{perKindBreakdown}.{dominantElementTypeNote}";

        string recommendation = severity == Severity.Warning
            ? "Trim long-lived collections and initialize with realistic capacities."
            : "Collection sizing appears acceptable in this snapshot.";

        // If a particular kind dominates wasted bytes we can add a focused recommendation.
        string? topKind = dominantWasteKind ?? (wastefulCollections.Count > 0 ? wastefulCollections[0].Evidence[0].Description : null);
        if (topKind is not null && topKind != "List")
        {
            recommendation += $" Consider addressing {topKind} instances specifically (e.g., TrimExcess / Resize / Use alternative collection) if they are long-lived.";
        }

        var derivedFrom = new List<ObservationId> { summary.Id };
        foreach (Observation o in kindWaste) derivedFrom.Add(o.Id);
        foreach (Observation o in elementTypeWaste) derivedFrom.Add(o.Id);

        var finding = new Claim
        {
            Fingerprint = Fingerprint("capacity-efficiency"),
            Analyzer = AnalyzerName,
            Category = "Memory",
            Severity = severity,
            Title = "Collection capacity efficiency",
            Narrative = evidence,
            Recommendation = recommendation,
            Tags = ["collections", "memory-waste", "capacity"],
            MetricValue = totalWastedMemory,
            MetricUnit = "wasted-bytes",
            DerivedFrom = derivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        };

        return ValueTask.FromResult<IReadOnlyList<Claim>>([finding]);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
