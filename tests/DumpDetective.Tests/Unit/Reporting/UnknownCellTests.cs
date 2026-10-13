using System.Text.Json;

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;
using DumpDetective.Reporting.Serialization;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class UnknownCellTests
{
    [Theory]
    [InlineData("NotMeasured", "{\"u\":\"nm\"}", "—")]
    [InlineData("NotApplicable", "{\"u\":\"na\"}", "n/a")]
    [InlineData("Ambiguous", "{\"u\":\"amb\"}", "?")]
    public void ARowWritesEachReasonAsOneObjectAndReadsItBack_AndTextRenderersGetTheMark(string reasonName, string json, string mark)
    {
        var reason = Enum.Parse<UnknownReason>(reasonName);
        var row = new CompactRow(["x", 5L, new UnknownCell(reason), null]);

        string written = JsonSerializer.Serialize(row);
        written.Should().Be($"[\"x\",5,{json},null]");

        CompactRow restored = JsonSerializer.Deserialize<CompactRow>(written)!;
        restored.Values[2].Should().Be(new UnknownCell(reason));
        restored.Values[2]!.ToString().Should().Be(mark);
    }

    [Fact]
    public void ARowWithAnUnknownCellSerializesThroughTheReportsOwnJsonContext()
    {
        // The report writes through a source-generated context, which only serializes types it lists; the plain serializer does not.
        var table = new CompactTable("t", [new CompactHeader("a")], [new CompactRow([new UnknownCell(UnknownReason.Ambiguous)])]);

        string json = JsonSerializer.Serialize(table, ReportJsonContext.Default.CompactTable);

        json.Should().Contain("[{\"u\":\"amb\"}]");
    }

    [Fact]
    public void TopTypes_AZeroLohSizeIsAValue_AndAnUnwalkedRetainedSizeIsNotMeasured()
    {
        var types = new[]
        {
            new TypeSnapshot("App.Walked", 10, 1_000, 0, AverageSize: 100, EstimatedRetainedBytes: 5_000),
            new TypeSnapshot("App.NotWalked", 10, 1_000, 0, AverageSize: 100, EstimatedRetainedBytes: 0),
        };
        MemoryDomainResult result = new(
            TotalBytes: 2_000, LohBytes: 0, LohPercent: 0, TotalObjects: 20, LohObjects: 0, LohThresholdBytes: 85_000, UniqueTypes: 2, TopTypes: types);

        CompactTable table = new MemorySectionBuilder().Build(result).CompactTables!.Single(t => t.Title == "Top types");
        int loh = table.Headers.ToList().FindIndex(h => h.Name == "LOH Bytes");
        int retained = table.Headers.ToList().FindIndex(h => h.Name == "Est. Retained");

        table.Rows[0].Values[loh].Should().Be(0L, "a measured zero is a value, not a gap");
        table.Rows[1].Values[loh].Should().Be(0L);
        table.Rows[0].Values[retained].Should().Be(5_000L);
        table.Rows[1].Values[retained].Should().Be(new UnknownCell(UnknownReason.NotMeasured));
    }

    [Fact]
    public void FinalizerQueue_NotDisposableIsNotApplicable_AndADisposableWithNoFieldIsAmbiguous()
    {
        var entries = new[]
        {
            Entry(disposable: false, fieldFound: false, fieldValue: false),
            Entry(disposable: true, fieldFound: false, fieldValue: false),
            Entry(disposable: true, fieldFound: true, fieldValue: true),
        };

        CompactTable table = new FinalizableObjectSectionBuilder().Build(ResultWith(entries)).CompactTables!
            .Single(t => t.Title.StartsWith("Top finalizer queue entries"));
        int fieldFound = table.Headers.ToList().FindIndex(h => h.Name == "Disposed Field Found");
        int disposed = table.Headers.ToList().FindIndex(h => h.Name == "Disposed");

        table.Rows[0].Values[fieldFound].Should().Be(new UnknownCell(UnknownReason.NotApplicable));
        table.Rows[0].Values[disposed].Should().Be(new UnknownCell(UnknownReason.NotApplicable));
        table.Rows[1].Values[fieldFound].Should().Be("No");
        table.Rows[1].Values[disposed].Should().Be(new UnknownCell(UnknownReason.Ambiguous));
        table.Rows[2].Values[fieldFound].Should().Be("Yes");
        table.Rows[2].Values[disposed].Should().Be("Yes");
    }

    private static FinalizerQueueEntry Entry(bool disposable, bool fieldFound, bool fieldValue) => new(
        Address: 0x1000, TypeName: "App.Thing", ShallowSize: 24, EstimatedRetainedBytes: 24,
        IsDisposableType: disposable, DisposedFieldFound: fieldFound, DisposedFieldValue: fieldValue);

    private static FinalizableObjectDomainResult ResultWith(IReadOnlyList<FinalizerQueueEntry> entries) => new(
        TotalFinalizableObjects: 3, TotalFinalizableBytes: 72, Gen0Count: 0, Gen1Count: 0, Gen2Count: 0, LohCount: 0,
        FinalizerQueueCount: 3, FinalizerQueueRetainedBytes: 72, IsRetainedEstimatePartial: false, HasUndisposedDisposableInQueue: false,
        CriticalFinalizerQueueCount: 0, CriticalFinalizerQueueBytes: 0,
        TopFinalizableTypesByGen2Count: [], TopQueueTypesByCount: [], TopCriticalFinalizerTypesByCount: [],
        TopQueueEntriesByRetainedSize: entries);
}
