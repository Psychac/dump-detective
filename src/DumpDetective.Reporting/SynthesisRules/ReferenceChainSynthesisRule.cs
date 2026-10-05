using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>ReferenceChainFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Trusts emission order for both list-shaped findings, matching the old generator's own
/// no-sort filter-in-place loops: <c>reference-chain.type-sample</c> observations are matched in
/// <c>TopTypeSampleTraces</c>'s order (sorted descending by type size before either emission or the
/// old generator's loop ever runs), and <c>reference-chain.shared-root-group</c> observations are
/// matched in <c>BuildSharedRootGroups</c>'s own pre-sorted (type-count desc, then address asc)
/// order — same already-sorted-before-emission structure <c>ArraySynthesisRule</c> relies on.
/// </remarks>
internal sealed class ReferenceChainSynthesisRule : ISynthesisRule
{
    private const double RetentionCoverageWarningPercent = 70;
    private const double TraversalLimitedWarningPercent = 20;
    private const int FinalizerOnlyPopulationWarningThreshold = 100;
    private const int SharedRootTypeCountWarningThreshold = 3;

    private const string AnalyzerName = "Reference Chain Analysis";

    public string RuleId => "reference-chain.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["reference-chain.type-sample", "reference-chain.population", "reference-chain.shared-root-group"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        var typeSamples = new List<Observation>();
        var sharedRootGroups = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "reference-chain.population":
                    population = observation;
                    break;
                case "reference-chain.type-sample":
                    typeSamples.Add(observation);
                    break;
                case "reference-chain.shared-root-group":
                    sharedRootGroups.Add(observation);
                    break;
            }
        }

        if (population is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long analyzedSamples = (long)population.Measures["analyzed.count"].Value;

        if (analyzedSamples == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
            {
                Fingerprint = Fingerprint("no-samples"),
                Analyzer = AnalyzerName,
                Category = "Retention",
                Severity = Severity.Info,
                Title = "No sample instances available for reference-chain tracing",
                Narrative = "Reference-chain analyzer could not obtain valid sample objects for configured top types.",
                Recommendation = "Review type statistics and dump integrity; re-run with broader type coverage if needed.",
                Tags = ["reference-chain", "roots", "retention"],
                MetricValue = 0,
                MetricUnit = "% retained-samples",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            }]);
        }

        long retainedSamples = (long)population.Measures["retained.count"].Value;
        double retainedPercent = population.Measures["retained.percent"].Value;
        long traversalLimitedSamples = (long)population.Measures["traversal-limited.count"].Value;

        var findings = new List<Claim>(4);

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("retention-coverage"),
            Analyzer = AnalyzerName,
            Category = "Retention",
            Severity = retainedPercent >= RetentionCoverageWarningPercent ? Severity.Warning : Severity.Info,
            Title = "Reference-chain retention coverage",
            Narrative = $"{retainedSamples:N0}/{analyzedSamples:N0} sampled top types had at least one GC-root path ({retainedPercent:F1}%).",
            Recommendation = "Focus on root paths for retained top types to identify ownership leaks.",
            Tags = ["reference-chain", "gc-roots", "retention"],
            MetricValue = retainedPercent,
            MetricUnit = "% retained-samples",
            DerivedFrom = [population.Id],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });

        if (traversalLimitedSamples > 0)
        {
            double limitedPct = analyzedSamples == 0 ? 0 : traversalLimitedSamples * 100.0 / analyzedSamples;
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("traversal-limit"),
                Analyzer = AnalyzerName,
                Category = "Retention",
                Severity = limitedPct >= TraversalLimitedWarningPercent ? Severity.Warning : Severity.Info,
                Title = "Reference-chain traversal limit reached",
                Narrative = $"{traversalLimitedSamples:N0}/{analyzedSamples:N0} sampled type(s) hit traversal limits before a conclusive root-path result ({limitedPct:F1}%).",
                Recommendation = "Increase sampling depth/path budget for inconclusive types and validate with targeted object tracing.",
                Tags = ["reference-chain", "traversal-limit", "retention"],
                MetricValue = limitedPct,
                MetricUnit = "% traversal-limited-samples",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        Claim? finalizerFinding = BuildFinalizerOnlyFinding(typeSamples);
        if (finalizerFinding is not null) findings.Add(finalizerFinding);

        Claim? sharedRootFinding = BuildSharedRootFinding(sharedRootGroups);
        if (sharedRootFinding is not null) findings.Add(sharedRootFinding);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static Claim? BuildFinalizerOnlyFinding(IReadOnlyList<Observation> typeSamples)
    {
        var finalizerOnly = new List<Observation>();
        foreach (Observation sample in typeSamples)
        {
            long retainedSampleCount = (long)sample.Measures["retained-sample.count"].Value;
            long dominantRootKindCount = (long)sample.Measures["dominant-root-kind.count"].Value;
            string? dominantRootKind = sample.Evidence.Count > 0 ? sample.Evidence[0].Description : null;

            if (retainedSampleCount > 0
                && string.Equals(dominantRootKind, "Finalizer", StringComparison.Ordinal)
                && dominantRootKindCount == retainedSampleCount)
            {
                finalizerOnly.Add(sample);
            }
        }

        if (finalizerOnly.Count == 0) return null;

        bool anyAtScale = false;
        var typeDescriptions = new List<string>(finalizerOnly.Count);
        var derivedFrom = new List<ObservationId>(finalizerOnly.Count);
        foreach (Observation sample in finalizerOnly)
        {
            string typeName = ((TypeRef)sample.Subjects[0]).CanonicalName;
            long instanceCount = (long)sample.Measures["instance.count"].Value;
            long sampleCount = (long)sample.Measures["sample.count"].Value;
            long retainedSampleCount = (long)sample.Measures["retained-sample.count"].Value;

            anyAtScale |= instanceCount >= FinalizerOnlyPopulationWarningThreshold;
            typeDescriptions.Add($"{typeName} ({instanceCount:N0} instances, {retainedSampleCount}/{sampleCount} samples)");
            derivedFrom.Add(sample.Id);
        }

        return new Claim
        {
            Fingerprint = Fingerprint("finalizer-only-retention"),
            Analyzer = AnalyzerName,
            Category = "Retention",
            Severity = anyAtScale ? Severity.Warning : Severity.Info,
            Title = "Types exclusively retained via finalizer queue",
            Narrative = $"{finalizerOnly.Count:N0} type(s) had every sampled retained instance rooted only through the finalizer queue: {string.Join(", ", typeDescriptions)}.",
            Recommendation = "Investigate whether the finalizer thread is blocked or the finalizer queue is backing up; these objects are pending finalization rather than being retained by application logic.",
            Tags = ["reference-chain", "finalizer", "retention"],
            MetricValue = finalizerOnly.Count,
            MetricUnit = "finalizer-only-types",
            DerivedFrom = derivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        };
    }

    private static Claim? BuildSharedRootFinding(IReadOnlyList<Observation> sharedRootGroups)
    {
        if (sharedRootGroups.Count == 0) return null;

        bool anyAtScale = false;
        var groupDescriptions = new List<string>(sharedRootGroups.Count);
        var derivedFrom = new List<ObservationId>(sharedRootGroups.Count);
        foreach (Observation group in sharedRootGroups)
        {
            ulong rootAddress = ((ObjectRef)group.Subjects[0]).Address;
            long typeCount = (long)group.Measures["type.count"].Value;
            string rootKind = group.Evidence[0].Description ?? string.Empty;
            string typeNames = group.Evidence[1].Description ?? string.Empty;

            anyAtScale |= typeCount >= SharedRootTypeCountWarningThreshold;
            groupDescriptions.Add($"0x{rootAddress:X} ({rootKind}) shared by {typeNames}");
            derivedFrom.Add(group.Id);
        }

        return new Claim
        {
            Fingerprint = Fingerprint("shared-root-retention"),
            Analyzer = AnalyzerName,
            Category = "Retention",
            Severity = anyAtScale ? Severity.Warning : Severity.Info,
            Title = "Shared root retention hubs across top types",
            Narrative = $"{sharedRootGroups.Count:N0} root object(s) retain more than one sampled top type: {string.Join("; ", groupDescriptions)}.",
            Recommendation = "Inspect the shared root object (e.g. a static cache or singleton) — releasing or bounding it may resolve retention for all associated types at once.",
            Tags = ["reference-chain", "shared-root", "retention"],
            MetricValue = sharedRootGroups.Count,
            MetricUnit = "shared-root-groups",
            DerivedFrom = derivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        };
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
