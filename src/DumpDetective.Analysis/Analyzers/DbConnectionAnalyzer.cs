using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Traversal;
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
public sealed class DbConnectionAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant, ITypedResourceCandidateSource, ITypedResourceInstanceSampler<DbConnectionSnapshot>, IRequiresReachableGraphIndex
{
    public string Name => "DB Connection Analysis";
    public string Category => "Infrastructure";

    // ADO.NET ConnectionState enum values
    private const int StateOpen      = 1;
    private const int StateClosed    = 0;
    private const int StateBroken    = 16;

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

    // Field names to try in order when reading connection state
    private static readonly string[] StateFieldNames = ["_connectionState", "_state", "m_connectionState"];

    // Field names to try when reading connection string
    private static readonly string[] ConnectionStringFieldNames = ["_connectionString"];

    DbConnectionSnapshot? ITypedResourceInstanceSampler<DbConnectionSnapshot>.TrySample(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        int stateVal = InstanceStateSampler<DbConnectionSnapshot>.TryReadIntField(heap, entry.Address, StateFieldNames);
        if (stateVal < 0)
            return null;

        string stateLabel = stateVal == StateOpen ? "Open" : stateVal == StateClosed ? "Closed" : stateVal == StateBroken ? "Broken" : "Other";

        // Try to read anonymised connection string for server/pool identification
        string? anonymisedConnStr = TryReadAnonymisedConnectionString(heap, entry.Address);

        return new DbConnectionSnapshot(typeName, entry.Address, stateLabel, stateVal, anonymisedConnStr, entry.Generation);
    }

    private static string? TryReadAnonymisedConnectionString(ClrHeap heap, ulong address)
    {
        try
        {
            var obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type == null)
                return null;

            // Try to read _connectionString field
            var connStringField = obj.Type.GetFieldByName("_connectionString");
            if (connStringField != null)
            {
                var connStringObj = connStringField.ReadObject(address, interior: false);
                if (connStringObj.IsValid && connStringObj.AsString() is string connStr)
                {
                    return ConnectionStringAnonymiser.Anonymise(connStr);
                }
            }

            return null;
        }
        catch
        {
            // Silently ignore errors reading connection strings
            return null;
        }
    }

    // Instance accumulator state for the IHeapIndexScanParticipant path. Populated by
    // BeforeHeapIndexScan (called by the pipeline dispatcher) and mutated per-entry by
    // OnHeapEntry; consumed by AnalyzeAsync once the shared index scan has completed.
    private ClrHeap? _heap;
    private IHeapAnalysisCache? _cache;
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private Dictionary<ulong, (string Name, int Total, int Open, int Closed, int Broken, int Other, int Unknown, int Gen2Open, int Gen0Open, ulong Bytes)>? _typeStats;
    private InstanceStateSampler<DbConnectionSnapshot>? _sampler;

    /// <summary>
    /// Resolves candidate connection-type MethodTables and pre-seeds per-type counters from
    /// TypeAggregates, exactly mirroring the historical single-shot "Step 1 + pre-seed" logic.
    /// </summary>
    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        ClrHeap heap = context.Heap;
        _heap = heap;
        _cache = context.Cache;

        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> candidateMts =
            TypedResourceScanDriver.DiscoverCandidates(this, heap, context.Cache);
        _candidateMts = candidateMts;

        var typeStats = new Dictionary<ulong, (string Name, int Total, int Open, int Closed, int Broken, int Other, int Unknown, int Gen2Open, int Gen0Open, ulong Bytes)>(candidateMts.Count);
        foreach (KeyValuePair<ulong, (string TypeName, long Count, ulong Bytes)> kv in candidateMts)
        {
            // Pre-seed from TypeAggregates when available (no heap access needed for counts)
            int total = (int)Math.Min(kv.Value.Count, int.MaxValue);
            typeStats[kv.Key] = (kv.Value.TypeName, total, 0, 0, 0, 0, 0, 0, 0, kv.Value.Bytes);
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

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() =>
        new DbConnectionAnalyzer();

    // Merges per-type state-change counts (open/closed/other) and top open samples from
    // disjoint-range workers. Total and Bytes come from TypeAggregates (pre-seeded identically
    // on every worker by BeforeHeapIndexScan) and are not summed.
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        var typeStats = _typeStats!;
        var sampler = _sampler!;

        foreach (IHeapIndexScanParticipant p in partials)
        {
            var other = (DbConnectionAnalyzer)p;
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
                    self.Open + o.Open,
                    self.Closed + o.Closed,
                    self.Broken + o.Broken,
                    self.Other + o.Other,
                    self.Unknown + o.Unknown,
                    self.Gen2Open + o.Gen2Open,
                    self.Gen0Open + o.Gen0Open,
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

        DbConnectionSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, _heap!, in entry, typeName);

        // Tally state and generation
        int open = ts.Open; int closed = ts.Closed; int broken = ts.Broken; int other = ts.Other; int unknown = ts.Unknown;
        int gen2Open = ts.Gen2Open; int gen0Open = ts.Gen0Open;

        if (snap is not null)
        {
            if (snap.StateValue == StateOpen)
            {
                open++;
                // Track open connections by generation (Gen2 = long-lived/leaked, Gen0 = in-flight)
                if (snap.Generation == 2)
                    gen2Open++;
                else if (snap.Generation == 0)
                    gen0Open++;
            }
            else if (snap.StateValue == StateClosed) closed++;
            else if (snap.StateValue == StateBroken) broken++;
            else                                     other++;
        }
        else
        {
            // Field read failed; count as unknown state
            unknown++;
        }
        typeStats[entry.MethodTable] = (typeName, ts.Total, open, closed, broken, other, unknown, gen2Open, gen0Open, ts.Bytes);

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

        int totalConnections = 0, totalOpen = 0, totalClosed = 0, totalBroken = 0, totalOther = 0, totalUnknown = 0;
        int totalGen2Open = 0, totalGen0Open = 0;
        var byType = new List<DbConnectionTypeSummary>(_typeStats.Count);

        foreach (var kv in _typeStats)
        {
            var ts = kv.Value;
            byType.Add(new DbConnectionTypeSummary(ts.Name, ts.Total, ts.Open, ts.Closed, ts.Broken, ts.Other, ts.Unknown, ts.Bytes));
            totalConnections += ts.Total;
            totalOpen        += ts.Open;
            totalClosed      += ts.Closed;
            totalBroken      += ts.Broken;
            totalOther       += ts.Other;
            totalUnknown     += ts.Unknown;
            totalGen2Open    += ts.Gen2Open;
            totalGen0Open    += ts.Gen0Open;
        }

        byType.Sort(static (a, b) => b.TotalCount.CompareTo(a.TotalCount));

        IReadOnlyList<DbConnectionSnapshot> topOpenConnections = WithRetainedBytes(_sampler?.TopSamples ?? []);
        topOpenConnections = WithRootPaths(topOpenConnections);

        // Build top pools by server/database grouping
        var topPools = BuildTopPools(topOpenConnections);

        return new DbConnectionDomainResult(
            ConnectionsFound:    totalConnections > 0,
            TotalConnections:    totalConnections,
            OpenConnections:     totalOpen,
            ClosedConnections:   totalClosed,
            BrokenConnections:   totalBroken,
            OtherConnections:    totalOther,
            UnknownStateConnections: totalUnknown,
            Gen2OpenConnections: totalGen2Open,
            Gen0OpenConnections: totalGen0Open,
            ByType:              byType,
            TopOpenConnections:  topOpenConnections,
            TopPools:            topPools);
    }

    // §9 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): the biggest gap
    // found in that audit — DbConnectionSnapshot carried no size field of any kind. Applied to
    // the complete open-connection population (§9.32, D5) — no longer a capped list.
    private IReadOnlyList<DbConnectionSnapshot> WithRetainedBytes(IReadOnlyList<DbConnectionSnapshot> snapshots)
    {
        IDominatorTreeProvider? treeProvider = _cache?.TryGetDominatorTreeProvider();
        if (treeProvider is null || snapshots.Count == 0)
            return snapshots;

        var result = new List<DbConnectionSnapshot>(snapshots.Count);
        foreach (DbConnectionSnapshot s in snapshots)
        {
            result.Add(treeProvider.TryGetRetainedBytes(s.Address, out ulong retained)
                ? s with { RetainedBytes = retained }
                : s);
        }
        return result;
    }

    // R12 (docs/analysis/phase1/DbConnectionAnalyzer-audit.md): matches WinDbg/SOS's !gcroot
    // workflow, but computing a root-path search for every open connection isn't viable — the
    // (uncapped, per §11.2 D5) open-connection population can be in the thousands for a real
    // leak. Scoped to Gen2 open connections (already the "likely leaked, not just in-flight"
    // subset per the existing generation split), ranked by retained bytes so the worst offenders
    // get a path first, and capped at MaxRootPathEnrichment total searches.
    private const int MaxRootPathEnrichment = 20;

    private static readonly RootPathSearchLimits RootPathLimits = new()
    {
        MaxCandidateNodes = 5_000,
        MaxCandidateDepth = 8,
        MaxRootExpansionDepth = 12,
        LargeFanoutThreshold = 100,
    };

    private IReadOnlyList<DbConnectionSnapshot> WithRootPaths(IReadOnlyList<DbConnectionSnapshot> snapshots)
    {
        if (_cache is null || _heap is null || snapshots.Count == 0)
            return snapshots;

        List<DbConnectionSnapshot> enrichmentTargets = SelectForRootPathEnrichment(snapshots, MaxRootPathEnrichment);
        if (enrichmentTargets.Count == 0)
            return snapshots;

        IReadOnlyList<(string RootKind, ulong Address)> validRoots = _cache.GetOrBuildValidRoots(_heap);
        if (validRoots.Count == 0)
            return snapshots;

        var provider = new ReferenceGraph(_heap);
        var finder = new RootPathFinder(
            _heap, provider, RootPathLimits, RootPathSearchSupport.NoOpTelemetry, RootPathSearchSupport.IsNoisyType,
            static _ => false, _cache.TryGetReverseIndexProvider(), _cache);

        var enrichedByAddress = new Dictionary<ulong, DbConnectionSnapshot>(enrichmentTargets.Count);
        foreach (DbConnectionSnapshot target in enrichmentTargets)
        {
            bool found = finder.TryFindAnyRootPath(
                target.Address, validRoots, out string? rootKind, out List<ulong>? addresses, out bool truncated, out _, out _);
            string? formattedPath = found ? RootPathSearchSupport.FormatPath(_heap, rootKind!, addresses, _cache) : null;
            enrichedByAddress[target.Address] = target with { RootPath = formattedPath, RootPathSearchTruncated = truncated };
        }

        var result = new List<DbConnectionSnapshot>(snapshots.Count);
        foreach (DbConnectionSnapshot s in snapshots)
            result.Add(enrichedByAddress.TryGetValue(s.Address, out DbConnectionSnapshot enriched) ? enriched : s);
        return result;
    }

    /// <summary>Gen2 open connections only, ranked by retained bytes descending (unknown-size
    /// connections last), capped at <paramref name="cap"/>. Pure/testable — no ClrMD access.</summary>
    internal static List<DbConnectionSnapshot> SelectForRootPathEnrichment(IReadOnlyList<DbConnectionSnapshot> snapshots, int cap)
    {
        var gen2 = new List<DbConnectionSnapshot>();
        foreach (DbConnectionSnapshot s in snapshots)
        {
            if (s.Generation == 2)
                gen2.Add(s);
        }

        gen2.Sort(static (a, b) => (b.RetainedBytes ?? 0).CompareTo(a.RetainedBytes ?? 0));

        return gen2.Count > cap ? gen2.GetRange(0, cap) : gen2;
    }

    private static List<PoolSummary> BuildTopPools(IReadOnlyList<DbConnectionSnapshot> topOpenConnections)
    {
        var poolGroups = new Dictionary<string, (int Open, int Total)>();

        foreach (var snap in topOpenConnections)
        {
            string poolId = snap.AnonymisedConnectionString ?? "unknown";
            if (poolGroups.TryGetValue(poolId, out var counts))
            {
                poolGroups[poolId] = (counts.Open + 1, counts.Total + 1);
            }
            else
            {
                poolGroups[poolId] = (1, 1);
            }
        }

        // Complete ranked population, no Top-N cap (§11.2 D5) — the render layer paginates.
        var topPools = poolGroups
            .OrderByDescending(kvp => kvp.Value.Open)
            .Select(kvp => new PoolSummary(kvp.Key, kvp.Value.Open, kvp.Value.Total))
            .ToList();

        return topPools;
    }

    private static DbConnectionDomainResult Empty() =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, [], [], []);
}
