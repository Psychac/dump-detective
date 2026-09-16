using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers.Infra;

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
/// <remarks>
/// Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), sixth
/// consumer of <c>TypedResourceCandidateStreamingLegacyAdapter</c> and first consumer of the new
/// <c>heap.root-paths</c> capability. Unlike <c>WcfChannelAnalyzer</c>'s retyping (same batch, same
/// day), an unreadable state field here counts as <see cref="DbConnectionDomainResult.UnknownStateConnections"/>
/// rather than being left out of every bucket — a real, pre-existing difference between the two
/// analyzers' own field-probe semantics, not a retyping inconsistency: <c>DbConnectionAnalyzer</c>'s
/// pre-retyping <c>OnHeapEntry</c> always incremented one of its counters (<c>unknown++</c> in the
/// <c>snap is null</c> branch), while WCF's left a "state field not found at all" instance out of
/// every bucket entirely.
///
/// <see cref="GenerationTagToSbyte"/> maps <see cref="HeapGenerationTag"/> to the sbyte convention
/// <see cref="DbConnectionSnapshot.Generation"/> already used (0/1/2 for Gen0-2, higher for
/// LOH/POH/Frozen, -1 unknown) -- matching <c>GCRootAnalyzer</c>'s own <c>Loh</c> index-3
/// convention. Only 0 and 2 are ever compared against downstream (Gen0Open/Gen2Open), so the exact
/// values picked for LOH/POH/Frozen are cosmetic.
///
/// Noted but out of scope, confirmed by decompiling the real assembly rather than guessed: on the
/// 3.35GB reference dump, every single candidate came back Unknown-state (0 Open/Closed/Broken/Other
/// out of 503). This is a genuine, pre-existing limitation, not a retyping regression --
/// <c>Microsoft.Data.SqlClient.SqlConnection</c> (v5.2.2, the dominant modern package) declares no
/// <c>_connectionState</c>/<c>_state</c>/<c>m_connectionState</c> field of its own; its public
/// <c>State</c> property delegates to a separate <c>_innerConnection</c> (<c>DbConnectionInternal</c>)
/// object's own state, which <see cref="StateFieldNames"/> was never written to chase into. The
/// pre-retyping analyzer had the identical gap (same field names, same direct-field read). Left
/// unfixed here since correctly reading <c>DbConnectionInternal</c>'s real state field would need
/// its own verification pass (field name stability across the SqlClient/Npgsql/MySql/Oracle provider
/// matrix this analyzer targets), not a quick addition to this batch.
/// </remarks>
[AnalyzerModule(key: "db-connection", displayName: "DbConnection Analysis", order: 420, tags: ["infra", "network"])]
[RequiresCapability(
    CapabilityVocabulary.DumpObjectFields,
    CapabilityVocabulary.HeapDominators,
    CapabilityVocabulary.HeapObjects,
    CapabilityVocabulary.HeapSegments)]
public sealed class DbConnectionAnalyzer :
    IAnalyzer, IProducesAnalyzerDomainResult, ITypedResourceCandidateSource,
    IParallelHeapIndexScanParticipant, IRequiresReachableGraphIndex, ICustomizesAnalysisContext
{
    private readonly TypedResourceHeapIndexScanState _scanState = new();

    public string Name => "DB Connection Analysis";
    public string Category => "Infrastructure";

    bool ITypedResourceCandidateSource.IsCandidateType(string typeName) => IsCandidateType(typeName);

    void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _scanState.BeforeHeapIndexScan(this, context);
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => _scanState.OnHeapEntry(in entry);
    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanState.OnHeapIndexScanCompleted(succeeded);
    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new DbConnectionAnalyzer();
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) =>
        _scanState.MergeFrom(partials.OfType<DbConnectionAnalyzer>().Select(a => a._scanState));

    Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
        AnalysisContextBuilder.Build(context, analyzerOptions: null, heapObjectsOverride: new PrecomputedHeapObjectStream(_scanState.Candidates));

    // ADO.NET ConnectionState enum values
    private const int StateOpen = 1;
    private const int StateClosed = 0;
    private const int StateBroken = 16;

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

    // Field names to try in order when reading connection state
    private static readonly string[] StateFieldNames = ["_connectionState", "_state", "m_connectionState"];

    private const int MaxRootPathEnrichment = 20;

    private static readonly RootPathSearchOptions RootPathOptions = new(
        MaxCandidateNodes: 5_000, MaxCandidateDepth: 8, MaxRootExpansionDepth: 12, LargeFanoutThreshold: 100);

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    // Candidate type must end in "Connection" (covers SqlConnection, NpgsqlConnection, etc.)
    internal static bool IsCandidateType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, ConnectionNamespacePrefixes, "Connection", null);

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");
        IHeapSegmentQuery segments = context.HeapSegments
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapSegments}' capability.");
        IHeapRootPathQuery rootPaths = context.HeapRootPaths
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.HeapRootPaths}' capability.");

        LastResult = Analyze(fields, segments, context.HeapDominators, rootPaths, context.HeapObjects);
        return ValueTask.CompletedTask;
    }

    private static DbConnectionDomainResult Analyze(
        IDumpObjectFieldQuery fields, IHeapSegmentQuery segments, IHeapDominatorQuery? dominators, IHeapRootPathQuery rootPaths, IHeapObjectStream? candidates)
    {
        if (candidates is null)
            return Empty();

        var typeStats = new Dictionary<string, (int Total, int Open, int Closed, int Broken, int Other, int Unknown, int Gen2Open, int Gen0Open, ulong Bytes)>(StringComparer.Ordinal);
        var topOpen = new List<DbConnectionSnapshot>();

        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
            int stateVal = TryReadState(fields, candidate.Address);

            typeStats.TryGetValue(candidate.TypeDisplayName, out var ts);
            int open = ts.Open, closed = ts.Closed, broken = ts.Broken, other = ts.Other, unknown = ts.Unknown;
            int gen2Open = ts.Gen2Open, gen0Open = ts.Gen0Open;

            if (stateVal < 0)
            {
                unknown++;
            }
            else if (stateVal == StateOpen)
            {
                open++;
                // AnonymisedConnectionString/Generation are only ever observable on an Open
                // snapshot (Closed/Broken/Other connections never surface a DbConnectionSnapshot
                // at all) -- read here, not unconditionally for every readable candidate, unlike
                // the pre-retyping analyzer's own TrySample (which computed both for every state,
                // discarding the result for non-Open connections).
                sbyte generation = GenerationTagToSbyte(segments.GetGeneration(candidate.Address));
                if (generation == 2) gen2Open++;
                else if (generation == 0) gen0Open++;

                string? anonymisedConnStr = TryReadAnonymisedConnectionString(fields, candidate.Address);
                topOpen.Add(new DbConnectionSnapshot(candidate.TypeDisplayName, candidate.Address, "Open", stateVal, anonymisedConnStr, generation));
            }
            else if (stateVal == StateClosed) closed++;
            else if (stateVal == StateBroken) broken++;
            else other++;

            typeStats[candidate.TypeDisplayName] = (ts.Total + 1, open, closed, broken, other, unknown, gen2Open, gen0Open, ts.Bytes + candidate.Size);
        }

        if (typeStats.Count == 0)
            return Empty();

        int totalConnections = 0, totalOpen = 0, totalClosed = 0, totalBroken = 0, totalOther = 0, totalUnknown = 0;
        int totalGen2Open = 0, totalGen0Open = 0;
        var byType = new List<DbConnectionTypeSummary>(typeStats.Count);
        foreach (var kv in typeStats)
        {
            var ts = kv.Value;
            byType.Add(new DbConnectionTypeSummary(kv.Key, ts.Total, ts.Open, ts.Closed, ts.Broken, ts.Other, ts.Unknown, ts.Bytes));
            totalConnections += ts.Total;
            totalOpen += ts.Open;
            totalClosed += ts.Closed;
            totalBroken += ts.Broken;
            totalOther += ts.Other;
            totalUnknown += ts.Unknown;
            totalGen2Open += ts.Gen2Open;
            totalGen0Open += ts.Gen0Open;
        }

        byType.Sort(static (a, b) => b.TotalCount.CompareTo(a.TotalCount));

        IReadOnlyList<DbConnectionSnapshot> topOpenConnections = WithRetainedBytes(topOpen, dominators);
        topOpenConnections = WithRootPaths(topOpenConnections, rootPaths);

        List<PoolSummary> topPools = BuildTopPools(topOpenConnections);

        return new DbConnectionDomainResult(
            ConnectionsFound: totalConnections > 0,
            TotalConnections: totalConnections,
            OpenConnections: totalOpen,
            ClosedConnections: totalClosed,
            BrokenConnections: totalBroken,
            OtherConnections: totalOther,
            UnknownStateConnections: totalUnknown,
            Gen2OpenConnections: totalGen2Open,
            Gen0OpenConnections: totalGen0Open,
            ByType: byType,
            TopOpenConnections: topOpenConnections,
            TopPools: topPools);
    }

    private static int TryReadState(IDumpObjectFieldQuery fields, ulong address)
    {
        foreach (string fieldName in StateFieldNames)
        {
            if (fields.TryReadField(address, fieldName, out FieldValue value) && value.Kind == FieldValueKind.Int32)
                return value.AsInt32();
        }
        return -1;
    }

    // _connectionString is a real string-typed field (unlike e.g. HttpObjectAnalyzer's Uri-typed
    // fields), so TryReadField's own ClrElementType.String case already resolves it directly --
    // no further TryGetStringValue chase needed. Same shape SqlConnectionPoolAnalyzer's pilot
    // already established for _usersConnectionString.
    private static string? TryReadAnonymisedConnectionString(IDumpObjectFieldQuery fields, ulong address)
    {
        if (fields.TryReadField(address, "_connectionString", out FieldValue value) && value.Kind == FieldValueKind.String)
            return ConnectionStringAnonymiser.Anonymise(value.AsString());
        return null;
    }

    private static sbyte GenerationTagToSbyte(HeapGenerationTag tag) => tag switch
    {
        HeapGenerationTag.Gen0 => 0,
        HeapGenerationTag.Gen1 => 1,
        HeapGenerationTag.Gen2 => 2,
        HeapGenerationTag.Loh => 3,
        HeapGenerationTag.Poh => 4,
        HeapGenerationTag.Frozen => 5,
        _ => -1,
    };

    // §9 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): the biggest gap
    // found in that audit — DbConnectionSnapshot carried no size field of any kind. Applied to
    // the complete open-connection population (§9.32, D5) — no longer a capped list.
    private static IReadOnlyList<DbConnectionSnapshot> WithRetainedBytes(IReadOnlyList<DbConnectionSnapshot> snapshots, IHeapDominatorQuery? dominators)
    {
        if (dominators is null || snapshots.Count == 0)
            return snapshots;

        var result = new List<DbConnectionSnapshot>(snapshots.Count);
        foreach (DbConnectionSnapshot s in snapshots)
        {
            result.Add(dominators.TryGetRetainedSize(s.Address, out ulong retained)
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
    private static IReadOnlyList<DbConnectionSnapshot> WithRootPaths(IReadOnlyList<DbConnectionSnapshot> snapshots, IHeapRootPathQuery rootPaths)
    {
        if (snapshots.Count == 0)
            return snapshots;

        List<DbConnectionSnapshot> enrichmentTargets = SelectForRootPathEnrichment(snapshots, MaxRootPathEnrichment);
        if (enrichmentTargets.Count == 0)
            return snapshots;

        var enrichedByAddress = new Dictionary<ulong, DbConnectionSnapshot>(enrichmentTargets.Count);
        foreach (DbConnectionSnapshot target in enrichmentTargets)
        {
            bool found = rootPaths.TryFindRootPath(target.Address, RootPathOptions, out string? formattedPath, out bool truncated);
            enrichedByAddress[target.Address] = target with { RootPath = found ? formattedPath : null, RootPathSearchTruncated = truncated };
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

    public void Dispose() { }
}
