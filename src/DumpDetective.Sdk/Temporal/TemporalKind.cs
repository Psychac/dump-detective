using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Temporal;

/// <summary>
/// Shape of a <see cref="TemporalExtent"/>: a dump is a point, a trace is an interval. See
/// docs/refactor/modularity/source-model.md § 5.
/// </summary>
/// <remarks>
/// No <c>Series</c> member, deliberately — removed 2026-09-10 (see
/// docs/refactor/modularity/phase-1-sdk-review-findings.md item 11). A <see cref="TemporalExtent"/>
/// lives on one <c>Observation</c>, which is scoped to exactly one artifact
/// (<c>Provenance.Artifact</c> is singular) — it can only ever describe a point or a bounded
/// interval for that one artifact, never a series across several. A multi-dump trend's ordered
/// view across N artifacts belongs on the session, not the observation: see
/// <c>AnalysisSession.Timeline : SessionTimeline</c>, source-model.md § 6 — Phase 4 territory, not
/// yet built. Confirmed nothing in the codebase constructed <c>TemporalKind.Series</c> before
/// removing it.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TemporalKind>))]
public enum TemporalKind
{
    Point,
    Interval,
}
