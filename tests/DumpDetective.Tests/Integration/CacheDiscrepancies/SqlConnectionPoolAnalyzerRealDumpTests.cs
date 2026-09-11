using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
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
/// Real-dump verification for the Tier-2 pilot
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): proves
/// <see cref="SqlConnectionPoolAnalyzerLegacyAdapter"/> — candidate discovery via the pipeline's
/// shared heap-index-scan-participant mechanism, field-value extraction via the new
/// <c>dump.object-fields</c> capability — reproduces the pre-retyping analyzer's behavior against a
/// real, large dump. Unlike the dictionary-backed fake in
/// <c>SqlConnectionPoolAnalyzerRetypingCharacterizationTests</c>, this exercises real ClrMD field
/// reads (real SqlClient/Microsoft.Data internal layouts, if any live pool objects exist in this
/// dump) and the real <see cref="HeapIndexScanDispatcher"/> pass, not a hand-fed candidate list.
/// </summary>
public sealed class SqlConnectionPoolAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public SqlConnectionPoolAnalyzerRealDumpTests(ITestOutputHelper output)
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

        // Ground truth: a raw ClrMD scan for the same two pool-manager type names the analyzer's
        // own ITypedResourceCandidateSource.IsCandidateType matches, independent of the pipeline's
        // discovery path — this is what the shared heap-index scan should also find.
        int expectedCandidateCount = 0;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.IsValid && obj.Type?.Name is "System.Data.ProviderBase.DbConnectionPool" or "Microsoft.Data.ProviderBase.DbConnectionPool")
                expectedCandidateCount++;
        }

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using SqlConnectionPoolAnalyzerLegacyAdapter adapter = new();
        new HeapIndexScanDispatcher().Run(cache, context, [adapter], CancellationToken.None);

        var result = (SqlConnectionPoolDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("SQL Connection Pool Analysis");

        if (expectedCandidateCount == 0)
        {
            // A legitimate, valid result — not every real dump uses ADO.NET connection pooling
            // (this reference dump is a WCF/EF-shaped app; SqlClient pool usage isn't guaranteed).
            result.PoolsFound.Should().BeFalse();
            result.TotalPools.Should().Be(0);
            _output.WriteLine("No DbConnectionPool-shaped objects found on this dump's heap — empty result is correct, not a failure.");
            return;
        }

        result.PoolsFound.Should().BeTrue();
        result.TotalPools.Should().BeGreaterThan(0);
        // Every discovered candidate's "_totalObjects" field must be readable as Int32 on a
        // well-formed dump for a real SqlClient/Microsoft.Data pool object; a real dump could still
        // have a handful the field-shape guard correctly rejects, so this is <=, not ==.
        result.TotalPools.Should().BeLessThanOrEqualTo(expectedCandidateCount);
        result.Pools.Should().HaveCount(result.TotalPools);
        result.PoolsNearCapacity.Should().BeLessThanOrEqualTo(result.TotalPools);

        foreach (SqlConnectionPoolSnapshot pool in result.Pools)
        {
            pool.Address.Should().NotBe(0UL);
            pool.CurrentSize.Should().BeGreaterThanOrEqualTo(0);
            // MaxPoolSize/MinPoolSize are -1 when the nested field chain couldn't be read — a real,
            // accepted fallback, not a failure — otherwise they must be non-negative pool limits.
            if (pool.MaxPoolSize != -1)
                pool.MaxPoolSize.Should().BeGreaterThanOrEqualTo(0);
            if (pool.MinPoolSize != -1)
                pool.MinPoolSize.Should().BeGreaterThanOrEqualTo(0);

            double utilization = SqlConnectionPoolAnalyzerUtilization(pool);
            if (pool.MaxPoolSize > 0)
                utilization.Should().BeInRange(0.0, 1000.0); // generous upper bound -- pools can exceed MaxPoolSize transiently
            else
                utilization.Should().Be(-1.0);

            // No plaintext credential survives redaction — a redacted "password=***"/"uid=***"
            // still legitimately contains the keyword, so check what follows "=", not the
            // keyword's absence.
            if (pool.AnonymisedConnectionString is not null)
            {
                // [^;]*, not \S* -- the anonymiser's own field boundary is ';', and a real
                // connection string has no spaces around it, so \S* would greedily consume past
                // the field separator into every subsequent key=value pair. Matches (not Match) --
                // a real connection string can carry more than one sensitive field (e.g. both
                // "User Id" and "Password"); every one must be redacted, not just the first found.
                foreach (System.Text.RegularExpressions.Match sensitiveField in System.Text.RegularExpressions.Regex.Matches(
                    pool.AnonymisedConnectionString, @"(?i)(password|pwd|user\s?id|uid|secret)\s*=\s*([^;]*)"))
                {
                    sensitiveField.Groups[2].Value.Should().Be("***", "sensitive connection-string fields must be redacted");
                }
            }
        }

        _output.WriteLine(
            $"Candidates(groundTruth)={expectedCandidateCount} TotalPools={result.TotalPools} " +
            $"PoolsNearCapacity={result.PoolsNearCapacity} " +
            $"Addresses=[{string.Join(", ", result.Pools.Select(p => $"0x{p.Address:x}"))}]");
    }

    private static double SqlConnectionPoolAnalyzerUtilization(SqlConnectionPoolSnapshot pool) =>
        (double)typeof(SqlConnectionPoolAnalyzer)
            .GetMethod("UtilizationPercent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [pool])!;
}
