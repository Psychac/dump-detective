using DumpDetective.Sdk.Artifacts;

using DumpDetective.Orchestration.Session;

namespace DumpDetective.Orchestration.Discovery;

/// <summary>
/// Turns a session's capability resolution into <see cref="CoverageRecord"/>s. Driven by the session's
/// artifacts, never by artifact kind, so a dump, a trace or both are treated alike: every capability an
/// artifact provides is <see cref="CoverageStatus.Complete"/> for that artifact, and every capability an
/// analyzer declared but no artifact provides is <see cref="CoverageStatus.Unavailable"/>.
/// </summary>
public static class CoverageRecordBuilder
{
    public static IReadOnlyList<CoverageRecord> Build(AnalysisSession session, CapabilityResolution resolution)
    {
        List<CoverageRecord> records = [];

        foreach (ArtifactDescriptor artifact in session.Artifacts)
        {
            foreach (Capability capability in artifact.Provides.OrderBy(c => c.Key, StringComparer.Ordinal))
                records.Add(new CoverageRecord(capability, artifact.Id, CoverageStatus.Complete, []));
        }

        SortedDictionary<string, List<CoverageImpact>> impactsByMissingCapability = new(StringComparer.Ordinal);

        foreach (UnsatisfiedAnalyzer unsatisfied in resolution.Unsatisfied)
        {
            foreach (string capability in unsatisfied.MissingCapabilities)
            {
                AddImpact(impactsByMissingCapability, capability,
                    new CoverageImpact(unsatisfied.Analyzer.Name, CoverageImpactKind.NotRun));
            }
        }

        foreach (DegradedAnalyzer degraded in resolution.Degraded)
        {
            foreach (MissingOptionalCapability missing in degraded.MissingOptional)
            {
                AddImpact(impactsByMissingCapability, missing.Capability,
                    new CoverageImpact(degraded.Analyzer.Name, CoverageImpactKind.ReducedFidelity, missing.Fidelity));
            }
        }

        foreach (KeyValuePair<string, List<CoverageImpact>> missing in impactsByMissingCapability)
            records.Add(new CoverageRecord(new Capability(missing.Key), null, CoverageStatus.Unavailable, missing.Value));

        return records;
    }

    private static void AddImpact(SortedDictionary<string, List<CoverageImpact>> impacts, string capability, CoverageImpact impact)
    {
        if (!impacts.TryGetValue(capability, out List<CoverageImpact>? list))
        {
            list = [];
            impacts[capability] = list;
        }

        list.Add(impact);
    }
}
