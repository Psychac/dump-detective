using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Architecture;

public sealed class FitnessEnforcementTests
{
    [Fact]
    public void AnalysisProject_ShouldNotReferenceReportingNamespaces()
    {
        string repoRoot = FindRepositoryRoot();
        string analysisRoot = Path.Combine(repoRoot, "src", "DumpDetective.Analysis");

        IReadOnlyList<string> offenders = FindNamespaceReferences(
            analysisRoot,
            forbiddenPrefixes: ["using DumpDetective.Reporting", "DumpDetective.Reporting."]);

        offenders.Should().BeEmpty("Analysis must not source-link to Reporting implementation namespaces.");
    }

    [Fact]
    public void CoreProject_ShouldNotReferenceAnalysisReportingOrCliNamespaces()
    {
        string repoRoot = FindRepositoryRoot();
        string coreRoot = Path.Combine(repoRoot, "src", "DumpDetective.Core");

        IReadOnlyList<string> offenders = FindNamespaceReferences(
            coreRoot,
            forbiddenPrefixes:
            [
                "using DumpDetective.Analysis",
                "using DumpDetective.Reporting",
                "using DumpDetective.Cli",
                "DumpDetective.Analysis.",
                "DumpDetective.Reporting.",
                "DumpDetective.Cli.",
            ]);

        offenders.Should().BeEmpty("Core must not take dependencies on higher-level layers.");
    }

    [Fact]
    public void SectionBuilders_ShouldNotDecideLeadFindingsOrSeverity()
    {
        string repoRoot = FindRepositoryRoot();
        string sectionBuildersRoot = Path.Combine(repoRoot, "src", "DumpDetective.Reporting", "SectionBuilders");

        IReadOnlyList<string> offenders = FindNamespaceReferences(
            sectionBuildersRoot,
            forbiddenPrefixes: ["new SectionLeadFinding(", "Severity: \""]);

        offenders.Should().BeEmpty(
            "section builders are presentation-only: the lead finding is derived from the top " +
            "synthesized finding in ReportSectionAssembler.NormalizeSectionContractSlots, never " +
            "constructed or severity-classified by a builder.");
    }

    // Section builders still computing their own confidence band or tiered interpretation — each a
    // known, open exception to the Phase 5 "presentation-only" exit criterion (see
    // docs/refactor/modularity/phase-5-observations-synthesis.md), listed so the set can only shrink.
    private static readonly HashSet<string> KnownBuilderJudgmentFiles = new(StringComparer.Ordinal)
    {
        "ConfidenceSectionBuilder.cs",
        "DominatorSectionBuilder.cs",
        "GCRootSectionBuilder.cs",
        "LeakCandidateSectionBuilder.cs",
        "StringSectionBuilder.cs",
    };

    /// <summary>
    /// A text search can't tell a severity threshold from a byte-unit loop (step 6a found ~99
    /// numeric comparisons across the builders, nearly all display), so this bans the three shapes
    /// judgment has actually taken in builders instead: a named threshold constant, the
    /// <c>ConfidenceScoring</c> penalty model, and <c>Interpret</c> tier banding.
    /// </summary>
    [Fact]
    public void SectionBuilders_ShouldNotDeclareThresholdsOrScoreConfidence()
    {
        string repoRoot = FindRepositoryRoot();
        string sectionBuildersRoot = Path.Combine(repoRoot, "src", "DumpDetective.Reporting", "SectionBuilders");
        var thresholdConstant = new System.Text.RegularExpressions.Regex(@"const\s+\w+\s+\w*(Threshold|Critical|Warning|Band)\w*\s*=");

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(sectionBuildersRoot, "*.cs", SearchOption.AllDirectories))
        {
            bool knownException = KnownBuilderJudgmentFiles.Contains(Path.GetFileName(file));
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                bool scoresConfidence = line.Contains("ConfidenceScoring.", StringComparison.Ordinal)
                    || (line.Contains("Interpret(", StringComparison.Ordinal) && !line.Contains("protected static", StringComparison.Ordinal));
                if (thresholdConstant.IsMatch(line) || (scoresConfidence && !knownException))
                    offenders.Add($"{file}:{i + 1}: {line}");
            }
        }

        offenders.Should().BeEmpty(
            "thresholds, confidence penalties and tier banding belong in synthesis rules or " +
            "analyzers; section builders only shape already-decided findings and measures.");
    }

    [Fact]
    public void HotspotGuardrailTests_ShouldExistForCriticalPaths()
    {
        string repoRoot = FindRepositoryRoot();

        string[] requiredTests =
        [
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "Integration", "P0SmokeTests.cs"),
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "Integration", "ProgramEntryPointTests.cs"),
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "Integration", "HtmlRendererCssTests.cs"),
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "ReportingVisualsTests.cs"),
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "Unit", "Hosting", "ServiceRegistrationTests.cs"),
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "Unit", "Architecture", "DependencyDirectionTests.cs"),
        ];

        foreach (string path in requiredTests)
        {
            File.Exists(path).Should().BeTrue($"Expected hotspot guardrail test file: {path}");
        }
    }

    private static IReadOnlyList<string> FindNamespaceReferences(string root, IReadOnlyList<string> forbiddenPrefixes)
    {
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                for (int p = 0; p < forbiddenPrefixes.Count; p++)
                {
                    if (line.Contains(forbiddenPrefixes[p], StringComparison.Ordinal))
                    {
                        offenders.Add($"{file}:{i + 1}: {line}");
                        break;
                    }
                }
            }
        }

        return offenders;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while (current is not null)
        {
            string slnxPath = Path.Combine(current.FullName, "DumpDetective.slnx");
            if (File.Exists(slnxPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root containing DumpDetective.slnx.");
    }
}
