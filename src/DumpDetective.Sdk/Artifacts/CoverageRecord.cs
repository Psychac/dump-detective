using System.Text.Json.Serialization;

using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Sdk.Artifacts;

/// <summary>
/// How much of a capability a session actually had. Only <see cref="Complete"/> and
/// <see cref="Unavailable"/> are produced from capability availability alone; the rest are reserved for
/// the steps that know how far an analyzer got (docs/ReportStructure/m3-coverage-plan.md steps 3-4).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoverageStatus>))]
public enum CoverageStatus
{
    /// <summary>Available with no known limitation. This is availability, not proof that every analyzer finished its scan.</summary>
    Complete,
    Degraded,
    Partial,
    Skipped,
    Failed,

    /// <summary>No artifact in the session provides the capability.</summary>
    Unavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<CoverageImpactKind>))]
public enum CoverageImpactKind
{
    /// <summary>The analyzer requires the capability and was dropped.</summary>
    NotRun,

    /// <summary>The analyzer runs without a capability it can optionally use.</summary>
    ReducedFidelity,

    /// <summary>The analyzer failed before producing results; it is not tied to any capability.</summary>
    Failed,

    /// <summary>The analyzer ran but stopped at a limit before finishing its scan; the boundary says how far it got.</summary>
    Limited,
}

/// <summary>
/// Which part of a scan a limit touched: <paramref name="Affected"/> of <paramref name="Total"/> <paramref name="Unit"/> followed by
/// <paramref name="Limit"/>, a verb phrase ("hit the path or depth budget"). <paramref name="Total"/> is null when the analyzer does not
/// know the population; zero means nothing was available to scan, and <paramref name="Limit"/> then says why.
/// </summary>
public sealed record CoverageBoundary(long Affected, long? Total, string Unit, string Limit);

/// <summary>An analyzer affected by a missing capability. <paramref name="Fidelity"/> is set only for <see cref="CoverageImpactKind.ReducedFidelity"/>.</summary>
public sealed record CoverageImpact(string Analyzer, CoverageImpactKind Kind, FidelityBoost? Fidelity = null);

/// <summary>
/// One capability's coverage in a session (docs/ReportStructure/ReportSystemVision.md § 7.1).
/// <paramref name="Capability"/> is null only for a failed analyzer, which is stated as a fact about that analyzer rather than as a
/// gap in any capability it declared (other analyzers may still cover them). <paramref name="Artifact"/> names the artifact that
/// provides the capability; it is null when none does, because a missing capability has no artifact to attach to. <paramref name="Impacts"/> is structured so
/// the report words the consequence once, in one place. <paramref name="Boundary"/> and <paramref name="Rule"/> are set
/// only on records an <see cref="ICoverageRule"/> produced; <paramref name="Claims"/> lists the fingerprints of the claims
/// whose analyzer is limited, so the report can show a claim's coverage without searching.
/// </summary>
public sealed record CoverageRecord(
    Capability? Capability,
    ArtifactId? Artifact,
    CoverageStatus Status,
    IReadOnlyList<CoverageImpact> Impacts,
    CoverageBoundary? Boundary = null,
    RuleRef? Rule = null,
    IReadOnlyList<string>? Claims = null);
