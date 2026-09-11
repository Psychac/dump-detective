using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-2 wave (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="SqlCommandAnalyzerLegacyAdapter"/> reproduces the pre-retyping analyzer's
/// behavior against a real, large dump — real shared heap-index scan, real
/// <c>dump.object-fields</c> reads. Same structure as <c>SqlConnectionPoolAnalyzerRealDumpTests</c>.
/// </summary>
public sealed class SqlCommandAnalyzerRealDumpTests
{
    private static readonly string[] CommandNamespacePrefixes =
    [
        "System.Data.SqlClient.", "Microsoft.Data.SqlClient.", "System.Data.OleDb.", "System.Data.Odbc.",
        "Oracle.", "Npgsql.", "MySql.", "Microsoft.Data.Sqlite.", "System.Data.SQLite.",
    ];

    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public SqlCommandAnalyzerRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task AnalyzeAsync_RealDump_ProducesInternallyConsistentResult()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        int expectedCandidateCount = 0;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.IsValid && obj.Type?.Name is string typeName
                && typeName.EndsWith("Command", StringComparison.Ordinal)
                && Array.Exists(CommandNamespacePrefixes, p => typeName.StartsWith(p, StringComparison.Ordinal)))
                expectedCandidateCount++;
        }

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using SqlCommandAnalyzerLegacyAdapter adapter = new();
        new HeapIndexScanDispatcher().Run(cache, context, [adapter], CancellationToken.None);

        var result = (SqlCommandDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("SQL Command Analysis");

        if (expectedCandidateCount == 0)
        {
            result.CommandsFound.Should().BeFalse();
            _output.WriteLine("No command-shaped objects found on this dump's heap — empty result is correct.");
            return;
        }

        result.CommandsFound.Should().BeTrue();
        result.TotalCommands.Should().Be(expectedCandidateCount);
        (result.ActiveCount + result.DisposedCount).Should().BeLessThanOrEqualTo(result.TotalCommands);

        int sumByType = 0;
        foreach (SqlCommandTypeSummary t in result.ByType) sumByType += t.TotalCount;
        sumByType.Should().Be(result.TotalCommands);

        for (int i = 1; i < result.ByType.Count; i++)
            result.ByType[i].TotalCount.Should().BeLessThanOrEqualTo(result.ByType[i - 1].TotalCount);

        _output.WriteLine(
            $"TotalCommands={result.TotalCommands} Active={result.ActiveCount} Disposed={result.DisposedCount} " +
            $"Types={result.ByType.Count} TopActive={result.TopActiveCommands.Count}");
    }
}
