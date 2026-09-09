using System.Buffers.Binary;

using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sources.NetTrace;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Integration.TraceIngest;

/// <summary>
/// End-to-end verification of Phase 6a's second and third slices (trace.gcevents, trace.contention)
/// against a real capture — docs/refactor/modularity/phase-6-trace-source.md § Phase 6a.
/// </summary>
public sealed class GcPauseAndContentionIndexerRealTraceTests : IDisposable
{
    private static readonly string TracePath = Environment.GetEnvironmentVariable("DD_BENCHMARK_ETL")
        ?? @"D:\Dumps\08-05\etls\HighCPU_11.etl";

    private readonly string _containerPath;

    public GcPauseAndContentionIndexerRealTraceTests()
    {
        _containerPath = Path.Combine(Path.GetTempPath(), $"trace-gc-contention-test-{Guid.NewGuid():N}.bin");
    }

    public void Dispose()
    {
        if (File.Exists(_containerPath))
            File.Delete(_containerPath);
    }

    [RealTraceFact]
    public void Build_RealEtlCapture_ProducesReadableGcEventsAndContentionSections()
    {
        File.Exists(TracePath).Should().BeTrue($"expected real .etl at {TracePath} or a DD_BENCHMARK_ETL override.");

        TraceIndexBuilder.Build(TracePath, _containerPath, targetProcessId: null);

        CacheContainerReader.TryOpen(_containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader.Should().NotBeNull();

        VerifyGcEvents(reader!);
        VerifyContention(reader!);
    }

    private static void VerifyGcEvents(CacheContainerReader reader)
    {
        reader.ContainsSection(CacheSectionId.TraceGcEvents).Should().BeTrue();
        reader.TryGetSectionInfo(CacheSectionId.TraceGcEvents, out CacheTocEntry entry).Should().BeTrue();
        entry.RecordCount.Should().BeGreaterThan(0);

        reader.TryOpenSection(CacheSectionId.TraceGcEvents, out Stream? sectionStream).Should().BeTrue();
        sectionStream.Should().NotBeNull();

        List<GcEventRecord> records = ReadGcEventRecords(sectionStream!, entry.RecordCount);

        records.Should().NotBeEmpty();
        records.Should().OnlyContain(r => r.PauseTicks >= 0, "a pause window can never end before it starts");
        records.Should().Contain(r => r.HasGcData, "at least one blocking GC in this capture should fully nest inside its pause window");
        records.Where(r => r.HasGcData).Should().OnlyContain(r => r.HeapBytes > 0);
    }

    private static void VerifyContention(CacheContainerReader reader)
    {
        reader.ContainsSection(CacheSectionId.TraceContention).Should().BeTrue();
        reader.TryGetSectionInfo(CacheSectionId.TraceContention, out CacheTocEntry entry).Should().BeTrue();
        entry.RecordCount.Should().BeGreaterThan(0);

        reader.TryOpenSection(CacheSectionId.TraceContention, out Stream? sectionStream).Should().BeTrue();
        sectionStream.Should().NotBeNull();

        List<ContentionRecord> records = ReadContentionRecords(sectionStream!, (int)Math.Min(entry.RecordCount, 5_000));

        records.Should().NotBeEmpty();
        records.Should().OnlyContain(r => r.DurationTicks >= 0, "a contention stop can never precede its own start");
    }

    private readonly record struct GcEventRecord(long TimestampTicks, int ThreadId, byte Reason, long PauseTicks, bool HasGcData, int Generation, long HeapBytes);
    private readonly record struct ContentionRecord(long StartTicks, long DurationTicks, int ThreadId, byte Flags);

    private static List<GcEventRecord> ReadGcEventRecords(Stream stream, long count)
    {
        var results = new List<GcEventRecord>();
        Span<byte> record = stackalloc byte[8 + 4 + 1 + 8 + 1 + 4 + 8];

        for (long i = 0; i < count; i++)
        {
            int read = stream.ReadAtLeast(record, record.Length, throwOnEndOfStream: false);
            if (read < record.Length)
                break;

            long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(record);
            int threadId = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
            byte reason = record[12];
            long pauseTicks = BinaryPrimitives.ReadInt64LittleEndian(record[13..]);
            bool hasGcData = record[21] != 0;
            int generation = BinaryPrimitives.ReadInt32LittleEndian(record[22..]);
            long heapBytes = BinaryPrimitives.ReadInt64LittleEndian(record[26..]);

            results.Add(new GcEventRecord(timestampTicks, threadId, reason, pauseTicks, hasGcData, generation, heapBytes));
        }

        return results;
    }

    private static List<ContentionRecord> ReadContentionRecords(Stream stream, int count)
    {
        var results = new List<ContentionRecord>();
        Span<byte> record = stackalloc byte[8 + 8 + 4 + 1];

        for (int i = 0; i < count; i++)
        {
            int read = stream.ReadAtLeast(record, record.Length, throwOnEndOfStream: false);
            if (read < record.Length)
                break;

            long startTicks = BinaryPrimitives.ReadInt64LittleEndian(record);
            long durationTicks = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
            int threadId = BinaryPrimitives.ReadInt32LittleEndian(record[16..]);
            byte flags = record[20];

            results.Add(new ContentionRecord(startTicks, durationTicks, threadId, flags));
        }

        return results;
    }
}
