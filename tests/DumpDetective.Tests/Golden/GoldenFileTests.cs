using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Tests.Golden.Fixtures;

using Xunit;

namespace DumpDetective.Tests.Golden;

public sealed class GoldenFileTests
{
    public static IEnumerable<object[]> GoldenCases()
    {
        string[] fixtures = ["BaselineSmall", "DuplicateHeavy", "LongNames", "RichEvidence", "MixedSeverity"];
        foreach (string fixture in fixtures)
        {
            yield return [fixture, 0, "Text", $"{fixture}.text.golden"];
            yield return [fixture, 1, "Markdown", $"{fixture}.markdown.golden"];
            yield return [fixture, 2, "Json", $"{fixture}.json.golden"];   // H1: JSON golden instead of HTML string
        }
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void Fixture_ShouldMatchGolden(string fixtureName, int formatCode, string folder, string fileName)
    {
        AnalysisReportDocument fixture = GoldenReportFixtures.Build(fixtureName);
        IReportFormatter formatter = formatCode switch
        {
            0 => new TextCanonicalReportFormatter(),
            1 => new MarkdownCanonicalReportFormatter(),
            2 => new JsonCanonicalReportFormatter(),   // H1: JSON formatter for stable HTML-layout-independent golden
            _ => throw new NotSupportedException($"Unsupported format code {formatCode}")
        };

        string actual = formatter.Render(fixture);
        string baselinePath = Path.Combine(AppContext.BaseDirectory, "Golden", "Baselines", folder, fileName);

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(baselinePath, actual);
            return;
        }

        string expected = File.ReadAllText(baselinePath);
        GoldenFileAssert.AssertMatches(expected, actual);
    }
}

