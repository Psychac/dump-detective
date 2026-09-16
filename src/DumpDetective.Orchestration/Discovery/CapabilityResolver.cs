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
        List<DegradedAnalyzer> degraded = [];

        foreach (IAnalyzer candidate in candidates)
        {
            Type candidateType = candidate.GetType();

            IReadOnlyList<string> required =
                candidateType.GetCustomAttribute<RequiresCapabilityAttribute>()?.Capabilities ?? [];

            string[] missingRequired = required
                .Where(capability => !availableCapabilities.Contains(new Capability(capability)))
                .ToArray();

            if (missingRequired.Length > 0)
            {
                unsatisfied.Add(new UnsatisfiedAnalyzer(candidate, missingRequired));
                continue;
            }

            satisfied.Add(candidate);

            MissingOptionalCapability[] missingOptional = candidateType
                .GetCustomAttributes<OptionalCapabilityAttribute>()
                .Where(optional => !availableCapabilities.Contains(new Capability(optional.Capability)))
                .Select(optional => new MissingOptionalCapability(optional.Capability, optional.Fidelity))
                .ToArray();

            if (missingOptional.Length > 0)
                degraded.Add(new DegradedAnalyzer(candidate, missingOptional));
        }

        return new CapabilityResolution(satisfied, unsatisfied, degraded);
    }
}

/// <summary>
/// Result of <see cref="CapabilityResolver.Resolve"/>: analyzers whose declared requirements are
/// fully met by the session (<see cref="Satisfied"/>, a superset of <see cref="Degraded"/>'s
/// analyzers), analyzers that were dropped along with the capabilities they were missing
/// (<see cref="Unsatisfied"/>), and analyzers within <see cref="Satisfied"/> that will run at
/// reduced fidelity because a declared optional capability is unavailable (<see cref="Degraded"/>).
/// </summary>
public sealed record CapabilityResolution(
    IReadOnlyList<IAnalyzer> Satisfied,
    IReadOnlyList<UnsatisfiedAnalyzer> Unsatisfied,
    IReadOnlyList<DegradedAnalyzer> Degraded);

public sealed record UnsatisfiedAnalyzer(IAnalyzer Analyzer, IReadOnlyList<string> MissingCapabilities);

public sealed record DegradedAnalyzer(IAnalyzer Analyzer, IReadOnlyList<MissingOptionalCapability> MissingOptional);

public sealed record MissingOptionalCapability(string Capability, FidelityBoost Fidelity);
