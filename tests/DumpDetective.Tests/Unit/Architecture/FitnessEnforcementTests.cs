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
    public void HotspotGuardrailTests_ShouldExistForCriticalPaths()
    {
        string repoRoot = FindRepositoryRoot();

        string[] requiredTests =
        [
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "Integration", "P0SmokeTests.cs"),
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "Integration", "ProgramEntryPointTests.cs"),
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "Integration", "HtmlRendererCssTests.cs"),
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "ReportingVisualsTests.cs"),
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "Unit", "Architecture", "AnalyzerFeatureModuleSpikeTests.cs"),
            Path.Combine(repoRoot, "tests", "DumpDetective.Tests", "Unit", "Architecture", "DependencyDirectionTests.cs"),
        ];

        foreach (string path in requiredTests)
        {
            File.Exists(path).Should().BeTrue($"Expected hotspot guardrail test file: {path}");
        }
    }

    [Fact]
    public void BaselineHarness_ShouldExistForCiFitnessGate()
    {
        string repoRoot = FindRepositoryRoot();
        string baselineScript = Path.Combine(repoRoot, "tools", "Phase0", "Invoke-Phase0Baseline.ps1");
        File.Exists(baselineScript).Should().BeTrue();
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
