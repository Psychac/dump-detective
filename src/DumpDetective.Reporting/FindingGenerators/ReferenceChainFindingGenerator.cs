using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class ReferenceChainFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Reference Chain Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is ReferenceChainDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not ReferenceChainDomainResult r) return [];

        var findings = new List<InsightFinding>(capacity: 2);

        if (r.AnalyzedSamples == 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: FindingSeverity.Info,
                Title: "No sample instances available for reference-chain tracing",
                Evidence: "Reference-chain analyzer could not obtain valid sample objects for configured top types.",
                Recommendation: "Review type statistics and dump integrity; re-run with broader type coverage if needed.",
                Tags: ["reference-chain", "roots", "retention"],
                MetricValue: 0,
                MetricUnit: "% retained-samples"));
            return findings;
        }

        FindingSeverity severity = r.RetainedPercent >= 70 ? FindingSeverity.Warning : FindingSeverity.Info;
        findings.Add(new InsightFinding(
            Analyzer: AnalyzerName,
            Category: "Retention",
            Severity: severity,
            Title: "Reference-chain retention coverage",
            Evidence: $"{r.RetainedSamples:N0}/{r.AnalyzedSamples:N0} sampled top types had at least one GC-root path ({r.RetainedPercent:F1}%).",
            Recommendation: "Focus on root paths for retained top types to identify ownership leaks.",
            Tags: ["reference-chain", "gc-roots", "retention"],
            MetricValue: r.RetainedPercent,
            MetricUnit: "% retained-samples"));

        // Traversal-limit finding derived from the typed result property.
        if (r.TraversalLimitedSamples > 0)
        {
            long limitedSamples = r.TraversalLimitedSamples;
            double limitedPct = r.AnalyzedSamples == 0 ? 0 : limitedSamples * 100.0 / r.AnalyzedSamples;
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: limitedPct >= 20 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Reference-chain traversal limit reached",
                Evidence: $"{limitedSamples:N0}/{r.AnalyzedSamples:N0} sampled type(s) hit traversal limits before a conclusive root-path result ({limitedPct:F1}%).",
                Recommendation: "Increase sampling depth/path budget for inconclusive types and validate with targeted object tracing.",
                Tags: ["reference-chain", "traversal-limit", "retention"],
                MetricValue: limitedPct,
                MetricUnit: "% traversal-limited-samples"));
        }

        // E-5 (docs/analysis/phase1/reference-chain-analyzer-audit.md): types where every
        // retained sample rooted exclusively via the finalizer queue — a distinct leak pattern
        // implying the finalizer thread is stalled/blocked or the finalizer isn't running.
        IReadOnlyList<ReferenceTypeSampleSnapshot> traces = r.TopTypeSampleTraces ?? [];
        var finalizerOnlyTypes = new List<ReferenceTypeSampleSnapshot>();
        for (int i = 0; i < traces.Count; i++)
        {
            ReferenceTypeSampleSnapshot trace = traces[i];
            if (trace.RetainedSampleCount > 0
                && trace.DominantSampleRootKind == "Finalizer"
                && trace.DominantSampleRootKindCount == trace.RetainedSampleCount)
            {
                finalizerOnlyTypes.Add(trace);
            }
        }

        if (finalizerOnlyTypes.Count > 0)
        {
            const int PopulationWarningThreshold = 100;
            bool anyAtScale = false;
            var typeDescriptions = new List<string>(finalizerOnlyTypes.Count);
            for (int i = 0; i < finalizerOnlyTypes.Count; i++)
            {
                ReferenceTypeSampleSnapshot trace = finalizerOnlyTypes[i];
                anyAtScale |= trace.Count >= PopulationWarningThreshold;
                typeDescriptions.Add($"{trace.TypeName} ({trace.Count:N0} instances, {trace.RetainedSampleCount}/{trace.SampleCount} samples)");
            }

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: anyAtScale ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Types exclusively retained via finalizer queue",
                Evidence: $"{finalizerOnlyTypes.Count:N0} type(s) had every sampled retained instance rooted only through the finalizer queue: {string.Join(", ", typeDescriptions)}.",
                Recommendation: "Investigate whether the finalizer thread is blocked or the finalizer queue is backing up; these objects are pending finalization rather than being retained by application logic.",
                Tags: ["reference-chain", "finalizer", "retention"],
                MetricValue: finalizerOnlyTypes.Count,
                MetricUnit: "finalizer-only-types"));
        }

        // E-6 (docs/analysis/phase1/reference-chain-analyzer-audit.md): root addresses shared by
        // multiple top types' representative samples — likely retention hubs (shared caches,
        // singletons) rather than independent leaks.
        IReadOnlyList<ReferenceChainSharedRootGroup> sharedRootGroups = r.SharedRootGroups ?? [];
        if (sharedRootGroups.Count > 0)
        {
            const int TypeCountWarningThreshold = 3;
            bool anyAtScale = false;
            var groupDescriptions = new List<string>(sharedRootGroups.Count);
            for (int i = 0; i < sharedRootGroups.Count; i++)
            {
                ReferenceChainSharedRootGroup group = sharedRootGroups[i];
                anyAtScale |= group.TypeNames.Count >= TypeCountWarningThreshold;
                groupDescriptions.Add($"0x{group.RootAddress:X} ({group.RootKind}) shared by {string.Join(", ", group.TypeNames)}");
            }

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: anyAtScale ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "Shared root retention hubs across top types",
                Evidence: $"{sharedRootGroups.Count:N0} root object(s) retain more than one sampled top type: {string.Join("; ", groupDescriptions)}.",
                Recommendation: "Inspect the shared root object (e.g. a static cache or singleton) — releasing or bounding it may resolve retention for all associated types at once.",
                Tags: ["reference-chain", "shared-root", "retention"],
                MetricValue: sharedRootGroups.Count,
                MetricUnit: "shared-root-groups"));
        }

        return findings;
    }
}
