using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Scans the managed heap for ADO.NET transaction objects (SqlTransaction/IDbTransaction
/// implementations). Detects long-held transactions that prevent connection pool return —
/// a transaction still referencing its owning connection is a strong leak/anti-pattern signal
/// distinct from an ordinary open-but-idle connection.
///
/// Same namespace-prefix providers as <see cref="DbConnectionAnalyzer"/>. A transaction is
/// classified Active when its internal <c>_connection</c> field still references a live
/// connection object (Commit/Rollback/Dispose null it out on completion) and Disposed otherwise.
/// </summary>
public sealed class SqlTransactionAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant, ITypedResourceCandidateSource, ITypedResourceInstanceSampler<SqlTransactionSnapshot>
{
    public string Name => "SQL Transaction Analysis";
    public string Category => "Infrastructure";

    private const int StateDisposed = 0;
    private const int StateActive = 1;

    // Same provider namespaces as DbConnectionAnalyzer — transaction types live alongside
    // connection types in each provider's assembly.
    private static readonly string[] TransactionNamespacePrefixes =
    [
        "System.Data.SqlClient.",
        "Microsoft.Data.SqlClient.",
        "System.Data.OleDb.",
        "System.Data.Odbc.",
        "Oracle.",
        "Npgsql.",
        "MySql.",
        "Microsoft.Data.Sqlite.",
        "System.Data.SQLite.",
    ];

    public bool IsCandidateType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, TransactionNamespacePrefixes, "Transaction", null);

    // Field names tried in order when reading the owning connection reference.
    private static readonly string[] ConnectionFieldNames = ["_connection", "_internalConnection", "Connection"];

    SqlTransactionSnapshot? ITypedResourceInstanceSampler<SqlTransactionSnapshot>.TrySample(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        (int stateVal, ulong? connectionAddress) = TryReadTransactionState(heap, entry.Address);
        string stateLabel = stateVal == StateActive ? "Active" : stateVal == StateDisposed ? "Disposed" : "Other";
        return new SqlTransactionSnapshot(typeName, entry.Address, stateLabel, stateVal, connectionAddress);
    }

    private static (int StateValue, ulong? ConnectionAddress) TryReadTransactionState(ClrHeap heap, ulong address)
    {
        try
        {
            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type == null)
                return (-1, null);

            for (int i = 0; i < ConnectionFieldNames.Length; i++)
            {
                ClrInstanceField? field = obj.Type.GetFieldByName(ConnectionFieldNames[i]);
                if (field == null)
                    continue;

                ClrObject connectionObj = field.ReadObject(address, interior: false);
                return connectionObj.IsValid && connectionObj.Type != null
                    ? (StateActive, connectionObj.Address)
                    : (StateDisposed, null);
            }

            return (-1, null);
        }
        catch
        {
            // Corrupt/unloaded object — treat as unreadable state, not a crash.
            return (-1, null);
        }
    }

    // Instance accumulator state for the IHeapIndexScanParticipant path. Mirrors
    // DbConnectionAnalyzer's shared typed-resource quartet wiring.
    private ClrHeap? _heap;
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private Dictionary<ulong, (string Name, int Total, int Active, int Disposed, int Other, ulong Bytes)>? _typeStats;
    private InstanceStateSampler<SqlTransactionSnapshot>? _sampler;

    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        ClrHeap heap = context.Heap;
        _heap = heap;

        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> candidateMts =
            TypedResourceScanDriver.DiscoverCandidates(this, heap, context.Cache);
        _candidateMts = candidateMts;

        var typeStats = new Dictionary<ulong, (string Name, int Total, int Active, int Disposed, int Other, ulong Bytes)>(candidateMts.Count);
        foreach (KeyValuePair<ulong, (string TypeName, long Count, ulong Bytes)> kv in candidateMts)
        {
            int total = (int)Math.Min(kv.Value.Count, int.MaxValue);
            typeStats[kv.Key] = (kv.Value.TypeName, total, 0, 0, 0, kv.Value.Bytes);
        }

        _typeStats = typeStats;
        _sampler = TypedResourceScanDriver.CreateSampler(this);
    }

    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => OnHeapEntry(in entry);

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() =>
        new SqlTransactionAnalyzer();

    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        var typeStats = _typeStats!;
        var sampler = _sampler!;

        foreach (IHeapIndexScanParticipant p in partials)
        {
            var other = (SqlTransactionAnalyzer)p;
            if (other._typeStats is null) continue;

            foreach (var kvp in other._typeStats)
            {
                if (!typeStats.TryGetValue(kvp.Key, out var self))
                {
                    typeStats[kvp.Key] = kvp.Value;
                    continue;
                }

                var o = kvp.Value;
                typeStats[kvp.Key] = (self.Name, self.Total,
                    self.Active + o.Active,
                    self.Disposed + o.Disposed,
                    self.Other + o.Other,
                    self.Bytes);
            }

            if (other._sampler is not null)
                sampler.MergeFrom(other._sampler);
        }
    }

    private void OnHeapEntry(in HeapEntry entry)
    {
        var candidateMts = _candidateMts!;
        var typeStats = _typeStats!;
        var sampler = _sampler!;

        if (!candidateMts.ContainsKey(entry.MethodTable)) return;
        if (!typeStats.TryGetValue(entry.MethodTable, out var ts)) return;
        string typeName = ts.Name;

        SqlTransactionSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, _heap!, in entry, typeName);

        int active = ts.Active; int disposed = ts.Disposed; int other = ts.Other;
        if (snap is not null)
        {
            if (snap.StateValue == StateActive) active++;
            else if (snap.StateValue == StateDisposed) disposed++;
            else other++;
        }
        else
        {
            other++;
        }
        typeStats[entry.MethodTable] = (typeName, ts.Total, active, disposed, other, ts.Bytes);

        if (snap is not null && snap.StateValue == StateActive)
            sampler.AddTopSample(snap);
    }

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BuildResult().Stamp(this));
    }

    private SqlTransactionDomainResult BuildResult()
    {
        if (_typeStats is null || _typeStats.Count == 0)
            return Empty();

        int totalTransactions = 0, totalActive = 0, totalDisposed = 0, totalOther = 0;
        var byType = new List<SqlTransactionTypeSummary>(_typeStats.Count);

        foreach (var kv in _typeStats)
        {
            var ts = kv.Value;
            byType.Add(new SqlTransactionTypeSummary(ts.Name, ts.Total, ts.Disposed, ts.Active, ts.Other, ts.Bytes));
            totalTransactions += ts.Total;
            totalActive        += ts.Active;
            totalDisposed       += ts.Disposed;
            totalOther          += ts.Other;
        }

        byType.Sort(static (a, b) => b.TotalCount.CompareTo(a.TotalCount));

        return new SqlTransactionDomainResult(
            TransactionsFound:      totalTransactions > 0,
            TotalTransactions:      totalTransactions,
            DisposedCount:          totalDisposed,
            ActiveCount:            totalActive,
            OtherCount:             totalOther,
            ByType:                 byType,
            TopActiveTransactions:  _sampler?.TopSamples ?? []);
    }

    private static SqlTransactionDomainResult Empty() =>
        new(false, 0, 0, 0, 0, [], []);
}
