using DumpDetective.Sdk.Artifacts;

using DumpDetective.Orchestration.Session;

namespace DumpDetective.Orchestration.Discovery;

/// <summary>
/// Turns a session's capability resolution into <see cref="CoverageRecord"/>s. Driven by the session's
/// artifacts, never by artifact kind, so a dump, a trace or both are treated alike: every capability an
/// artifact provides is <see cref="CoverageStatus.Complete"/> for that artifact, and every capability an
/// analyzer declared but no artifact provides is <see cref="CoverageStatus.Unavailable"/>, or <see cref="CoverageStatus.NotSupplied"/>
/// when the session has no artifact of that capability's family at all.
/// </summary>
public static class CoverageRecordBuilder
{
    public static IReadOnlyList<CoverageRecord> Build(AnalysisSession session, CapabilityResolution resolution)
    {
        List<CoverageRecord> records = [];
        HashSet<string> providedFamilies = new(StringComparer.Ordinal);

        foreach (ArtifactDescriptor artifact in session.Artifacts)
        {
            foreach (Capability capability in artifact.Provides.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                providedFamilies.Add(FamilyOf(capability.Key));
                records.Add(new CoverageRecord(capability, artifact.Id, CoverageStatus.Complete, []));
            }
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
            records.Add(new CoverageRecord(new Capability(missing.Key), null, StatusOfMissing(missing.Key, providedFamilies), missing.Value));

        return records;
    }

    // A family is the part of a capability key before the first dot ("trace" in "trace.gc-events"). When no artifact provides
    // anything in the family, the session never supplied that kind of artifact, which is not the same as a supplied one lacking it.
    private static string FamilyOf(string capabilityKey)
    {
        int dot = capabilityKey.IndexOf('.');
        return dot < 0 ? capabilityKey : capabilityKey[..dot];
    }

    private static CoverageStatus StatusOfMissing(string capabilityKey, HashSet<string> providedFamilies) =>
        providedFamilies.Contains(FamilyOf(capabilityKey)) ? CoverageStatus.Unavailable : CoverageStatus.NotSupplied;

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
