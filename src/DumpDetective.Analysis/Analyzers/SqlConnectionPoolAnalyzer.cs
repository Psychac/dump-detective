using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Scans the managed heap for ADO.NET connection-pool manager objects and reads their exact
/// current-size/max-pool-size counters directly (R11,
/// docs/analysis/phase1/DbConnectionAnalyzer-audit.md). This is exact pool-utilisation evidence,
/// not an estimate derived from sampling connection objects — matching what a WinDbg/SOS
/// investigation would have to reconstruct field-by-field.
///
/// Both <c>System.Data.SqlClient</c> and <c>Microsoft.Data.SqlClient</c> share the exact same
/// pool-manager implementation (copied internal type, same field names) under
/// <c>System.Data.ProviderBase.DbConnectionPool</c> / <c>Microsoft.Data.ProviderBase.DbConnectionPool</c>
/// respectively — there is no per-provider pool subclass to match by namespace prefix, so this
/// analyzer matches those two fully-qualified type names directly. Other providers (Npgsql,
/// MySql, Oracle, Sqlite) use unrelated internal pooling designs and are out of scope.
/// </summary>
public sealed class SqlConnectionPoolAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant, ITypedResourceCandidateSource
{
    public string Name => "SQL Connection Pool Analysis";
    public string Category => "Infrastructure";

    private const double NearCapacityUtilizationPct = 80.0;

    private static readonly HashSet<string> PoolTypeNames = new(StringComparer.Ordinal)
    {
        "System.Data.ProviderBase.DbConnectionPool",
        "Microsoft.Data.ProviderBase.DbConnectionPool",
    };

    public bool IsCandidateType(string typeName) => PoolTypeNames.Contains(typeName);

    private ClrHeap? _heap;
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private readonly List<SqlConnectionPoolSnapshot> _pools = [];

    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        ClrHeap heap = context.Heap;
        _heap = heap;
        _candidateMts = TypedResourceScanDriver.DiscoverCandidates(this, heap, context.Cache);
    }

    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => OnHeapEntry(in entry);

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() =>
        new SqlConnectionPoolAnalyzer();

    // Every pool object address is discovered by exactly one worker (disjoint index shards), so
    // merging is a plain concatenation — no per-type/per-address dedup key needed.
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        foreach (IHeapIndexScanParticipant p in partials)
        {
            var other = (SqlConnectionPoolAnalyzer)p;
            _pools.AddRange(other._pools);
        }
    }

    private void OnHeapEntry(in HeapEntry entry)
    {
        var candidateMts = _candidateMts!;
        if (!candidateMts.TryGetValue(entry.MethodTable, out var candidate)) return;

        SqlConnectionPoolSnapshot? snap = TryReadPoolSnapshot(_heap!, entry.Address, candidate.TypeName);
        if (snap is not null)
            _pools.Add(snap);
    }

    private static SqlConnectionPoolSnapshot? TryReadPoolSnapshot(ClrHeap heap, ulong address, string typeName)
    {
        try
        {
            ClrObject pool = heap.GetObject(address);
            if (!pool.IsValid || pool.Type == null)
                return null;

            ClrInstanceField? totalObjectsField = pool.Type.GetFieldByName("_totalObjects");
            if (totalObjectsField == null || totalObjectsField.ElementType != ClrElementType.Int32)
                return null;
            int currentSize = totalObjectsField.Read<int>(address, interior: false);

            (int maxPoolSize, int minPoolSize) = TryReadPoolSizeLimits(pool);
            string? anonymisedConnStr = TryReadAnonymisedConnectionString(pool);

            return new SqlConnectionPoolSnapshot(typeName, address, currentSize, maxPoolSize, minPoolSize, anonymisedConnStr);
        }
        catch
        {
            // Corrupt/unloaded object — treat as unreadable, not a crash.
            return null;
        }
    }

    // Pool -> _connectionPoolGroupOptions (DbConnectionPoolGroupOptions) -> _maxPoolSize/_minPoolSize
    private static (int MaxPoolSize, int MinPoolSize) TryReadPoolSizeLimits(ClrObject pool)
    {
        try
        {
            ClrInstanceField? optionsField = pool.Type!.GetFieldByName("_connectionPoolGroupOptions");
            if (optionsField == null) return (-1, -1);

            ClrObject options = optionsField.ReadObject(pool.Address, interior: false);
            if (!options.IsValid || options.Type == null) return (-1, -1);

            ClrInstanceField? maxField = options.Type.GetFieldByName("_maxPoolSize");
            ClrInstanceField? minField = options.Type.GetFieldByName("_minPoolSize");
            int max = maxField != null && maxField.ElementType == ClrElementType.Int32
                ? maxField.Read<int>(options.Address, interior: false) : -1;
            int min = minField != null && minField.ElementType == ClrElementType.Int32
                ? minField.Read<int>(options.Address, interior: false) : -1;
            return (max, min);
        }
        catch
        {
            return (-1, -1);
        }
    }

    // Pool -> _connectionPoolGroup (DbConnectionPoolGroup) -> _connectionOptions (DbConnectionOptions)
    // -> _usersConnectionString
    private static string? TryReadAnonymisedConnectionString(ClrObject pool)
    {
        try
        {
            ClrInstanceField? groupField = pool.Type!.GetFieldByName("_connectionPoolGroup");
            if (groupField == null) return null;

            ClrObject group = groupField.ReadObject(pool.Address, interior: false);
            if (!group.IsValid || group.Type == null) return null;

            ClrInstanceField? connOptionsField = group.Type.GetFieldByName("_connectionOptions");
            if (connOptionsField == null) return null;

            ClrObject connOptions = connOptionsField.ReadObject(group.Address, interior: false);
            if (!connOptions.IsValid || connOptions.Type == null) return null;

            ClrInstanceField? connStringField = connOptions.Type.GetFieldByName("_usersConnectionString");
            if (connStringField == null) return null;

            ClrObject connStringObj = connStringField.ReadObject(connOptions.Address, interior: false);
            return connStringObj.IsValid && connStringObj.AsString() is string connStr
                ? ConnectionStringAnonymiser.Anonymise(connStr)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BuildResult().Stamp(this));
    }

    private SqlConnectionPoolDomainResult BuildResult()
    {
        if (_pools.Count == 0)
            return Empty();

        int nearCapacity = 0;
        foreach (SqlConnectionPoolSnapshot pool in _pools)
        {
            if (UtilizationPercent(pool) >= NearCapacityUtilizationPct)
                nearCapacity++;
        }

        return new SqlConnectionPoolDomainResult(
            PoolsFound:       true,
            TotalPools:       _pools.Count,
            PoolsNearCapacity: nearCapacity,
            Pools:            _pools);
    }

    /// <summary>Returns -1 when <see cref="SqlConnectionPoolSnapshot.MaxPoolSize"/> couldn't be read.</summary>
    internal static double UtilizationPercent(SqlConnectionPoolSnapshot pool) =>
        pool.MaxPoolSize > 0 ? 100.0 * pool.CurrentSize / pool.MaxPoolSize : -1;

    private static SqlConnectionPoolDomainResult Empty() => new(false, 0, 0, []);
}
