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
        analysisRefs.Should().Equal(["DumpDetective.Core"]);
        reportingRefs.Should().Equal(["DumpDetective.Analysis", "DumpDetective.Core"]);
        cliRefs.Should().Equal(["DumpDetective.Analysis", "DumpDetective.Core", "DumpDetective.Reporting"]);
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
