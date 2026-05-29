using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
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
public sealed class DbConnectionAnalyzer : IAnalyzer
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
    private static bool IsConnectionType(string typeName)
    {
        if (!typeName.EndsWith("Connection", StringComparison.Ordinal)) return false;
        for (int i = 0; i < ConnectionNamespacePrefixes.Length; i++)
        {
            if (typeName.StartsWith(ConnectionNamespacePrefixes[i], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Field names to try in order when reading connection state
    private static readonly string[] StateFieldNames = ["_connectionState", "_state", "m_connectionState"];

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            Analyze(context.Heap, context.Cache, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(
        ClrHeap? heap,
        IHeapAnalysisCache? cache,
        CancellationToken cancellationToken)
    {
        if (heap is null)
            return Empty();

        // ── Step 1: Resolve TypeAggregates and find matching MTs ─────────────
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
        if (cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            typeAggregates = idx?.TypeAggregates;

        // Map MethodTable → (TypeName, IndexEntry) for connection types
        var candidateMts = new Dictionary<ulong, (string TypeName, TypeAggregateIndexEntry Entry)>(8);

        if (typeAggregates is not null)
        {
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                if (!obj.IsValid || obj.Type is null) continue;
                string typeName = obj.Type.Name ?? string.Empty;
                if (!IsConnectionType(typeName)) continue;
                ulong mt = obj.Type.MethodTable;
                if (!candidateMts.ContainsKey(mt))
                    candidateMts[mt] = (typeName, default);
            }
        }

        if (candidateMts.Count == 0)
            return Empty();

        // ── Step 2: Aggregate per-type counters + sample state reading ────────
        // Per-type accumulator: open, closed, other, total bytes
        var typeStats = new Dictionary<ulong, (string Name, int Total, int Open, int Closed, int Other, ulong Bytes)>(candidateMts.Count);
        foreach (KeyValuePair<ulong, (string TypeName, TypeAggregateIndexEntry Entry)> kv in candidateMts)
        {
            // Pre-seed from TypeAggregates when available (no heap access needed for counts)
            int total = (int)Math.Min(kv.Value.Entry.Count, int.MaxValue);
            ulong bytes = kv.Value.Entry.TotalSize;
            typeStats[kv.Key] = (kv.Value.TypeName, total, 0, 0, 0, bytes);
        }

        var topOpen    = new List<DbConnectionSnapshot>(16);
        int stateSamples = 0;
        bool stateScanCapped = false;

        // Per-MT state-read counter to avoid excessive object reads
        var perTypeSamples = new Dictionary<ulong, int>(candidateMts.Count);

        if (cache is HeapAnalysisCache heapCache2 && heapCache2.TryGetHeapIndex(out HeapIndexBuildResult? idx2))
        {
            // Fast path: iterate disk-backed index entries filtered by matching MTs
            foreach (HeapEntry entry in heapCache2.EnumerateIndexedEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidateMts.ContainsKey(entry.MethodTable)) continue;

                if (!typeStats.TryGetValue(entry.MethodTable, out var ts)) continue;
                string typeName = ts.Name;

                // Read state field (capped per type and globally)
                int stateVal = -1;
                perTypeSamples.TryGetValue(entry.MethodTable, out int typeSampleCount);
                if (typeSampleCount < MaxStateSamples && stateSamples < MaxStateSamples * candidateMts.Count)
                {
                    stateVal = TryReadConnectionState(heap, entry.Address);
                    perTypeSamples[entry.MethodTable] = typeSampleCount + 1;
                    stateSamples++;
                }
                else
                {
                    stateScanCapped = true;
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
        }
        else
        {
            // Full heap fallback
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!obj.IsValid || obj.Type is null) continue;
                ulong mt = obj.Type.MethodTable;
                if (!typeStats.TryGetValue(mt, out var ts)) continue;

                string typeName = ts.Name;
                int stateVal = -1;
                perTypeSamples.TryGetValue(mt, out int typeSampleCount);
                if (typeSampleCount < MaxStateSamples)
                {
                    stateVal = TryReadConnectionState(heap, obj.Address);
                    perTypeSamples[mt] = typeSampleCount + 1;
                    stateSamples++;
                }
                else
                {
                    stateScanCapped = true;
                }

                int open = ts.Open; int closed = ts.Closed; int other = ts.Other;
                if (stateVal == StateOpen)        open++;
                else if (stateVal == StateClosed) closed++;
                else if (stateVal >= 0)           other++;
                typeStats[mt] = (typeName, ts.Total + 1, open, closed, other, ts.Bytes + (ulong)obj.Size);

                if (stateVal == StateOpen && topOpen.Count < 50)
                    topOpen.Add(new DbConnectionSnapshot(typeName, obj.Address, "Open", stateVal));
            }
        }

        // ── Step 3: Build result ──────────────────────────────────────────────
        int totalConnections = 0, totalOpen = 0, totalClosed = 0, totalOther = 0;
        var byType = new List<DbConnectionTypeSummary>(typeStats.Count);

        foreach (var kv in typeStats)
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
            TopOpenConnections:  topOpen,
            StateScanCapped:     stateScanCapped);
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
