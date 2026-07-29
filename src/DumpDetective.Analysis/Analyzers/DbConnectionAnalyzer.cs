using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

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
public sealed class DbConnectionAnalyzer : IAnalyzer, IHeapIndexScanParticipant, ITypedResourceCandidateSource, ITypedResourceInstanceSampler<DbConnectionSnapshot>
{
    public string Name => "DB Connection Analysis";
    public string Category => "Infrastructure";

    // Max per-object state reads to cap ClrMD field access cost on large heaps.
    private const int MaxStateSamples = 500;
    private const int TopOpenCap = 50;

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
    public bool IsCandidateType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, ConnectionNamespacePrefixes, "Connection", null);

    public int MaxStateSamplesPerType => MaxStateSamples;
    public int TopSampleCap => TopOpenCap;

    // Field names to try in order when reading connection state
    private static readonly string[] StateFieldNames = ["_connectionState", "_state", "m_connectionState"];

    DbConnectionSnapshot? ITypedResourceInstanceSampler<DbConnectionSnapshot>.TrySample(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        int stateVal = InstanceStateSampler<DbConnectionSnapshot>.TryReadIntField(heap, entry.Address, StateFieldNames);
        if (stateVal < 0)
            return null;

        return new DbConnectionSnapshot(typeName, entry.Address, stateVal == StateOpen ? "Open" : stateVal == StateClosed ? "Closed" : "Other", stateVal);
    }

    // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
    // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
    // OnHeapEntry; consumed by AnalyzeAsync once the shared index scan has completed.
    private ClrHeap? _heap;
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private Dictionary<ulong, (string Name, int Total, int Open, int Closed, int Other, ulong Bytes)>? _typeStats;
    private InstanceStateSampler<DbConnectionSnapshot>? _sampler;

    /// <summary>
    /// Resolves candidate connection-type MethodTables and pre-seeds per-type counters from
    /// TypeAggregates, exactly mirroring the historical single-shot "Step 1 + pre-seed" logic.
    /// </summary>
    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        ClrHeap heap = context.Heap;
        _heap = heap;

        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> candidateMts =
            TypedResourceScanDriver.DiscoverCandidates(this, heap, context.Cache);
        _candidateMts = candidateMts;

        var typeStats = new Dictionary<ulong, (string Name, int Total, int Open, int Closed, int Other, ulong Bytes)>(candidateMts.Count);
        foreach (KeyValuePair<ulong, (string TypeName, long Count, ulong Bytes)> kv in candidateMts)
        {
            // Pre-seed from TypeAggregates when available (no heap access needed for counts)
            int total = (int)Math.Min(kv.Value.Count, int.MaxValue);
            typeStats[kv.Key] = (kv.Value.TypeName, total, 0, 0, 0, kv.Value.Bytes);
        }

        _typeStats = typeStats;
        _sampler = TypedResourceScanDriver.CreateSampler(this);
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
        var sampler = _sampler!;

        if (!candidateMts.ContainsKey(entry.MethodTable)) return;
        if (!typeStats.TryGetValue(entry.MethodTable, out var ts)) return;
        string typeName = ts.Name;

        // Read state field (capped per type, gated via TryGetSample's reserve-then-sample order)
        DbConnectionSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, sampler, _heap!, in entry, typeName);

        // Tally state
        int open = ts.Open; int closed = ts.Closed; int other = ts.Other;
        if (snap is not null)
        {
            if (snap.StateValue == StateOpen)        open++;
            else if (snap.StateValue == StateClosed) closed++;
            else                                     other++;
        }
        typeStats[entry.MethodTable] = (typeName, ts.Total, open, closed, other, ts.Bytes);

        // Capture top-N open connections for the detail table
        if (snap is not null && snap.StateValue == StateOpen)
            sampler.AddTopSample(snap);
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
            TopOpenConnections:  _sampler?.TopSamples ?? [],
            StateScanCapped:     _sampler?.ScanCapped ?? false);
    }

    private static DbConnectionDomainResult Empty() =>
        new(false, 0, 0, 0, 0, [], [], false);
}
