using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Phase 1 retyping pilot
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): proves
/// <see cref="GCGenerationAnalyzerLegacyAdapter"/> — the new capability-scoped
/// <see cref="GCGenerationAnalyzer"/>, wrapped to run through the unchanged
/// <c>Core.Abstractions.IAnalyzer</c> pipeline — reproduces the exact arithmetic the pre-retyping
/// analyzer body implemented (verified by hand-computing expected values from the injected fixture
/// data below, since that pre-retyping body no longer exists to diff against directly). No prior
/// per-analyzer unit test existed for this analyzer to diff against; this is the first.
/// </summary>
public sealed class GCGenerationAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public void AnalyzeAsync_FastPath_MatchesHandComputedArithmetic()
    {
        // Not LiveHeapSnapshotFixture.AttachToSelf() — that discards the owning ClrRuntime, and
        // Core.Abstractions.AnalysisContext.Runtime is required.
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            ulong[] methodTables = FindThreeDistinctMethodTables(heap);
            (ulong mtA, ulong mtB, ulong mtC) = (methodTables[0], methodTables[1], methodTables[2]);
            string nameA = heap.GetTypeByMethodTable(mtA)!.Name!;
            string nameB = heap.GetTypeByMethodTable(mtB)!.Name!;
            string nameC = heap.GetTypeByMethodTable(mtC)!.Name!;

            var aggregates = new Dictionary<ulong, TypeAggregateIndexEntry>
            {
                // TypeA: ordinary SOH type, non-finalizable, some Gen2 bytes.
                [mtA] = new(mtA, ModuleId: 0, Count: 13, TotalSize: 1300, LohCount: 0, LohSize: 0,
                    SampleAddress: 0, Gen0Count: 10, Gen1Count: 2, Gen2Count: 1,
                    Flags: TypeAggregateFlags.None, Gen2TotalSize: 100),
                // TypeB: finalizable, has both Gen2 and LOH instances.
                [mtB] = new(mtB, ModuleId: 0, Count: 8, TotalSize: 300500, LohCount: 3, LohSize: 300000,
                    SampleAddress: 0, Gen0Count: 0, Gen1Count: 0, Gen2Count: 5,
                    Flags: TypeAggregateFlags.IsFinalizableType, Gen2TotalSize: 500),
                // TypeC: Gen0-only, no Gen2 bytes at all.
                [mtC] = new(mtC, ModuleId: 0, Count: 1000, TotalSize: 8000, LohCount: 0, LohSize: 0,
                    SampleAddress: 0, Gen0Count: 1000, Gen1Count: 0, Gen2Count: 0,
                    Flags: TypeAggregateFlags.None, Gen2TotalSize: 0),
            };

            HeapAnalysisCache cache = new();
            InjectHeapIndex(cache, new HeapIndexBuildResult(
                HeapIndexStorageKind.Disk, IndexPath: string.Empty, ObjectCount: 1021,
                Elapsed: TimeSpan.Zero, TypeAggregates: aggregates));

            AnalysisOptions options = new()
            {
                GCGenerationAnalysis = new GCGenerationAnalysisOptions { LohThresholdPercent = 33.3 },
            };
            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = options };

            GCGenerationAnalyzerLegacyAdapter adapter = new();
            GCGenerationDomainResult result = (GCGenerationDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            AnalyzerHelpers.ComputeExactGenBytes(heap, out ulong expectedGen0Bytes, out ulong expectedGen1Bytes, out ulong expectedGen2Bytes);

            result.AnalyzerName.Should().Be("GC Generation Analysis");
            result.Category.Should().Be("GC");
            result.FallbackMode.Should().BeFalse();
            result.GenBytesAreApproximate.Should().BeFalse();

            // Exact per-generation SOH bytes come straight from AnalyzerHelpers.ComputeExactGenBytes
            // (unchanged, real code) via the new heap.types capability surface — cross-checked
            // directly, not hardcoded, since it depends on this test process's own live heap state.
            result.Gen0Bytes.Should().Be(expectedGen0Bytes);
            result.Gen1Bytes.Should().Be(expectedGen1Bytes);
            result.Gen2Bytes.Should().Be(expectedGen2Bytes);

            // Everything below is hand-computed from the injected fixture data above.
            result.Gen0Objects.Should().Be(1010); // 10 + 0 + 1000
            result.Gen1Objects.Should().Be(2);     // 2 + 0 + 0
            result.Gen2Objects.Should().Be(6);     // 1 + 5 + 0
            result.LohBytes.Should().Be(300000);
            result.LohObjects.Should().Be(3);
            result.TotalObjects.Should().Be(1021); // 13 + 8 + 1000

            ulong expectedManagedBytes = expectedGen0Bytes + expectedGen1Bytes + expectedGen2Bytes + 300000;
            double expectedLohPct = expectedManagedBytes == 0 ? 0.0 : 300000 * 100.0 / expectedManagedBytes;
            result.LohPercent.Should().BeApproximately(expectedLohPct, 0.0001);
            result.Gen2Pct.Should().BeApproximately(6 * 100.0 / 1021, 0.0001);

            result.TopLohTypes.Should().ContainSingle();
            result.TopLohTypes[0].TypeName.Should().Be(nameB);
            result.TopLohTypes[0].TotalBytes.Should().Be(300000);

            result.PerTypeGenerationProfiles.Should().HaveCount(3);
            // Sorted by exact Gen2 bytes descending: TypeB (500) > TypeA (100) > TypeC (0).
            result.PerTypeGenerationProfiles![0].TypeName.Should().Be(nameB);
            result.PerTypeGenerationProfiles[0].IsFinalizable.Should().BeTrue();
            result.PerTypeGenerationProfiles[1].TypeName.Should().Be(nameA);
            result.PerTypeGenerationProfiles[1].IsFinalizable.Should().BeFalse();
            result.PerTypeGenerationProfiles[2].TypeName.Should().Be(nameC);

            result.FinalizableGen2Count.Should().Be(5);
            result.FinalizableGen2Bytes.Should().Be(500);

            // Options flowed through the new AnalysisContext.AnalyzerOptions slot, not defaults.
            result.LohThresholdPercent.Should().Be(33.3);
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    [Fact]
    public void AnalyzeAsync_FallbackPath_MatchesHandComputedArithmetic()
    {
        // Not LiveHeapSnapshotFixture.AttachToSelf() — that discards the owning ClrRuntime, and
        // Core.Abstractions.AnalysisContext.Runtime is required.
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        try
        {
            // No heap index injected — HeapTypeStatisticsQuery.HasExactGenerationData is false, so
            // the analyzer takes the coarse fallback path (no per-generation breakdown).
            HeapAnalysisCache cache = new();

            AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new AnalysisOptions() };

            GCGenerationAnalyzerLegacyAdapter adapter = new();
            GCGenerationDomainResult result = (GCGenerationDomainResult)adapter.AnalyzeAsync(context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            result.FallbackMode.Should().BeTrue();
            result.GenBytesAreApproximate.Should().BeTrue();
            result.Gen0Bytes.Should().Be(0);
            result.Gen1Bytes.Should().Be(0);
            result.Gen0Objects.Should().Be(0);
            result.Gen1Objects.Should().Be(0);
            result.PerTypeGenerationProfiles.Should().BeEmpty();
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    private static ulong[] FindThreeDistinctMethodTables(ClrHeap heap)
    {
        var found = new List<ulong>(3);
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null || obj.Type.MethodTable == 0)
                continue;

            if (!found.Contains(obj.Type.MethodTable))
                found.Add(obj.Type.MethodTable);

            if (found.Count == 3)
                return found.ToArray();
        }

        throw new InvalidOperationException("Live test-process heap did not expose 3 distinct method tables.");
    }

    private static void InjectHeapIndex(HeapAnalysisCache cache, HeapIndexBuildResult result)
    {
        typeof(HeapAnalysisCache)
            .GetField("_heapIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cache, result);
    }
}
