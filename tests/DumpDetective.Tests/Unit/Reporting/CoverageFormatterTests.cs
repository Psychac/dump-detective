using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Artifacts;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class CoverageFormatterTests
{
    private static readonly ArtifactId Dump = new("dump-1");

    private static SingleDumpReportDocument DocumentWithCoverage()
    {
        CoverageRecord[] records =
        [
            new(new Capability("heap.types"), Dump, CoverageStatus.Complete, []),
            new(new Capability("heap.root-paths"), Dump, CoverageStatus.Partial,
                [new CoverageImpact("Reference Chain Analysis", CoverageImpactKind.Limited)],
                new CoverageBoundary(8, 10, "sampled types", "hit the path or depth budget")),
            new(new Capability("trace.gc-events"), null, CoverageStatus.NotSupplied,
                [new CoverageImpact("GcPauseAnalyzer", CoverageImpactKind.NotRun)]),
        ];

        return new SingleDumpReportDocument
        {
            DumpPath = "C:/dumps/x.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 1,
            Coverage = records,
            CoverageView = CoverageProse.Build(records),
        };
    }

    [Fact]
    public void Markdown_RendersACoverageSectionWithTheHeadlineAndEachGroup()
    {
        string output = new MarkdownCanonicalReportFormatter().Render(DocumentWithCoverage());

        output.Should().Contain("## Coverage")
            .And.Contain("Coverage: 1 of 2 capabilities complete; 1 limited (heap.root-paths: 8 of 10 sampled types hit the path or depth budget)")
            .And.Contain("**Limited by their own budgets**")
            .And.Contain("- heap.root-paths: 8 of 10 sampled types hit the path or depth budget. Limits Reference Chain Analysis.")
            .And.Contain("**Not supplied**")
            .And.Contain("- No trace was supplied: 1 capability, 1 analyzer(s) not run")
            .And.Contain("**Complete** (1): heap.types");
    }

    [Fact]
    public void Text_RendersTheSameCoverageWording()
    {
        string output = new TextCanonicalReportFormatter().Render(DocumentWithCoverage());

        output.Should().Contain("COVERAGE")
            .And.Contain("Coverage: 1 of 2 capabilities complete; 1 limited (heap.root-paths: 8 of 10 sampled types hit the path or depth budget)")
            .And.Contain("Limited by their own budgets:")
            .And.Contain("- heap.root-paths: 8 of 10 sampled types hit the path or depth budget. Limits Reference Chain Analysis.")
            .And.Contain("- No trace was supplied: 1 capability, 1 analyzer(s) not run")
            .And.Contain("Complete (1): heap.types");
    }

    [Fact]
    public void BothFormats_RenderNothingForCoverage_WhenTheDocumentHasNone()
    {
        var doc = new SingleDumpReportDocument { DumpPath = "C:/dumps/x.dmp", GeneratedAtUtc = DateTime.UtcNow, ElapsedSeconds = 1 };

        new MarkdownCanonicalReportFormatter().Render(doc).Should().NotContain("## Coverage");
        new TextCanonicalReportFormatter().Render(doc).Should().NotContain("COVERAGE");
    }
}
