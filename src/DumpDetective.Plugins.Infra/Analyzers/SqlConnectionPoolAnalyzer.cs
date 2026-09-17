using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Tier-2 pilot (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md): first analyzer
/// retyped onto <see cref="IDumpObjectFieldQuery"/> (<c>dump.object-fields</c>), alongside the
/// already-shipped Tier-1 surfaces. Reports exact current-size vs. max-pool-size for each discovered
/// ADO.NET connection-pool manager object (<c>System.Data.ProviderBase.DbConnectionPool</c> /
/// <c>Microsoft.Data.ProviderBase.DbConnectionPool</c>) on the heap, read directly from the pool's
/// live internal counters — exact pool-utilisation evidence, not an estimate derived from sampling
/// connection objects.
/// </summary>
/// <remarks>
/// Candidate discovery (which heap objects are pool-manager instances) is Tier-1-shaped — address
/// and type only, no field reads — and stays a pipeline-level, single-shared-heap-scan concern on
/// <see cref="SqlConnectionPoolAnalyzerLegacyAdapter"/> (the pipeline never sees this inner analyzer
/// directly, the same reason <c>GCRootAnalyzerLegacyAdapter</c> carries its dominator-tree marker
/// interfaces). This class only reads field *values* off the addresses the adapter discovered, via
/// the new Tier-2 capability — exactly the split Tier 1/Tier 2 draws. The pre-retyping analyzer's own
/// per-field <c>try/catch</c> moved down into <see cref="DumpObjectFieldQuery.TryReadField"/> itself
/// (every read is independently defensive there); this class no longer needs its own wrapping
/// <c>try/catch</c> around a whole snapshot read.
///
/// Candidates arrive through <see cref="AnalysisContext.HeapObjects"/> (<c>heap.objects</c>), the
/// existing Tier-1 <see cref="HeapObjectRef"/> shape — not a bespoke per-analyzer record, and not
/// <c>AnalyzerOptions</c>, which stays reserved for actual analyzer configuration. Corrected from
/// the pilot's original design, which reused <c>AnalyzerOptions</c> for this and was recognized as
/// overloading one slot for two unrelated purposes.
/// </remarks>
[AnalyzerModule(key: "sql-connection-pool", displayName: "SQL Connection Pool Analysis", order: 427, tags: ["infra", "network"])]
[RequiresCapability(
    CapabilityVocabulary.DumpObjectFields,
    CapabilityVocabulary.HeapObjects)]
public sealed class SqlConnectionPoolAnalyzer :
    IAnalyzer, IProducesAnalyzerDomainResult, ITypedResourceCandidateSource,
    IParallelHeapIndexScanParticipant, ICustomizesAnalysisContext
{
    /// <summary>
    /// Both legacy and current SqlClient packages share the exact same pool-manager implementation
    /// (copied internal type, same field names) under
    /// <c>System.Data.ProviderBase.DbConnectionPool</c> / <c>Microsoft.Data.ProviderBase.DbConnectionPool</c>
    /// respectively — no per-provider pool subclass, matched by namespace prefix; other providers
    /// (Npgsql, MySql, Oracle, Sqlite) use unrelated internal pooling designs, out of scope.
    /// </summary>
    private static readonly HashSet<string> PoolTypeNames = new(StringComparer.Ordinal)
    {
        "System.Data.ProviderBase.DbConnectionPool",
        "Microsoft.Data.ProviderBase.DbConnectionPool",
    };

    private readonly TypedResourceHeapIndexScanState _scanState = new();

    public string Name => "SQL Connection Pool Analysis";
    public string Category => "Infrastructure";

    private const double NearCapacityUtilizationPct = 80.0;

    public bool IsCandidateType(string typeName) => PoolTypeNames.Contains(typeName);

    void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _scanState.BeforeHeapIndexScan(this, context);
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => _scanState.OnHeapEntry(in entry);
    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanState.OnHeapIndexScanCompleted(succeeded);
    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new SqlConnectionPoolAnalyzer();
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) =>
        _scanState.MergeFrom(partials.OfType<SqlConnectionPoolAnalyzer>().Select(a => a._scanState));

    Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
        AnalysisContextBuilder.Build(context, analyzerOptions: null, heapObjectsOverride: new PrecomputedHeapObjectStream(_scanState.Candidates));

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");

        LastResult = await Analyze(fields, context.HeapObjects, context, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
    /// <c>infra.sql-connection-pool</c> observation per discovered pool-manager instance, inline
    /// where <see cref="HeapObjectRef.Type"/> is still in scope (no <c>IHeapObjectLookup</c>
    /// capability on this analyzer to re-resolve it from <c>SqlConnectionPoolSnapshot.Address</c>
    /// afterwards). Bounded to actual pool-manager objects — a handful per process, never
    /// heap-scale.
    /// </summary>
    private static async ValueTask<SqlConnectionPoolDomainResult> Analyze(
        IDumpObjectFieldQuery fields, IHeapObjectStream? candidates, AnalysisContext observationContext, CancellationToken cancellationToken)
    {
        if (candidates is null)
            return Empty();

        var observationCapabilities = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects };
        var pools = new List<SqlConnectionPoolSnapshot>();
        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            SqlConnectionPoolSnapshot? snap = TryReadPoolSnapshot(fields, candidate.Address, candidate.TypeDisplayName);
            if (snap is null)
                continue;

            pools.Add(snap);

            var measures = new Dictionary<string, Measure>
            {
                ["current.size"] = new Measure(snap.CurrentSize, MeasureUnit.Count, MeasureSemantics.Absolute),
            };
            if (snap.MaxPoolSize >= 0)
                measures["max.size"] = new Measure(snap.MaxPoolSize, MeasureUnit.Count, MeasureSemantics.Absolute);
            if (snap.MinPoolSize >= 0)
                measures["min.size"] = new Measure(snap.MinPoolSize, MeasureUnit.Count, MeasureSemantics.Absolute);

            await observationContext.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.sql-connection-pool",
                Subjects = [candidate.Type],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = measures,
                Provenance = new Provenance
                {
                    Artifact = observationContext.ArtifactId!.Value,
                    AnalyzerKey = "SQL Connection Pool Analysis",
                    CapabilitiesUsed = observationCapabilities,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }

        if (pools.Count == 0)
            return Empty();

        int nearCapacity = 0;
        foreach (SqlConnectionPoolSnapshot pool in pools)
        {
            if (UtilizationPercent(pool) >= NearCapacityUtilizationPct)
                nearCapacity++;
        }

        return new SqlConnectionPoolDomainResult(
            PoolsFound: true,
            TotalPools: pools.Count,
            PoolsNearCapacity: nearCapacity,
            Pools: pools);
    }

    private static SqlConnectionPoolSnapshot? TryReadPoolSnapshot(IDumpObjectFieldQuery fields, ulong address, string typeName)
    {
        // Matches the pre-retyping analyzer's own defensive shape check — a differently-typed
        // "_totalObjects" would mean a SqlClient internal layout change this analyzer doesn't
        // understand, not a value to report.
        if (!fields.TryReadField(address, "_totalObjects", out FieldValue totalObjectsValue)
            || totalObjectsValue.Kind != FieldValueKind.Int32)
            return null;
        int currentSize = totalObjectsValue.AsInt32();

        (int maxPoolSize, int minPoolSize) = TryReadPoolSizeLimits(fields, address);
        string? anonymisedConnStr = TryReadAnonymisedConnectionString(fields, address);

        return new SqlConnectionPoolSnapshot(typeName, address, currentSize, maxPoolSize, minPoolSize, anonymisedConnStr);
    }

    // Pool -> _connectionPoolGroupOptions (DbConnectionPoolGroupOptions) -> _maxPoolSize/_minPoolSize
    private static (int MaxPoolSize, int MinPoolSize) TryReadPoolSizeLimits(IDumpObjectFieldQuery fields, ulong poolAddress)
    {
        if (!fields.TryReadField(poolAddress, "_connectionPoolGroupOptions", out FieldValue optionsValue)
            || optionsValue.Kind != FieldValueKind.ObjectReference)
            return (-1, -1);

        ulong optionsAddress = optionsValue.AsObjectAddress();
        int max = fields.TryReadField(optionsAddress, "_maxPoolSize", out FieldValue maxValue) && maxValue.Kind == FieldValueKind.Int32
            ? maxValue.AsInt32()
            : -1;
        int min = fields.TryReadField(optionsAddress, "_minPoolSize", out FieldValue minValue) && minValue.Kind == FieldValueKind.Int32
            ? minValue.AsInt32()
            : -1;
        return (max, min);
    }

    // Pool -> _connectionPoolGroup (DbConnectionPoolGroup) -> _connectionOptions (DbConnectionOptions)
    // -> _usersConnectionString
    private static string? TryReadAnonymisedConnectionString(IDumpObjectFieldQuery fields, ulong poolAddress)
    {
        if (!fields.TryReadField(poolAddress, "_connectionPoolGroup", out FieldValue groupValue)
            || groupValue.Kind != FieldValueKind.ObjectReference)
            return null;

        if (!fields.TryReadField(groupValue.AsObjectAddress(), "_connectionOptions", out FieldValue connOptionsValue)
            || connOptionsValue.Kind != FieldValueKind.ObjectReference)
            return null;

        if (!fields.TryReadField(connOptionsValue.AsObjectAddress(), "_usersConnectionString", out FieldValue connStrValue)
            || connStrValue.Kind != FieldValueKind.String)
            return null;

        return ConnectionStringAnonymiser.Anonymise(connStrValue.AsString());
    }

    /// <summary>Returns -1 when <see cref="SqlConnectionPoolSnapshot.MaxPoolSize"/> couldn't be read.</summary>
    internal static double UtilizationPercent(SqlConnectionPoolSnapshot pool) =>
        pool.MaxPoolSize > 0 ? 100.0 * pool.CurrentSize / pool.MaxPoolSize : -1;

    private static SqlConnectionPoolDomainResult Empty() => new(false, 0, 0, []);

    public void Dispose() { }
}
