using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Scans the managed heap for ADO.NET command objects (SqlCommand/IDbCommand implementations).
/// Reports outstanding commands still wired to a connection — a large population indicates
/// command objects held onto (e.g. cached on a long-lived owner) rather than short-lived
/// per-call instances, which retains parameter/result-set state and connection references.
///
/// Same namespace-prefix providers as <see cref="DbConnectionAnalyzer"/>/<see cref="SqlTransactionAnalyzer"/>.
/// Unlike transactions, ADO.NET providers do not reliably null out a command's internal
/// connection-reference field on <c>Dispose()</c>, so "Active" here means "still references a
/// connection object", not strictly "not yet disposed".
/// </summary>
public sealed class SqlCommandAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant, ITypedResourceCandidateSource, ITypedResourceInstanceSampler<SqlCommandSnapshot>
{
    public string Name => "SQL Command Analysis";
    public string Category => "Infrastructure";

    private const int StateDisposed = 0;
    private const int StateActive = 1;

    // Same provider namespaces as DbConnectionAnalyzer/SqlTransactionAnalyzer.
    private static readonly string[] CommandNamespacePrefixes =
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
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, CommandNamespacePrefixes, "Command", null);

    // Field names tried in order when reading the owning connection reference.
    private static readonly string[] ConnectionFieldNames = ["_activeConnection", "_connection", "Connection"];

    SqlCommandSnapshot? ITypedResourceInstanceSampler<SqlCommandSnapshot>.TrySample(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        int stateVal = TryReadCommandState(heap, entry.Address);
        string stateLabel = stateVal == StateActive ? "Active" : stateVal == StateDisposed ? "Disposed" : "Other";
        return new SqlCommandSnapshot(typeName, entry.Address, stateLabel, stateVal);
    }

    private static int TryReadCommandState(ClrHeap heap, ulong address)
    {
        try
        {
            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type == null)
                return -1;

            for (int i = 0; i < ConnectionFieldNames.Length; i++)
            {
                ClrInstanceField? field = obj.Type.GetFieldByName(ConnectionFieldNames[i]);
                if (field == null)
                    continue;

                ClrObject connectionObj = field.ReadObject(address, interior: false);
                return connectionObj.IsValid && connectionObj.Type != null ? StateActive : StateDisposed;
            }

            return -1;
        }
        catch
        {
            // Corrupt/unloaded object — treat as unreadable state, not a crash.
            return -1;
        }
    }

    // Instance accumulator state for the IHeapIndexScanParticipant path. Mirrors
    // SqlTransactionAnalyzer's/DbConnectionAnalyzer's shared typed-resource quartet wiring.
    private ClrHeap? _heap;
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private Dictionary<ulong, (string Name, int Total, int Active, int Disposed, int Other, ulong Bytes)>? _typeStats;
    private InstanceStateSampler<SqlCommandSnapshot>? _sampler;

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
        new SqlCommandAnalyzer();

    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        var typeStats = _typeStats!;
        var sampler = _sampler!;

        foreach (IHeapIndexScanParticipant p in partials)
        {
            var other = (SqlCommandAnalyzer)p;
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

        SqlCommandSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, _heap!, in entry, typeName);

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

    private SqlCommandDomainResult BuildResult()
    {
        if (_typeStats is null || _typeStats.Count == 0)
            return Empty();

        int totalCommands = 0, totalActive = 0, totalDisposed = 0;
        var byType = new List<SqlCommandTypeSummary>(_typeStats.Count);

        foreach (var kv in _typeStats)
        {
            var ts = kv.Value;
            byType.Add(new SqlCommandTypeSummary(ts.Name, ts.Total, ts.Disposed, ts.Active, ts.Bytes));
            totalCommands += ts.Total;
            totalActive   += ts.Active;
            totalDisposed += ts.Disposed;
        }

        byType.Sort(static (a, b) => b.TotalCount.CompareTo(a.TotalCount));

        return new SqlCommandDomainResult(
            CommandsFound:     totalCommands > 0,
            TotalCommands:     totalCommands,
            DisposedCount:     totalDisposed,
            ActiveCount:       totalActive,
            ByType:            byType,
            TopActiveCommands: _sampler?.TopSamples ?? []);
    }

    private static SqlCommandDomainResult Empty() =>
        new(false, 0, 0, 0, [], []);
}
