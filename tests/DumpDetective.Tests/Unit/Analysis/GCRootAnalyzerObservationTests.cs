using System.Reflection;

using DumpDetective.Analysis.Analyzers.Gc;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Sdk.Observations;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Phase 5 step 1's pilot (docs/refactor/modularity/phase-5-observations-synthesis.md): proves
/// <see cref="GCRootAnalyzer"/> emits <c>heap.root</c>, <c>root.kind-summary</c> (step 2), and
/// <c>root.population</c> (step 2) observations alongside its unchanged
/// <see cref="GCRootDomainResult"/> — the domain-result assertions in
/// <see cref="GCRootAnalyzerRetypingCharacterizationTests"/> stay green untouched, so this file only
/// covers the new observation-emission behavior, not a duplicate of that test's fixture assertions.
/// </summary>
public sealed class GCRootAnalyzerObservationTests
{
    [Fact]
    public void AnalyzeAsync_WithHeapIndex_EmitsOneHeapRootObservationPerFinding()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            const ulong typeATotalSize = 400_000;
            const ulong typeBTotalSize = 600_000;
            var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
            {
                [0x1000] = new(0x1000, ModuleId: 0, Count: 40, TotalSize: typeATotalSize, LohCount: 0, LohSize: 0,
                    SampleAddress: 0, Gen0Count: 40, Gen1Count: 0, Gen2Count: 0, Flags: TypeAggregateFlags.None),
                [0x2000] = new(0x2000, ModuleId: 0, Count: 60, TotalSize: typeBTotalSize, LohCount: 0, LohSize: 0,
                    SampleAddress: 0, Gen0Count: 60, Gen1Count: 0, Gen2Count: 0, Flags: TypeAggregateFlags.None),
            };

            HeapAnalysisCache cache = new();
            InjectHeapIndex(cache, new HeapIndexBuildResult(
                HeapIndexStorageKind.Disk, IndexPath: string.Empty, ObjectCount: 100,
                Elapsed: TimeSpan.Zero, TypeAggregates: aggregates));

            var sink = new CollectingObservationSink();
            RuntimeAnalysisContext runtimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new(), ObservationSink = sink };
            DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(runtimeCtx, analyzerOptions: null);

            using GCRootAnalyzer adapter = new();
            adapter.AnalyzeAsync(context, CancellationToken.None).GetAwaiter().GetResult();
            var result = (GCRootDomainResult)adapter.LastResult!;

            // Three observation types now (Phase 5 step 2 added root.kind-summary/root.population
            // alongside step 1's heap.root — see GCRootSynthesisRule's own remarks): one heap.root
            // per finding, one root.kind-summary per ByKind row, one whole-run root.population.
            IReadOnlyList<Observation> heapRootObservations = sink.Observations.Where(o => o.ObservationType == "heap.root").ToList();
            IReadOnlyList<Observation> kindSummaryObservations = sink.Observations.Where(o => o.ObservationType == "root.kind-summary").ToList();
            IReadOnlyList<Observation> populationObservations = sink.Observations.Where(o => o.ObservationType == "root.population").ToList();

            sink.Observations.Should().HaveCount(heapRootObservations.Count + kindSummaryObservations.Count + populationObservations.Count);
            heapRootObservations.Should().HaveCount(result.TopRootsBySeverity.Count);
            heapRootObservations.Should().OnlyContain(o => o.Subjects.Count == 1);
            heapRootObservations.Should().OnlyContain(o => o.Measures.ContainsKey("retained.bytes"));
            kindSummaryObservations.Should().HaveCount(result.ByKind.Count);
            kindSummaryObservations.Should().OnlyContain(o => o.Measures.ContainsKey("kind.ordinal") && o.Measures.ContainsKey("count"));
            populationObservations.Should().ContainSingle();
            populationObservations.Should().OnlyContain(o => o.Measures.ContainsKey("total.count"));
            sink.Observations.Should().OnlyContain(o => o.Provenance.AnalyzerKey == "GC Root Analysis");
            sink.Observations.Should().OnlyContain(o => o.Provenance.Artifact == runtimeCtx.ArtifactId);

            // Purity rule (observation-and-correlation-model.md § 2a): raw facts only — never a
            // severity/classification value baked in by this analyzer.
            sink.Observations.Should().OnlyContain(o => !o.Measures.Keys.Any(k =>
                k.Contains("severity", StringComparison.OrdinalIgnoreCase) || k.Contains("score", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    [Fact]
    public void AnalyzeAsync_WithoutHeapIndex_EmitsNoObservations()
    {
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            HeapAnalysisCache cache = new();
            var sink = new CollectingObservationSink();
            RuntimeAnalysisContext runtimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new(), ObservationSink = sink };
            DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(runtimeCtx, analyzerOptions: null);

            using GCRootAnalyzer adapter = new();
            adapter.AnalyzeAsync(context, CancellationToken.None).GetAwaiter().GetResult();

            sink.Observations.Should().BeEmpty();
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    private static void InjectHeapIndex(HeapAnalysisCache cache, HeapIndexBuildResult result)
    {
        typeof(HeapAnalysisCache)
            .GetField("_heapIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cache, result);
    }
}
