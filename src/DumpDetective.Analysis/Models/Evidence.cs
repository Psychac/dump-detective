using DumpDetective.Sdk.Observations;

namespace DumpDetective.Analysis.Models;

/// <summary>
/// Shared "why is this alive / why does this matter" shape for leak-adjacent analyzers.
/// Consumed by the ranking engine and confidence scoring; produced only for a small
/// top-K set of items per analyzer, never for every candidate before filtering.
/// </summary>
internal sealed record Evidence(
    ulong EstimatedRetainedBytes,
    string? SampleRootPath,
    bool RootPathSearchTruncated,
    IReadOnlyList<EvidenceSignal> ContributingSignals);

internal readonly record struct EvidenceSignal(string Name, string Description, double? Value = null);

/// <summary>
/// EventLeak-specific evidence shape (design §4.3): the publisher's own BFS-derived root path
/// and a cheap subscriber-derived root hint are tracked separately rather than conflated into
/// one field, so the report can label which source produced the displayed path. Distinct from
/// the shared <see cref="Evidence"/> record used by Dominator/StaticRoot/Timer analyzers.
/// Kept here (not moved to <c>Plugins.Leaks</c> with the rest of the EventLeak domain model)
/// because <see cref="EvidenceConfidence"/>'s <c>EventLeakEvidence</c> overload, a shared,
/// source-neutral utility every domain's finding generator relies on, needs it directly and
/// must not depend on any <c>Plugins.*</c> package.
/// </summary>
internal sealed record EventLeakEvidence(
    int SchemaVersion,
    string? PublisherRootPath,
    string? SampleSubscriberHint,
    bool SearchTruncated,
    IReadOnlyList<EvidenceSignal> Signals);

/// <summary>
/// Writes the raw root-path facts behind an <see cref="Evidence"/>/<see cref="EventLeakEvidence"/> onto
/// an observation, under a caller-chosen key prefix naming which entity they describe (e.g. the top
/// timer type). Facts only — whether a root path was found, whether its search was truncated, how
/// many contributing signals there were; the confidence judged from them is a synthesis-rule concern
/// (Reporting's <c>RootPathEvidenceStrength</c>).
/// </summary>
internal static class RootPathEvidenceMeasures
{
    internal static void Add(Dictionary<string, Measure> measures, string prefix, Evidence? evidence) =>
        Add(measures, prefix, evidence?.SampleRootPath, evidence?.RootPathSearchTruncated ?? false, evidence?.ContributingSignals.Count ?? 0);

    internal static void Add(Dictionary<string, Measure> measures, string prefix, EventLeakEvidence? evidence) =>
        Add(measures, prefix, evidence?.PublisherRootPath, evidence?.SearchTruncated ?? false, evidence?.Signals.Count ?? 0);

    private static void Add(Dictionary<string, Measure> measures, string prefix, string? rootPath, bool searchTruncated, int signalCount)
    {
        measures[prefix + "has-root-path"] = new Measure(string.IsNullOrEmpty(rootPath) ? 0 : 1, MeasureUnit.Count, MeasureSemantics.Absolute);
        measures[prefix + "is-root-path-search-truncated"] = new Measure(searchTruncated ? 1 : 0, MeasureUnit.Count, MeasureSemantics.Absolute);
        measures[prefix + "evidence-signal.count"] = new Measure(signalCount, MeasureUnit.Count, MeasureSemantics.Absolute);
    }
}
