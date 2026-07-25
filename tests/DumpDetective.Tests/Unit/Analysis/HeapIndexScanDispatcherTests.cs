using System.Buffers.Binary;
using System.Reflection;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class HeapIndexScanDispatcherTests : IDisposable
{
    private readonly string _testDir;

    public HeapIndexScanDispatcherTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "heap-index-scan-dispatcher-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void Run_FansOutEachEntryToAllParticipants_ExactlyOnce_InAddressOrder()
    {
        (ulong Addr, ulong Mt, ulong Size)[] entries =
        [
            (0x1000, 0x2000, 100),
            (0x1100, 0x2100, 200),
            (0x1200, 0x2200, 300)
        ];

        HeapAnalysisCache cache = CreateCacheWithIndex(entries);
        RecordingParticipant first = new();
        RecordingParticipant second = new();

        new HeapIndexScanDispatcher().Run(cache, CreateContext(cache), [first, second], CancellationToken.None);

        foreach (RecordingParticipant participant in new[] { first, second })
        {
            participant.Entries.Should().HaveCount(entries.Length);
            participant.Entries.Select(e => e.Address).Should().Equal(entries.Select(e => e.Addr));
            participant.Entries.Select(e => e.MethodTable).Should().Equal(entries.Select(e => e.Mt));
            participant.Entries.Select(e => e.Size).Should().Equal(entries.Select(e => e.Size));
            participant.CompletedWithSuccess.Should().Be(true);
        }
    }

    [Fact]
    public void Run_PublishesStartedAndCompletedDiagnosticsEvents_ForSharedScan()
    {
        HeapAnalysisCache cache = CreateCacheWithIndex([(0x1000, 0x2000, 100), (0x1100, 0x2100, 200)]);
        InMemoryAnalysisDiagnosticsSink sink = new();
        RuntimeAnalysisContext context = new()
        {
            Runtime = null!,
            Cache = cache,
            DiagnosticsSink = sink
        };

        new HeapIndexScanDispatcher().Run(cache, context, [new RecordingParticipant()], CancellationToken.None);

        IReadOnlyList<AnalysisDiagnosticsEvent> events = sink.Events;
        events.Should().Contain(e => e.EventType == AnalysisDiagnosticsEventType.AnalyzerStarted && e.AnalyzerName == "Shared heap index scan");
        events.Should().Contain(e => e.EventType == AnalysisDiagnosticsEventType.AnalyzerCompleted && e.ObjectScanCount == 2);
    }

    [Fact]
    public void Run_DoesNotEnumerate_WhenNoParticipantsRegistered()
    {
        HeapAnalysisCache cache = CreateCacheWithIndex([(0x1000, 0x2000, 100)]);

        Action act = () => new HeapIndexScanDispatcher().Run(cache, CreateContext(cache), [], CancellationToken.None);

        act.Should().NotThrow();
    }

    [Fact]
    public void Run_MarksParticipantsFailed_WhenNoHeapIndexBuilt()
    {
        HeapAnalysisCache cache = new();
        RecordingParticipant participant = new();

        new HeapIndexScanDispatcher().Run(cache, CreateContext(cache), [participant], CancellationToken.None);

        participant.Entries.Should().BeEmpty();
        participant.CompletedWithSuccess.Should().Be(false);
    }

    private static RuntimeAnalysisContext CreateContext(HeapAnalysisCache cache)
    {
        return new RuntimeAnalysisContext
        {
            Runtime = null!,
            Cache = cache
        };
    }

    private HeapAnalysisCache CreateCacheWithIndex((ulong Addr, ulong Mt, ulong Size)[] entries)
    {
        string containerPath = Path.Combine(_testDir, $"{Guid.NewGuid()}.bin");
        WriteObjectIndexContainer(containerPath, entries);

        HeapIndexBuildResult buildResult = new(
            StorageKind: HeapIndexStorageKind.Disk,
            IndexPath: containerPath,
            ObjectCount: entries.Length,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: new Dictionary<ulong, TypeAggregateIndexEntry>());

        HeapAnalysisCache cache = new();

        object heapIndexCache = typeof(HeapAnalysisCache)
            .GetField("_heapIndexCache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cache)!;

        heapIndexCache.GetType()
            .GetField("_heapIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(heapIndexCache, buildResult);

        return cache;
    }

    private static void WriteObjectIndexContainer(string containerPath, (ulong Addr, ulong Mt, ulong Size)[] entries)
    {
        using CacheContainerWriter writer = new(containerPath);

        WriteColumn(writer, CacheSectionId.ObjectAddresses, entries.Select(e => e.Addr).ToArray());
        WriteColumn(writer, CacheSectionId.ObjectMethodTables, entries.Select(e => e.Mt).ToArray());
        WriteColumn(writer, CacheSectionId.ObjectSizes, entries.Select(e => e.Size).ToArray());

        writer.Finish();
    }

    private static void WriteColumn(CacheContainerWriter writer, CacheSectionId sectionId, ulong[] values)
    {
        writer.BeginSection(sectionId);

        Span<byte> buffer = stackalloc byte[8];
        foreach (ulong value in values)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            writer.Stream.Write(buffer);
        }

        writer.EndSection(values.Length);
    }

    private sealed class RecordingParticipant : IHeapIndexScanParticipant
    {
        public List<HeapEntry> Entries { get; } = new();
        public bool? CompletedWithSuccess { get; private set; }

        public void BeforeHeapIndexScan(AnalysisContext context)
        {
        }

        public void OnHeapEntry(in HeapEntry entry) => Entries.Add(entry);

        public void OnHeapIndexScanCompleted(bool succeeded) => CompletedWithSuccess = succeeded;
    }
}
