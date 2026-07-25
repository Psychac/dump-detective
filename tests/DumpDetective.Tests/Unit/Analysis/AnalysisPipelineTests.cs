using System.Buffers.Binary;
using System.Reflection;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

using CoreAnalysisContext = DumpDetective.Core.Abstractions.AnalysisContext;

public sealed class AnalysisPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldContinueOnFailure_WhenConfigured()
    {
        IAnalyzer[] analyzers =
        [
            new TestAnalyzer("Failing", 0, throwError: true),
            new TestAnalyzer("AfterFailure", 1)
        ];

        AnalysisPipeline pipeline = new(analyzers, new FindingGenerationPipeline([]));
        RuntimeAnalysisContext context = CreateContext(continueOnFailure: true);

        IReadOnlyList<AnalyzerRunResult> result = await pipeline.ExecuteAsync(context, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Status.Should().Be(AnalyzerExecutionStatus.Failed);
        result[1].Status.Should().Be(AnalyzerExecutionStatus.Success);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopOnFailure_WhenConfigured()
    {
        IAnalyzer[] analyzers =
        [
            new TestAnalyzer("Failing", 0, throwError: true),
            new TestAnalyzer("ShouldNotRun", 1)
        ];

        AnalysisPipeline pipeline = new(analyzers, new FindingGenerationPipeline([]));
        RuntimeAnalysisContext context = CreateContext(continueOnFailure: false);

        IReadOnlyList<AnalyzerRunResult> result = await pipeline.ExecuteAsync(context, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].AnalyzerName.Should().Be("Failing");
        result[0].Status.Should().Be(AnalyzerExecutionStatus.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMarkCanceled_WhenAnalyzerThrowsOperationCanceledException()
    {
        IAnalyzer[] analyzers =
        [
            new TestAnalyzer("Canceling", 0, throwCanceled: true),
            new TestAnalyzer("ShouldNotRun", 1)
        ];

        AnalysisPipeline pipeline = new(analyzers, new FindingGenerationPipeline([]));
        RuntimeAnalysisContext context = CreateContext(continueOnFailure: true);

        IReadOnlyList<AnalyzerRunResult> result = await pipeline.ExecuteAsync(context, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(AnalyzerExecutionStatus.SkippedByCancellation);
    }

    [Fact]
    public async Task ExecuteAsync_ScansHeapIndexExactlyOnce_WhenMultipleParticipantsRegistered()
    {
        (ulong Addr, ulong Mt, ulong Size)[] entries =
        [
            (0x1000, 0x2000, 100),
            (0x1100, 0x2100, 200),
            (0x1200, 0x2200, 300)
        ];

        ScanParticipantTestAnalyzer first = new("First", 0);
        ScanParticipantTestAnalyzer second = new("Second", 1);
        IAnalyzer[] analyzers = [first, second];

        AnalysisPipeline pipeline = new(analyzers, new FindingGenerationPipeline([]));
        RuntimeAnalysisContext context = CreateContext(continueOnFailure: true);
        InjectHeapIndex((HeapAnalysisCache)context.Cache, entries);

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Each participant must see every entry exactly once — if the shared scan ever
        // ran more than once per pipeline execution, these counts would be multiplied.
        foreach (ScanParticipantTestAnalyzer analyzer in new[] { first, second })
        {
            analyzer.ScannedEntries.Should().HaveCount(entries.Length);
            analyzer.ScannedEntries.Select(e => e.Address).Should().Equal(entries.Select(e => e.Addr));
            analyzer.BeforeHeapIndexScanCallCount.Should().Be(1);
        }
    }

    private static RuntimeAnalysisContext CreateContext(bool continueOnFailure)
    {
        DiagnosticsOptions diagnostics = new()
        {
            ContinueOnAnalyzerFailure = continueOnFailure
        };

        return new RuntimeAnalysisContext
        {
            Runtime = null!,
            Cache = new HeapAnalysisCache(),
            Diagnostics = diagnostics
        };
    }

    private static void InjectHeapIndex(HeapAnalysisCache cache, (ulong Addr, ulong Mt, ulong Size)[] entries)
    {
        string containerPath = Path.Combine(Path.GetTempPath(), "analysis-pipeline-tests", $"{Guid.NewGuid()}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(containerPath)!);
        WriteObjectIndexContainer(containerPath, entries);

        HeapIndexBuildResult buildResult = new(
            StorageKind: HeapIndexStorageKind.Disk,
            IndexPath: containerPath,
            ObjectCount: entries.Length,
            Elapsed: TimeSpan.Zero,
            TypeAggregates: new Dictionary<ulong, TypeAggregateIndexEntry>());

        // HeapAnalysisCache delegates enumeration to its private HeapIndexCache, so the
        // build result must be injected on that nested cache (not HeapAnalysisCache's own
        // reflection-only backdoor field, which only satisfies TryGetHeapIndex).
        object heapIndexCache = typeof(HeapAnalysisCache)
            .GetField("_heapIndexCache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cache)!;

        typeof(HeapIndexCache)
            .GetField("_heapIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(heapIndexCache, buildResult);
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

    private sealed class TestAnalyzer(string name, int order, Action? onExecute = null, bool throwError = false, bool throwCanceled = false) : IAnalyzer
    {
        public string Name { get; } = name;
        public int Order { get; } = order;
        public string Category => "Test";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(CoreAnalysisContext context, CancellationToken cancellationToken)
        {
            onExecute?.Invoke();

            if (throwCanceled)
            {
                throw new OperationCanceledException("Canceled by test analyzer.");
            }

            if (throwError)
            {
                throw new InvalidOperationException("Failure from test analyzer.");
            }

            AnalyzerDomainResult result = new GenericAnalyzerDomainResult
            {
                AnalyzerName = Name,
                Category = Category
            };

            return ValueTask.FromResult(result);
        }
    }

    private sealed class ScanParticipantTestAnalyzer(string name, int order) : IAnalyzer, IHeapIndexScanParticipant
    {
        public string Name { get; } = name;
        public int Order { get; } = order;
        public string Category => "Test";

        public List<HeapEntry> ScannedEntries { get; } = new();
        public int BeforeHeapIndexScanCallCount { get; private set; }

        public void BeforeHeapIndexScan(CoreAnalysisContext context) => BeforeHeapIndexScanCallCount++;

        public void OnHeapEntry(in HeapEntry entry) => ScannedEntries.Add(entry);

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(CoreAnalysisContext context, CancellationToken cancellationToken)
        {
            AnalyzerDomainResult result = new GenericAnalyzerDomainResult
            {
                AnalyzerName = Name,
                Category = Category
            };

            return ValueTask.FromResult(result);
        }
    }
}
