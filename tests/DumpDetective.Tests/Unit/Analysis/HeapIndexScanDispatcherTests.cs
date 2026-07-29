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

    [Fact]
    public void Run_DoesNotPartition_WhenEntryCountBelowMinRecordsPerWorker()
    {
        // Below MinRecordsPerWorker, ComputeWorkerCount collapses to 1 — the parallel-capable
        // participant must fall back to the same single full-range pass as everyone else, with
        // CreateWorkerInstance/MergePartial never invoked.
        HeapAnalysisCache cache = CreateCacheWithIndex([(0x1000, 0x2000, 100), (0x1100, 0x2100, 200)]);
        FakeParallelParticipant participant = new();

        new HeapIndexScanDispatcher().Run(cache, CreateContext(cache), [participant], CancellationToken.None);

        participant.Entries.Should().HaveCount(2);
        participant.CreatedWorkers.Should().BeEmpty();
        participant.MergedPartials.Should().BeNull();
        participant.CompletedWithSuccess.Should().Be(true);
    }

    [Fact]
    public void Run_PartitionsParallelCapableParticipant_AcrossWorkers_ExactlyOnceNoGaps()
    {
        const int entryCount = 500_000; // >= 2 * MinRecordsPerWorker, forces workerCount > 1
        var entries = new (ulong Addr, ulong Mt, ulong Size)[entryCount];
        for (int i = 0; i < entryCount; i++)
            entries[i] = ((ulong)(0x1000 + i), 0x2000, 8);

        HeapAnalysisCache cache = CreateCacheWithIndex(entries);
        FakeParallelParticipant participant = new();

        new HeapIndexScanDispatcher().Run(cache, CreateContext(cache), [participant], CancellationToken.None);

        participant.CompletedWithSuccess.Should().Be(true);
        participant.Entries.Should().HaveCount(entryCount);
        participant.Entries.Select(e => e.Address).Distinct().Should().HaveCount(entryCount);

        if (Environment.ProcessorCount < 2)
        {
            // ComputeWorkerCount clamps to Environment.ProcessorCount, so a single-core test
            // runner can't exercise the partitioned path — the single-worker fallback above is
            // still correct, but there's nothing further to assert about workers/merge here.
            return;
        }

        participant.CreatedWorkers.Should().NotBeEmpty();
        participant.MergedPartials.Should().HaveCount(participant.CreatedWorkers.Count);
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
        WriteGenerationColumn(writer, entries.Length);

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

    private static void WriteGenerationColumn(CacheContainerWriter writer, int recordCount)
    {
        writer.BeginSection(CacheSectionId.ObjectGenerations);
        for (int i = 0; i < recordCount; i++)
            writer.Stream.WriteByte(0);
        writer.EndSection(recordCount);
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

    // Each instance (primary + every CreateWorkerInstance() clone) owns its own private Entries
    // list, so concurrent workers never share mutable state — matching the contract
    // IParallelHeapIndexScanParticipant documents.
    private sealed class FakeParallelParticipant : IParallelHeapIndexScanParticipant
    {
        public List<HeapEntry> Entries { get; } = new();
        public bool? CompletedWithSuccess { get; private set; }
        public IReadOnlyList<IHeapIndexScanParticipant>? MergedPartials { get; private set; }
        public List<FakeParallelParticipant> CreatedWorkers { get; } = new();

        public void BeforeHeapIndexScan(AnalysisContext context)
        {
        }

        public void OnHeapEntry(in HeapEntry entry) => Entries.Add(entry);

        public void OnHeapIndexScanCompleted(bool succeeded) => CompletedWithSuccess = succeeded;

        public IHeapIndexScanParticipant CreateWorkerInstance()
        {
            FakeParallelParticipant worker = new();
            CreatedWorkers.Add(worker);
            return worker;
        }

        public void MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
        {
            MergedPartials = partials;
            foreach (IHeapIndexScanParticipant p in partials)
                Entries.AddRange(((FakeParallelParticipant)p).Entries);
        }
    }
}
