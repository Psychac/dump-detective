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
/// proves <see cref="SqlTransactionAnalyzer"/> reproduces the pre-retyping analyzer's
/// behavior against a real, large dump. Same structure as <c>SqlConnectionPoolAnalyzerRealDumpTests</c>.
/// </summary>
public sealed class SqlTransactionAnalyzerRealDumpTests
{
    private static readonly string[] TransactionNamespacePrefixes =
    [
        "System.Data.SqlClient.", "Microsoft.Data.SqlClient.", "System.Data.OleDb.", "System.Data.Odbc.",
        "Oracle.", "Npgsql.", "MySql.", "Microsoft.Data.Sqlite.", "System.Data.SQLite.",
    ];

    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public SqlTransactionAnalyzerRealDumpTests(ITestOutputHelper output)
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
                && typeName.EndsWith("Transaction", StringComparison.Ordinal)
                && Array.Exists(TransactionNamespacePrefixes, p => typeName.StartsWith(p, StringComparison.Ordinal)))
                expectedCandidateCount++;
        }

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        using SqlTransactionAnalyzer adapter = new();
        new HeapIndexScanDispatcher().Run(cache, contextRuntimeCtx, [adapter], CancellationToken.None);

        context = ((ICustomizesAnalysisContext)adapter).BuildAnalysisContext(contextRuntimeCtx);

        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (SqlTransactionDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("SQL Transaction Analysis");

        if (expectedCandidateCount == 0)
        {
            result.TransactionsFound.Should().BeFalse();
            _output.WriteLine("No transaction-shaped objects found on this dump's heap — empty result is correct.");
            return;
        }

        result.TransactionsFound.Should().BeTrue();
        result.TotalTransactions.Should().Be(expectedCandidateCount);
        (result.ActiveCount + result.DisposedCount + result.OtherCount).Should().Be(result.TotalTransactions);

        int sumByType = 0;
        foreach (SqlTransactionTypeSummary t in result.ByType) sumByType += t.TotalCount;
        sumByType.Should().Be(result.TotalTransactions);

        for (int i = 1; i < result.ByType.Count; i++)
            result.ByType[i].TotalCount.Should().BeLessThanOrEqualTo(result.ByType[i - 1].TotalCount);

        _output.WriteLine(
            $"TotalTransactions={result.TotalTransactions} Active={result.ActiveCount} Disposed={result.DisposedCount} " +
            $"Other={result.OtherCount} Types={result.ByType.Count} TopActive={result.TopActiveTransactions.Count}");
    }
}
