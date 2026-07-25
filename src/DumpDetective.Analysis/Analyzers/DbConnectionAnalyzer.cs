using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Scans the managed heap for ADO.NET and third-party DB connection objects.
/// Detects open/leaked connections indicative of connection pool exhaustion or missing Dispose().
///
/// Supported providers (namespace-prefix matching):
///   System.Data.SqlClient, Microsoft.Data.SqlClient, System.Data.OleDb, System.Data.Odbc,
///   Oracle. (ODP.NET), Npgsql. (PostgreSQL), MySql. (Connector/NET), Microsoft.Data.Sqlite.
///
/// Connection state mapping (System.Data.ConnectionState):
///   Closed=0, Open=1, Connecting=2, Executing=4, Fetching=8, Broken=16
/// </summary>
public sealed class DbConnectionAnalyzer : IAnalyzer, IHeapIndexScanParticipant
{
    public string Name => "DB Connection Analysis";
    public string Category => "Infrastructure";

    // Max per-object state reads to cap ClrMD field access cost on large heaps.
    private const int MaxStateSamples = 500;

    // ADO.NET ConnectionState enum values
    private const int StateOpen      = 1;
    private const int StateClosed    = 0;

    // Namespace prefixes that identify DB connection types
    private static readonly string[] ConnectionNamespacePrefixes =
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

    // Candidate type must end in "Connection" (covers SqlConnection, NpgsqlConnection, etc.)
    private static bool IsConnectionType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, ConnectionNamespacePrefixes, "Connection", null);

    // Field names to try in order when reading connection state
    private static readonly string[] StateFieldNames = ["_connectionState", "_state", "m_connectionState"];

    // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
    // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
    // OnHeapEntry; consumed by AnalyzeAsync once the shared index scan has completed.
    private ClrHeap? _heap;
    private Dictionary<ulong, (string TypeName, TypeAggregateIndexEntry Entry)>? _candidateMts;
    private Dictionary<ulong, (string Name, int Total, int Open, int Closed, int Other, ulong Bytes)>? _typeStats;
    private List<DbConnectionSnapshot>? _topOpen;
    private Dictionary<ulong, int>? _perTypeSamples;
    private int _stateSamples;
    private bool _stateScanCapped;

    /// <summary>
    /// Resolves candidate connection-type MethodTables and pre-seeds per-type counters from
    /// TypeAggregates, exactly mirroring the historical single-shot "Step 1 + pre-seed" logic.
    /// </summary>
    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        ClrHeap heap = context.Heap;
        _heap = heap;

        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
        if (context.Cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            typeAggregates = idx?.TypeAggregates;

        var candidateMts = new Dictionary<ulong, (string TypeName, TypeAggregateIndexEntry Entry)>(8);

        if (typeAggregates is not null)
        {
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
            {
                ClrType? clrType = heap.GetTypeByMethodTable(kv.Key);
                if (clrType?.Name is not string fullName) continue;
                if (IsConnectionType(fullName))
                    candidateMts[kv.Key] = (fullName, kv.Value);
            }
        }
        else
        {
            // Fallback: discover types by scanning live heap (slower on large dumps)
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;
                string typeName = obj.Type.Name ?? string.Empty;
                if (!IsConnectionType(typeName)) continue;
                ulong mt = obj.Type.MethodTable;
                if (!candidateMts.ContainsKey(mt))
                    candidateMts[mt] = (typeName, default);
            }
        }

        _candidateMts = candidateMts;

        var typeStats = new Dictionary<ulong, (string Name, int Total, int Open, int Closed, int Other, ulong Bytes)>(candidateMts.Count);
        foreach (KeyValuePair<ulong, (string TypeName, TypeAggregateIndexEntry Entry)> kv in candidateMts)
        {
            // Pre-seed from TypeAggregates when available (no heap access needed for counts)
            int total = (int)Math.Min(kv.Value.Entry.Count, int.MaxValue);
            ulong bytes = kv.Value.Entry.TotalSize;
            typeStats[kv.Key] = (kv.Value.TypeName, total, 0, 0, 0, bytes);
        }

        _typeStats = typeStats;
        _topOpen = new List<DbConnectionSnapshot>(16);
        _perTypeSamples = new Dictionary<ulong, int>(candidateMts.Count);
        _stateSamples = 0;
        _stateScanCapped = false;
    }

    /// <summary>
    /// Called once per disk-backed index entry, in address order, during the shared heap-index
    /// scan pass. Mirrors the historical fast-path loop body, operating on instance fields.
    /// Explicit interface implementation because <see cref="HeapEntry"/> is internal and this
    /// class is public — an implicit implementation would leak the internal type as public API.
    /// </summary>
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => OnHeapEntry(in entry);

    private void OnHeapEntry(in HeapEntry entry)
    {
        var candidateMts = _candidateMts!;
        var typeStats = _typeStats!;
        var perTypeSamples = _perTypeSamples!;
        var topOpen = _topOpen!;

        if (!candidateMts.ContainsKey(entry.MethodTable)) return;
        if (!typeStats.TryGetValue(entry.MethodTable, out var ts)) return;
        string typeName = ts.Name;

        // Read state field (capped per type and globally)
        int stateVal = -1;
        perTypeSamples.TryGetValue(entry.MethodTable, out int typeSampleCount);
        if (typeSampleCount < MaxStateSamples && _stateSamples < MaxStateSamples * candidateMts.Count)
        {
            stateVal = TryReadConnectionState(_heap!, entry.Address);
            perTypeSamples[entry.MethodTable] = typeSampleCount + 1;
            _stateSamples++;
        }
        else
        {
            _stateScanCapped = true;
        }

        // Tally state
        int open = ts.Open; int closed = ts.Closed; int other = ts.Other;
        if (stateVal == StateOpen)        open++;
        else if (stateVal == StateClosed) closed++;
        else if (stateVal >= 0)           other++;
        typeStats[entry.MethodTable] = (typeName, ts.Total, open, closed, other, ts.Bytes);

        // Capture top-N open connections for the detail table
        if (stateVal == StateOpen && topOpen.Count < 50)
            topOpen.Add(new DbConnectionSnapshot(typeName, entry.Address, "Open", stateVal));
    }

    // Relies on the pipeline dispatcher having already called BeforeHeapIndexScan/OnHeapEntry
    // for this context before AnalyzeAsync runs (see AnalysisPipeline.ExecuteAsync).
    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(BuildResult().Stamp(this));
    }

    private DbConnectionDomainResult BuildResult()
    {
        if (_typeStats is null || _typeStats.Count == 0)
            return Empty();

        int totalConnections = 0, totalOpen = 0, totalClosed = 0, totalOther = 0;
        var byType = new List<DbConnectionTypeSummary>(_typeStats.Count);

        foreach (var kv in _typeStats)
        {
            var ts = kv.Value;
            byType.Add(new DbConnectionTypeSummary(ts.Name, ts.Total, ts.Open, ts.Closed, ts.Other, ts.Bytes));
            totalConnections += ts.Total;
            totalOpen        += ts.Open;
            totalClosed      += ts.Closed;
            totalOther       += ts.Other;
        }

        byType.Sort(static (a, b) => b.TotalCount.CompareTo(a.TotalCount));

        return new DbConnectionDomainResult(
            ConnectionsFound:    totalConnections > 0,
            TotalConnections:    totalConnections,
            OpenConnections:     totalOpen,
            ClosedConnections:   totalClosed,
            OtherConnections:    totalOther,
            ByType:              byType,
            TopOpenConnections:  _topOpen ?? [],
            StateScanCapped:     _stateScanCapped);
    }

    private static int TryReadConnectionState(ClrHeap heap, ulong address)
    {
        try
        {
            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type is null) return -1;

            for (int i = 0; i < StateFieldNames.Length; i++)
            {
                ClrInstanceField? field = obj.Type.GetFieldByName(StateFieldNames[i]);
                if (field is null || field.ElementType != ClrElementType.Int32) continue;
                return field.Read<int>(obj.Address, interior: false);
            }
        }
        catch { /* ClrMD can throw on corrupt/unloaded objects */ }
        return -1;
    }

    private static DbConnectionDomainResult Empty() =>
        new(false, 0, 0, 0, 0, [], [], false);
}
