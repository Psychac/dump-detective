using System.Buffers.Binary;
using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Platform.Storage.Container;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;
using DumpDetective.Analysis.Analyzers.Threads;

namespace DumpDetective.Tests.Unit.Analysis;

using DumpDetective.Tests.Helpers;

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

    /// <summary>
    /// Gate for the thread-domain quartet retyping
    /// (docs/refactor/modularity/phase-1-thread-quartet-plan.md § 6): proves the pipeline's shared
    /// <c>ThreadStackScanDispatcher</c> pass still runs exactly once per thread when retyped
    /// analyzers (<see cref="LockGraphAnalyzer"/>, <see cref="ThreadStackClusterAnalyzer"/>
    /// — both implement <see cref="IThreadStackScanParticipant"/> directly) are registered alongside
    /// other participants — the property this whole shared-scan design depends on, asserted
    /// directly rather than assumed from the design.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ScansThreadStacksExactlyOnce_WhenRetypedAndLegacyParticipantsAreMixed()
    {
        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();

        int expectedThreadCount = 0;
        foreach (ClrThread _ in runtime.Threads)
            expectedThreadCount++;

        var first = new ThreadStackScanParticipantTestAnalyzer("First", 0);
        using LockGraphAnalyzer lockGraph = new();
        using ThreadStackClusterAnalyzer threadStackCluster = new();
        var second = new ThreadStackScanParticipantTestAnalyzer("Second", 2);
        IAnalyzer[] analyzers = [first, lockGraph, threadStackCluster, second];

        AnalysisPipeline pipeline = new(analyzers, new FindingGenerationPipeline([]));
        RuntimeAnalysisContext context = new() { Runtime = runtime, Cache = new HeapAnalysisCache() };

        IReadOnlyList<AnalyzerRunResult> results = await pipeline.ExecuteAsync(context, CancellationToken.None);

        // Each participant must see every thread exactly once — if the shared scan ever ran more
        // than once per pipeline execution, these counts would be multiplied.
        foreach (ThreadStackScanParticipantTestAnalyzer analyzer in new[] { first, second })
        {
            analyzer.OnThreadStackCallCount.Should().Be(expectedThreadCount);
            analyzer.BeforeThreadStackScanCallCount.Should().Be(1);
        }

        // Both analyzers' own results still come out correctly.
        AnalyzerRunResult lockGraphRun = results.Single(r => r.AnalyzerName == lockGraph.Name);
        lockGraphRun.Status.Should().Be(AnalyzerExecutionStatus.Success);
        lockGraphRun.Result.Should().BeOfType<LockGraphDomainResult>();

        AnalyzerRunResult threadStackClusterRun = results.Single(r => r.AnalyzerName == threadStackCluster.Name);
        threadStackClusterRun.Status.Should().Be(AnalyzerExecutionStatus.Success);
        threadStackClusterRun.Result.Should().BeOfType<ThreadStackClusterDomainResult>();
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
        ObjectColumnSectionsWriter.WriteMethodTableColumns(writer, entries.Select(e => e.Mt).ToArray());
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

    private static void WriteGenerationColumn(CacheContainerWriter writer, int recordCount) =>
        // Delegates so the run-length encoding (format v9) lives in exactly one place.
        ObjectColumnSectionsWriter.WriteGenerationColumn(writer, new sbyte[recordCount]);

    private sealed class TestAnalyzer(string name, int order, Action? onExecute = null, bool throwError = false, bool throwCanceled = false) : IAnalyzer, IProducesAnalyzerDomainResult, ICustomizesAnalysisContext
    {
        public string Name { get; } = name;
        public int Order { get; } = order;
        public string Category => "Test";

        public AnalyzerDomainResult? LastResult { get; private set; }

        // These tests exercise pipeline orchestration (failure/cancellation handling), not real
        // heap access, and use a fake RuntimeAnalysisContext with Runtime = null! — bypass the
        // normal capability-building context (which unconditionally touches context.Heap).
        public AnalysisContext BuildAnalysisContext(RuntimeAnalysisContext context) => new() { Observations = NoOpObservationSink.Instance };

        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
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

            LastResult = new GenericAnalyzerDomainResult
            {
                AnalyzerName = Name,
                Category = Category
            };

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScanParticipantTestAnalyzer(string name, int order) : IAnalyzer, IProducesAnalyzerDomainResult, IHeapIndexScanParticipant
    {
        public string Name { get; } = name;
        public int Order { get; } = order;
        public string Category => "Test";

        public AnalyzerDomainResult? LastResult { get; private set; }

        public List<HeapEntry> ScannedEntries { get; } = new();
        public int BeforeHeapIndexScanCallCount { get; private set; }

        public void BeforeHeapIndexScan(RuntimeAnalysisContext context) => BeforeHeapIndexScanCallCount++;

        public void OnHeapEntry(in HeapEntry entry) => ScannedEntries.Add(entry);

        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            LastResult = new GenericAnalyzerDomainResult
            {
                AnalyzerName = Name,
                Category = Category
            };

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThreadStackScanParticipantTestAnalyzer(string name, int order) : IAnalyzer, IProducesAnalyzerDomainResult, IThreadStackScanParticipant
    {
        public string Name { get; } = name;
        public int Order { get; } = order;
        public string Category => "Test";

        public AnalyzerDomainResult? LastResult { get; private set; }

        public int OnThreadStackCallCount { get; private set; }
        public int BeforeThreadStackScanCallCount { get; private set; }

        public int GetRequiredFrameCount(RuntimeAnalysisContext context) => 1;

        public void BeforeThreadStackScan(RuntimeAnalysisContext context) => BeforeThreadStackScanCallCount++;

        public void OnThreadStack(in ThreadStackSnapshot snapshot) => OnThreadStackCallCount++;

        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            LastResult = new GenericAnalyzerDomainResult
            {
                AnalyzerName = Name,
                Category = Category
            };

            return ValueTask.CompletedTask;
        }
    }
}
