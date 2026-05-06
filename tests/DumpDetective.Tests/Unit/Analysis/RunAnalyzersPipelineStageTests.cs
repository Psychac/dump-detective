using System.Reflection;
using DumpDetective.Cli.Pipeline.Stages;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Options;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Pipeline;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class RunAnalyzersPipelineStageTests
{
    [Fact]
    public void BuildContext_Derives_SamplingSeed_From_DumpPath_When_Zero()
    {
        // Arrange
        var resolved = ResolvedExecutionOptionsFactory.Create("out.json");
        // ensure thread options seed is zero (auto-derive)
        var threadOptions = new ThreadAnalysisOptions { SamplingSeed = 0 };
        resolved = resolved with { ThreadAnalysis = threadOptions };

        var state = new SingleDumpPipelineState
        {
            Resolved = resolved,
            ActiveAnalyzers = System.Array.Empty<IAnalyzer>(),
            HeapCache = new FakeHeapCache(),
            LoadContext = CreateDumpLoadContext("C:\\dumps\\sample1.dmp")
        };

        // Act
        var ctx = InvokeBuildContext(state);
        var opt = (ThreadAnalysisOptions)ctx.Options[typeof(ThreadAnalysisOptions)];

        // Assert
        opt.SamplingSeed.Should().NotBe(0);

        // Re-run to ensure determinism for same path
        var state2 = new SingleDumpPipelineState
        {
            Resolved = resolved,
            ActiveAnalyzers = System.Array.Empty<IAnalyzer>(),
            HeapCache = new FakeHeapCache(),
            LoadContext = CreateDumpLoadContext("C:\\dumps\\sample1.dmp")
        };

        var ctx2 = InvokeBuildContext(state2);
        var opt2 = (ThreadAnalysisOptions)ctx2.Options[typeof(ThreadAnalysisOptions)];

        opt2.SamplingSeed.Should().Be(opt.SamplingSeed);
    }

    [Fact]
    public void BuildContext_Produces_Different_Seeds_For_Different_Paths()
    {
        var resolved = ResolvedExecutionOptionsFactory.Create("out.json");
        var threadOptions = new ThreadAnalysisOptions { SamplingSeed = 0 };
        resolved = resolved with { ThreadAnalysis = threadOptions };

        var s1 = new SingleDumpPipelineState
        {
            Resolved = resolved,
            ActiveAnalyzers = System.Array.Empty<IAnalyzer>(),
            HeapCache = new FakeHeapCache(),
            LoadContext = CreateDumpLoadContext("C:\\dumps\\a.dmp")
        };

        var s2 = new SingleDumpPipelineState
        {
            Resolved = resolved,
            ActiveAnalyzers = System.Array.Empty<IAnalyzer>(),
            HeapCache = new FakeHeapCache(),
            LoadContext = CreateDumpLoadContext("C:\\dumps\\b.dmp")
        };

        var c1 = InvokeBuildContext(s1);
        var c2 = InvokeBuildContext(s2);

        var o1 = (ThreadAnalysisOptions)c1.Options[typeof(ThreadAnalysisOptions)];
        var o2 = (ThreadAnalysisOptions)c2.Options[typeof(ThreadAnalysisOptions)];

        o1.SamplingSeed.Should().NotBe(o2.SamplingSeed);
    }

    private static RuntimeAnalysisContext InvokeBuildContext(SingleDumpPipelineState state)
    {
        var t = typeof(RunAnalyzersPipelineStage);
        var mi = t.GetMethod("BuildContext", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = mi.Invoke(null, new object[] { state });
        return (RuntimeAnalysisContext)result!;
    }

    private static DumpDetective.Analysis.Dump.DumpLoadContext CreateDumpLoadContext(string path)
    {
        var assembly = typeof(DumpDetective.Analysis.Dump.DumpLoader).Assembly;
        var dlcType = assembly.GetType("DumpDetective.Analysis.Dump.DumpLoadContext")!;
        // Invoke primary constructor with (string, DataTarget, ClrRuntime, ClrHeap)
        var ctor = dlcType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First();
        var instance = ctor.Invoke(new object?[] { path, null, null, null });
        return (DumpDetective.Analysis.Dump.DumpLoadContext)instance!;
    }

    private sealed class FakeHeapCache : IHeapAnalysisCache
    {
        public long ObjectScanCount => 0;
        public long CacheHits => 0;
        public long CacheMisses => 0;
        public DumpDetective.Core.Models.DumpSizeTier SizeTier => DumpDetective.Core.Models.DumpSizeTier.Small;
        public HashSet<ulong> GetStaticRootedAddresses(Microsoft.Diagnostics.Runtime.ClrHeap heap) => new();
        public Dictionary<string, DumpDetective.Core.Models.CachedTypeStatistics> GetOrBuildTypeStatistics(Microsoft.Diagnostics.Runtime.ClrHeap heap) => new();
        public ulong? GetSampleInstanceAddress(string typeName) => null;
        public HashSet<ulong> GetRetainedObjects(Microsoft.Diagnostics.Runtime.ClrHeap heap, ulong rootAddress, int maxObjects = 10000) => new();
        public IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(Microsoft.Diagnostics.Runtime.ClrHeap heap) => Array.Empty<(string, ulong)>();
        public string? GetRootDescription(ulong address) => null;
        public int GetOrCountThreadStackRoots(Microsoft.Diagnostics.Runtime.ClrThread thread, int maxStackRootsToCount) => 0;
        public bool MethodTableHasOutgoingRefs(Microsoft.Diagnostics.Runtime.ClrHeap heap, ulong methodTable) => false;
        public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples() => Array.Empty<(ulong, ulong, ulong)>();
    }
}
