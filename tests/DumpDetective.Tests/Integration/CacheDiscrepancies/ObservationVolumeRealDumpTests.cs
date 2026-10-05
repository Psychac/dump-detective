using System.Diagnostics;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;

using Microsoft.Diagnostics.Runtime;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 5 step 1's closing measurement (docs/refactor/modularity/phase-5-observations-synthesis.md
/// exit criterion: "<c>ObservationStore</c> handles a full large-dump run within bounded memory").
/// Runs every retyped analyzer against the reference dump through the same <see cref="AnalysisPipeline"/>
/// orchestration the CLI uses, and reports how many <see cref="Observation"/>s the run's
/// <see cref="CollectingObservationSink"/> accumulated and how much managed memory that cost —
/// closing the model's own "biggest unvalidated assumption" (observation-and-correlation-model.md
/// § 7) with a real number instead of an estimate.
/// </summary>
public sealed class ObservationVolumeRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public ObservationVolumeRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task RunAllAnalyzers_RealDump_ReportsObservationVolumeAndMemory()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        var indexStopwatch = Stopwatch.StartNew();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        indexStopwatch.Stop();

        var sink = new CollectingObservationSink();
        RuntimeAnalysisContext context = new()
        {
            Runtime = runtime,
            Cache = cache,
            ArtifactId = new ArtifactId(dumpPath),
            ObservationSink = sink,
            AnalysisOptions = new(),
        };

        IReadOnlyList<Sdk.Analysis.IAnalyzer> analyzers = new DefaultAnalyzerFactory().CreateAnalyzers();
        var pipeline = new AnalysisPipeline(analyzers);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long managedBytesBefore = GC.GetTotalMemory(forceFullCollection: true);
        long workingSetBefore = Process.GetCurrentProcess().WorkingSet64;

        var runStopwatch = Stopwatch.StartNew();
        IReadOnlyList<AnalyzerRunResult> runs = await pipeline.ExecuteAsync(context, CancellationToken.None);
        runStopwatch.Stop();

        long managedBytesAfterRaw = GC.GetTotalMemory(forceFullCollection: false);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long managedBytesAfter = GC.GetTotalMemory(forceFullCollection: true);
        long workingSetAfter = Process.GetCurrentProcess().WorkingSet64;

        IReadOnlyList<Observation> observations = sink.Observations;
        long managedDelta = managedBytesAfter - managedBytesBefore;
        long workingSetDelta = workingSetAfter - workingSetBefore;
        double bytesPerObservation = observations.Count > 0 ? (double)managedDelta / observations.Count : 0.0;

        var byType = observations
            .GroupBy(o => o.ObservationType, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ToArray();

        _output.WriteLine($"Dump: {dumpPath}");
        _output.WriteLine($"Heap index build: {indexStopwatch.Elapsed}");
        _output.WriteLine($"Analyzers run: {analyzers.Count}, completed run results: {runs.Count}");
        _output.WriteLine($"Analyzer run wall time: {runStopwatch.Elapsed}");
        _output.WriteLine($"Total observations emitted: {observations.Count:N0}");
        _output.WriteLine($"Managed memory before run (post-GC): {managedBytesBefore / 1024.0 / 1024.0:N1} MB");
        _output.WriteLine($"Managed memory immediately after run (no forced GC): {managedBytesAfterRaw / 1024.0 / 1024.0:N1} MB");
        _output.WriteLine($"Managed memory after run (post-GC): {managedBytesAfter / 1024.0 / 1024.0:N1} MB");
        _output.WriteLine($"Managed memory delta (post-GC, attributable to the run incl. observations): {managedDelta / 1024.0 / 1024.0:N1} MB");
        _output.WriteLine($"Approx bytes per observation (upper bound — includes domain results, not just observations): {bytesPerObservation:N0} bytes");
        _output.WriteLine($"Process working set before: {workingSetBefore / 1024.0 / 1024.0:N1} MB, after: {workingSetAfter / 1024.0 / 1024.0:N1} MB, delta: {workingSetDelta / 1024.0 / 1024.0:N1} MB");
        _output.WriteLine("Observations by type:");
        foreach (var group in byType)
            _output.WriteLine($"  {group.Key}: {group.Count():N0}");

        ReportSerializedObservationSize(observations);

        Assert.True(observations.Count > 0, "Phase 5 step 1's retyped analyzers should emit at least one observation on a real dump.");
    }

    // M1 decision D2 (docs/ReportStructure/m1-claims-plan.md): inline vs side-car observations in report.json.
    private void ReportSerializedObservationSize(IReadOnlyList<Observation> observations)
    {
        string outputPath = Environment.GetEnvironmentVariable("DD_OBSERVATION_JSON_OUT")
            ?? Path.Combine(Path.GetTempPath(), "observations-d2.json");
        var options = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        long compactBytes;
        using (FileStream file = File.Create(outputPath))
        using (var writer = new System.Text.Json.Utf8JsonWriter(file))
        {
            writer.WriteStartArray();
            for (int i = 0; i < observations.Count; i++)
                System.Text.Json.JsonSerializer.Serialize(writer, observations[i], options);
            writer.WriteEndArray();
            writer.Flush();
            compactBytes = file.Length;
        }

        long gzipBytes;
        using (FileStream source = File.OpenRead(outputPath))
        using (var sizeCounter = new CountingStream())
        {
            using (var gzip = new System.IO.Compression.GZipStream(sizeCounter, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                source.CopyTo(gzip);
            gzipBytes = sizeCounter.Length;
        }

        _output.WriteLine($"Serialized observations (compact JSON): {compactBytes / 1024.0 / 1024.0:N1} MB at {outputPath}");
        _output.WriteLine($"Serialized observations (gzip): {gzipBytes / 1024.0 / 1024.0:N1} MB");
        _output.WriteLine($"Compact JSON bytes per observation: {(double)compactBytes / observations.Count:N0}");
    }

    private sealed class CountingStream : Stream
    {
        private long _length;
        public override long Length => _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    }
}
