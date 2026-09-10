using System.Xml.Linq;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Architecture;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void RefactoredProjects_ShouldFollowStrictDependencyDirection()
    {
        string repoRoot = FindRepositoryRoot();

        IReadOnlyCollection<string> coreRefs = ReadProjectReferenceNames(Path.Combine(repoRoot, "src", "DumpDetective.Core", "DumpDetective.Core.csproj"));
        IReadOnlyCollection<string> analysisRefs = ReadProjectReferenceNames(Path.Combine(repoRoot, "src", "DumpDetective.Analysis", "DumpDetective.Analysis.csproj"));
        IReadOnlyCollection<string> reportingRefs = ReadProjectReferenceNames(Path.Combine(repoRoot, "src", "DumpDetective.Reporting", "DumpDetective.Reporting.csproj"));
        IReadOnlyCollection<string> cliRefs = ReadProjectReferenceNames(Path.Combine(repoRoot, "src", "DumpDetective.Cli", "DumpDetective.Cli.csproj"));

        coreRefs.Should().BeEmpty();
        // DumpDetective.Sdk added 2026-09-10: the Phase 1 retyping pilot's dump-side Tier-1
        // capability-surface implementations (Sdk/HeapTypeStatisticsQuery.cs) and the legacy
        // bridge adapter live in this project — see
        // docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
        analysisRefs.Should().Equal(["DumpDetective.Core", "DumpDetective.Platform", "DumpDetective.Sdk"]);
        // DumpDetective.Sdk added 2026-09-09: TraceSessionReport (the trace-only report.json
        // shape) belongs here, not in Cli — this project is the one that owns report-shape
        // contracts (see AnalysisReportDocument), the same reasoning that already puts
        // ReportOutputWriter's file-writing role in Cli instead.
        reportingRefs.Should().Equal(["DumpDetective.Analysis", "DumpDetective.Core", "DumpDetective.Sdk"]);
        // DumpDetective.Sources.NetTrace added 2026-09-09: the interim trace/dump router
        // (docs/refactor/modularity-plan.md § 8) needs Cli to reach the trace-only analysis path
        // directly, the same way it already reaches dump analysis through Analysis/Reporting.
        cliRefs.Should().Equal(["DumpDetective.Analysis", "DumpDetective.Core", "DumpDetective.Reporting", "DumpDetective.Sources.NetTrace"]);
    }

    [Fact]
    public void PlatformProject_ShouldDependOnSdkOnly()
    {
        // Phase 2, § 8-trimmed: "source-agnostic; references Sdk only" — see
        // docs/refactor/modularity/phase-2-artifact-platform.md. Platform must never pull in Core
        // (which carries the ClrMD package reference), Analysis, Reporting, or Cli.
        string repoRoot = FindRepositoryRoot();
        string platformProjectPath = Path.Combine(repoRoot, "src", "DumpDetective.Platform", "DumpDetective.Platform.csproj");

        IReadOnlyCollection<string> projectRefs = ReadProjectReferenceNames(platformProjectPath);

        projectRefs.Should().Equal(["DumpDetective.Sdk"]);
    }

    [Fact]
    public void SdkProject_ShouldHaveZeroDependenciesBeyondTheBcl()
    {
        // Phase 1 migration step 7: "Add SDK-boundary ... rules to the architecture test." The
        // whole point of the SDK is a contract surface any artifact source, any analyzer, any
        // consumer can target without pulling in the rest of the product — see
        // docs/refactor/modularity/phase-1-contracts-sdk.md.
        string repoRoot = FindRepositoryRoot();
        string sdkProjectPath = Path.Combine(repoRoot, "src", "DumpDetective.Sdk", "DumpDetective.Sdk.csproj");

        IReadOnlyCollection<string> projectRefs = ReadProjectReferenceNames(sdkProjectPath);
        IReadOnlyCollection<string> packageRefs = ReadPackageReferenceNames(sdkProjectPath);

        projectRefs.Should().BeEmpty("the SDK must not depend on any other project in this repo.");
        packageRefs.Should().BeEmpty("the SDK must not depend on any NuGet package — BCL only.");
    }

    private static IReadOnlyCollection<string> ReadPackageReferenceNames(string projectPath)
    {
        XDocument document = XDocument.Load(projectPath);

        return document
            .Descendants("PackageReference")
            .Select(r => (string?)r.Attribute("Include"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyCollection<string> ReadProjectReferenceNames(string projectPath)
    {
        XDocument document = XDocument.Load(projectPath);

        return document
            .Descendants("ProjectReference")
            .Select(r => (string?)r.Attribute("Include"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFileNameWithoutExtension(path!))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
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
