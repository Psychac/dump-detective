using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

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
/// </remarks>
public sealed class SqlConnectionPoolAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "SQL Connection Pool Analysis";
    public string Category => "Infrastructure";

    private const double NearCapacityUtilizationPct = 80.0;

    public AnalyzerDomainResult? LastResult { get; private set; }

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");

        IReadOnlyList<SqlConnectionPoolCandidate> candidates =
            context.AnalyzerOptions as IReadOnlyList<SqlConnectionPoolCandidate> ?? [];

        LastResult = Analyze(fields, candidates);
        return ValueTask.CompletedTask;
    }

    private static SqlConnectionPoolDomainResult Analyze(
        IDumpObjectFieldQuery fields, IReadOnlyList<SqlConnectionPoolCandidate> candidates)
    {
        if (candidates.Count == 0)
            return Empty();

        var pools = new List<SqlConnectionPoolSnapshot>(candidates.Count);
        foreach (SqlConnectionPoolCandidate candidate in candidates)
        {
            SqlConnectionPoolSnapshot? snap = TryReadPoolSnapshot(fields, candidate.Address, candidate.TypeName);
            if (snap is not null)
                pools.Add(snap);
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

/// <summary>
/// One candidate heap object <see cref="SqlConnectionPoolAnalyzerLegacyAdapter"/> discovered during
/// the pipeline's shared heap-index scan (Tier-1-shaped: address + type name only). Field-value
/// extraction happens later, in <see cref="SqlConnectionPoolAnalyzer.AnalyzeAsync"/>, via
/// <see cref="IDumpObjectFieldQuery"/>.
/// </summary>
internal sealed record SqlConnectionPoolCandidate(ulong Address, string TypeName);
