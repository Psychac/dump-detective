using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Reporting.Synthesis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.DependencyInjection;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 5 step 7's real-dump check (docs/refactor/modularity/phase-5-observations-synthesis.md):
/// over one analyzer run on the reference dump, compares every finding's report confidence as its
/// rule alone judged it (the parity baseline) with what <see cref="SynthesisEngine"/> reports after
/// <see cref="ConfidenceCalculator"/> folds in the observation lineage — so the calculation's
/// intended output change is a measured list, not an estimate.
/// </summary>
public sealed class FindingConfidenceRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public FindingConfidenceRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task LineageConfidence_RealDump_ReportsEveryFindingWhoseScoreMoves()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(runtime.Heap, dumpPath, CancellationToken.None, progress: null);

        var sink = new CollectingObservationSink();
        RuntimeAnalysisContext context = new()
        {
            Runtime = runtime,
            Cache = cache,
            ArtifactId = new ArtifactId(dumpPath),
            ObservationSink = sink,
            AnalysisOptions = new(),
        };

        var pipeline = new AnalysisPipeline(new DefaultAnalyzerFactory().CreateAnalyzers(), new FindingGenerationPipeline([]));
        IReadOnlyList<AnalyzerRunResult> runs = await pipeline.ExecuteAsync(context, CancellationToken.None);
        IReadOnlyList<Observation> observations = sink.Observations;

        IReadOnlyList<ISynthesisRule> rules = CreateProductionRules();
        int failedRuns = runs.Count(r => r.Status == AnalyzerExecutionStatus.Failed);
        var synthesisContext = new SynthesisContext { AnalyzerRunSummary = (runs.Count, failedRuns) };

        IReadOnlyList<Finding> withLineage = await new SynthesisEngine(rules).SynthesizeAsync(observations, synthesisContext);
        List<Finding> ruleOnly = await SynthesizeWithoutLineageAsync(rules, observations, synthesisContext);

        Assert.Equal(ruleOnly.Count, withLineage.Count);

        var ruleOnlyByKey = new Dictionary<(string, int), Finding>();
        foreach (var (key, finding) in KeyByOccurrence(ruleOnly))
            ruleOnlyByKey[key] = finding;

        var lines = new List<string>
        {
            $"Dump: {dumpPath}",
            $"Analyzer runs: {runs.Count} ({failedRuns} failed); observations: {observations.Count:N0}",
            $"Degraded observations: {observations.Count(o => o.Provenance.Fidelity == FidelityLevel.Degraded):N0}; " +
            $"observations below confidence 1.0: {observations.Count(o => o.Confidence < 1.0):N0}",
            $"Findings: {withLineage.Count}",
        };

        int moved = 0;
        var movedLines = new List<string>();
        foreach (var (key, after) in KeyByOccurrence(withLineage))
        {
            Finding before = ruleOnlyByKey[key];
            double? beforeScore = FindingSynthesisAdapter.ToInsightFinding(before).ConfidenceScore;
            double? afterScore = FindingSynthesisAdapter.ToInsightFinding(after).ConfidenceScore;
            if (beforeScore == afterScore)
                continue;

            moved++;
            movedLines.Add($"  [{after.Severity}] {after.Analyzer} :: {after.Fingerprint}");
            movedLines.Add($"      report score {beforeScore:0.###} -> {afterScore:0.###} (rule composite {before.Confidence.Composite:0.###}, lineage {after.DerivedFrom.Count} obs)");
            foreach (string factor in after.Confidence.LimitingFactors)
                movedLines.Add($"      - {factor}");
        }

        lines.Add($"Findings whose report score moved: {moved}");
        lines.AddRange(movedLines);
        lines.AddRange(DescribeLineageGaps(withLineage, observations));

        string reportPath = Path.Combine(Path.GetTempPath(), "dd-finding-confidence-real-dump.txt");
        await File.WriteAllLinesAsync(reportPath, lines);
        lines.Add($"(also written to {reportPath})");
        foreach (string line in lines)
            _output.WriteLine(line);
    }

    // Exit criterion "full observation lineage": every finding resolves to observations, and each
    // of those to an artifact, an analyzer, and the capabilities it used.
    private static List<string> DescribeLineageGaps(IReadOnlyList<Finding> findings, IReadOnlyList<Observation> observations)
    {
        var byId = new Dictionary<ObservationId, Observation>(observations.Count);
        foreach (Observation observation in observations)
            byId[observation.Id] = observation;

        var gaps = new List<string>();
        int fullyResolved = 0;
        foreach (Finding finding in findings)
        {
            int unresolved = 0, missingProvenance = 0;
            foreach (ObservationId id in finding.DerivedFrom)
            {
                if (!byId.TryGetValue(id, out Observation? observation))
                    unresolved++;
                else if (string.IsNullOrEmpty(observation.Provenance.Artifact.ToString())
                         || string.IsNullOrEmpty(observation.Provenance.AnalyzerKey)
                         || observation.Provenance.CapabilitiesUsed.Count == 0)
                    missingProvenance++;
            }

            if (finding.DerivedFrom.Count > 0 && unresolved == 0 && missingProvenance == 0)
            {
                fullyResolved++;
                continue;
            }

            gaps.Add($"  {finding.Analyzer} :: {finding.Fingerprint} — lineage {finding.DerivedFrom.Count}, " +
                     $"unresolved {unresolved}, missing provenance {missingProvenance}");
        }

        gaps.Insert(0, $"Lineage: {fullyResolved} of {findings.Count} findings fully resolve to artifact/analyzer/capabilities; gaps:");
        return gaps;
    }

    private static IReadOnlyList<ISynthesisRule> CreateProductionRules()
    {
        ServiceCollection services = new();
        services.AddSingleton<ISynthesisRuleCatalog, DefaultSynthesisRuleCatalog>();
        services.AddSingleton<ISynthesisRuleFactory, DefaultSynthesisRuleFactory>();
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ISynthesisRuleFactory>().CreateRules();
    }

    private static async Task<List<Finding>> SynthesizeWithoutLineageAsync(
        IReadOnlyList<ISynthesisRule> rules, IReadOnlyList<Observation> observations, SynthesisContext context)
    {
        var findings = new List<Finding>();
        foreach (ISynthesisRule rule in rules)
        {
            var matched = observations.Where(o => rule.Query.Matches(o)).ToList();
            try
            {
                findings.AddRange(await rule.SynthesizeAsync(new ObservationMatchSet(matched), context));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Mirrors SynthesisEngine's per-rule isolation, so both sides drop the same rules.
            }
        }

        findings.Sort((a, b) => string.CompareOrdinal(a.Fingerprint, b.Fingerprint));
        return findings;
    }

    private static IEnumerable<((string Fingerprint, int Occurrence) Key, Finding Finding)> KeyByOccurrence(IReadOnlyList<Finding> findings)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Finding finding in findings)
        {
            seen.TryGetValue(finding.Fingerprint, out int occurrence);
            seen[finding.Fingerprint] = occurrence + 1;
            yield return ((finding.Fingerprint, occurrence), finding);
        }
    }
}
