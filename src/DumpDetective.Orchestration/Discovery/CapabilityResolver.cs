using System.Reflection;

using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Orchestration.Discovery;

/// <summary>
/// Cross-references each candidate analyzer's declared <see cref="RequiresCapabilityAttribute"/>
/// against a session's <see cref="AnalysisSession.AvailableCapabilities"/> — see
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 2.
/// </summary>
/// <remarks>
/// Works over <see cref="IAnalyzer"/> instances and reflects on their declared attributes directly,
/// rather than over <c>Reporting.Capabilities.AnalyzerFeatureModule</c> — that type carries
/// report-wiring fields (FindingGenerator/TrendComparer/SectionBuilder types) this resolver has no
/// need of, and depending on it would violate this project's Sdk/Platform-only dependency
/// direction (<c>DependencyDirectionTests.OrchestrationProject_ShouldDependOnSdkAndPlatformOnly</c>).
/// </remarks>
public static class CapabilityResolver
{
    public static CapabilityResolution Resolve(
        IReadOnlyList<IAnalyzer> candidates, IReadOnlySet<Capability> availableCapabilities)
    {
        List<IAnalyzer> satisfied = [];
        List<UnsatisfiedAnalyzer> unsatisfied = [];

        foreach (IAnalyzer candidate in candidates)
        {
            IReadOnlyList<string> required =
                candidate.GetType().GetCustomAttribute<RequiresCapabilityAttribute>()?.Capabilities ?? [];

            string[] missing = required
                .Where(capability => !availableCapabilities.Contains(new Capability(capability)))
                .ToArray();

            if (missing.Length == 0)
                satisfied.Add(candidate);
            else
                unsatisfied.Add(new UnsatisfiedAnalyzer(candidate, missing));
        }

        return new CapabilityResolution(satisfied, unsatisfied);
    }
}

/// <summary>
/// Result of <see cref="CapabilityResolver.Resolve"/>: analyzers whose declared requirements are
/// fully met by the session, and analyzers that were dropped along with the capabilities they were
/// missing.
/// </summary>
public sealed record CapabilityResolution(
    IReadOnlyList<IAnalyzer> Satisfied,
    IReadOnlyList<UnsatisfiedAnalyzer> Unsatisfied);

public sealed record UnsatisfiedAnalyzer(IAnalyzer Analyzer, IReadOnlyList<string> MissingCapabilities);
