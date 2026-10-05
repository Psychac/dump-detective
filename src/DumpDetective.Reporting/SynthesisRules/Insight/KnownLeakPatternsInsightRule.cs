using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectKnownLeakPatterns</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
/// <remarks>
/// Sourced from <c>MemoryAnalyzer</c>'s new bounded, known-vocabulary
/// <c>type.known-pattern-population</c> observation (see its own remarks) — same shape as
/// <c>infra.timer-population</c>'s <c>TimerWrapperTypeNames</c>, not the unbounded full-<c>TopTypes</c>
/// population.
/// </remarks>
internal sealed class KnownLeakPatternsInsightRule : ISynthesisRule
{
    private const long TdsClosureThreshold = 1_000;
    private const long ReflectionThreshold = 50_000;

    private static readonly HashSet<string> ReflectionMetadataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Reflection.RuntimeMethodInfo",
        "System.Reflection.RuntimePropertyInfo",
        "System.Reflection.RuntimeFieldInfo",
        "System.Reflection.RuntimeConstructorInfo",
    };

    public string RuleId => "insight.known-leak-patterns";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["type.known-pattern-population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        long tdsClosureCount = 0;
        long reflectionCount = 0;
        var tdsDerivedFrom = new List<ObservationId>();
        var reflectionDerivedFrom = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
            long instanceCount = (long)observation.Measures["instance.count"].Value;

            // TdsParser closure accumulation — known .NET Framework 4.x System.Data.SqlClient issue.
            // Async SqlCommand continuations capture closures that linger when connections are not
            // disposed promptly.
            if (typeName.Contains("TdsParser", StringComparison.OrdinalIgnoreCase) &&
                (typeName.Contains("DisplayClass", StringComparison.OrdinalIgnoreCase) ||
                 typeName.Contains("c__", StringComparison.OrdinalIgnoreCase) ||
                 typeName.Contains("<>", StringComparison.OrdinalIgnoreCase)))
            {
                tdsClosureCount += instanceCount;
                tdsDerivedFrom.Add(observation.Id);
            }
            else if (ReflectionMetadataTypes.Contains(typeName))
            {
                reflectionCount += instanceCount;
                reflectionDerivedFrom.Add(observation.Id);
            }
        }

        var findings = new List<Claim>(2);

        if (tdsClosureCount >= TdsClosureThreshold)
        {
            findings.Add(new Claim
            {
                Fingerprint = "insightengine::sqlclient-tdsparser-closure",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "SqlClient TdsParser closure accumulation — known ADO.NET pattern",
                Narrative = $"{tdsClosureCount:N0} TdsParser compiler-generated closure object(s) on heap. " +
                            "In .NET Framework System.Data.SqlClient, async SqlCommand continuations can accumulate " +
                            "closures when connections are not disposed promptly or when async paths are abandoned.",
                Recommendation = "Upgrade to Microsoft.Data.SqlClient NuGet package which has resolved this issue. " +
                                 "Ensure SqlConnection and SqlCommand are disposed immediately after use via using statements. " +
                                 "Avoid fire-and-forget async ADO.NET operations on .NET Framework.",
                Tags = ["ado-net", "sqlclient", "closure", "memory-leak", "known-pattern"],
                DerivedFrom = tdsDerivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // Reflection metadata accumulation: RuntimeMethodInfo / RuntimePropertyInfo > 50 k
        // indicates hot-path reflection (Type.GetMethod / GetProperty) without result caching.
        if (reflectionCount >= ReflectionThreshold)
        {
            findings.Add(new Claim
            {
                Fingerprint = "insightengine::uncached-reflection-metadata",
                Analyzer = "InsightEngine",
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Uncached reflection metadata accumulation detected",
                Narrative = $"{reflectionCount:N0} RuntimeMethodInfo/RuntimePropertyInfo/RuntimeFieldInfo object(s) on heap. " +
                            "Each call to Type.GetMethod(), GetProperty(), or GetField() allocates a new metadata wrapper " +
                            "unless results are cached.",
                Recommendation = "Cache reflection results in static dictionaries keyed by Type. " +
                                 "Use compiled Expression trees or source generators (System.Text.Json, Mapster) " +
                                 "to replace runtime reflection in hot paths.",
                Tags = ["reflection", "memory-leak", "performance", "known-pattern"],
                DerivedFrom = reflectionDerivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }
}
