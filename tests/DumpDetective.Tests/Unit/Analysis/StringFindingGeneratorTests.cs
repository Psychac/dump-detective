using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class StringFindingGeneratorTests
{
    private readonly StringFindingGenerator _gen = new();

    [Fact]
    public void Generate_DuplicationRatioAboveHalf_WithPatterns_EmitsCriticalFinding()
    {
        StringDomainResult result = BuildResult(duplicationRatio: 0.7, duplicatePatternCount: 50);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("allocation") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Generate_DuplicatePatternsPresent_RatioAtOrBelowHalf_EmitsWarningFinding()
    {
        StringDomainResult result = BuildResult(duplicationRatio: 0.3, duplicatePatternCount: 10);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title.Contains("High duplicate string pressure") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_NoDuplicatePatterns_EmitsNoDuplicationFinding()
    {
        StringDomainResult result = BuildResult(duplicationRatio: 0.0, duplicatePatternCount: 0);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Title.Contains("duplicat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_LohStringBytesPresent_EmitsWarningFinding()
    {
        StringDomainResult result = BuildResult(lohStringBytes: 5_000_000UL);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("loh") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_VeryLongStringsPresent_EmitsInfoFinding()
    {
        StringDomainResult result = BuildResult(veryLongStrings:
        [
            new LongStringEntry(Address: 0x2000, CharLength: 200_000, SizeBytes: 400_000UL)
        ]);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title == "Very long strings detected" && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_PctOfManagedHeapAboveTwenty_EmitsWarningFinding()
    {
        StringDomainResult result = BuildResult(pctOfManagedHeap: 35.0);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Title == "Strings dominate managed heap" && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_LowSamplingCoverage_EmitsInfoFinding()
    {
        StringDomainResult result = BuildResult(samplingCoverage: 0.02);

        var findings = _gen.Generate(result);

        findings.Should().Contain(f => f.Tags.Contains("coverage") && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Generate_ZeroSamplingCoverage_EmitsNoCoverageFinding()
    {
        StringDomainResult result = BuildResult(samplingCoverage: 0.0);

        var findings = _gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("coverage"));
    }

    [Fact]
    public void Generate_AllBelowThresholds_EmitsNoFindings()
    {
        StringDomainResult result = BuildResult();

        var findings = _gen.Generate(result);

        findings.Should().BeEmpty();
    }

    private static StringDomainResult BuildResult(
        double duplicationRatio = 0.0,
        int duplicatePatternCount = 0,
        ulong lohStringBytes = 0,
        IReadOnlyList<LongStringEntry>? veryLongStrings = null,
        double pctOfManagedHeap = 0.0,
        double samplingCoverage = 0.0) => new(
        TotalStrings: 100_000,
        TotalStringMemoryBytes: 10_000_000,
        SampledUniquePatterns: 90_000,
        DuplicatePatternCount: duplicatePatternCount,
        DuplicateWastedBytes: 1_000_000,
        DuplicationRatio: duplicationRatio,
        PctOfManagedHeap: pctOfManagedHeap,
        TopDuplicates: [],
        VeryLongStrings: veryLongStrings ?? [],
        LohStringBytes: lohStringBytes,
        InternedStringCount: 0,
        InternedStringBytes: 0,
        Gen0StringCount: 0,
        Gen1StringCount: 0,
        Gen2StringCount: 0,
        Gen2StringBytes: 0,
        StringsSampled: 100_000,
        SamplingCoverage: samplingCoverage);
}
