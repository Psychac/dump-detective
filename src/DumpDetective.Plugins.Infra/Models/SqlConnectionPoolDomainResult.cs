namespace DumpDetective.Analysis.Models;

/// <summary>
/// Snapshot of a single ADO.NET connection-pool manager object
/// (<c>System.Data.ProviderBase.DbConnectionPool</c> / <c>Microsoft.Data.ProviderBase.DbConnectionPool</c>
/// — shared by both the legacy and current SqlClient packages; there is no per-provider pool
/// subclass to match by name). <see cref="CurrentSize"/>/<see cref="MaxPoolSize"/> come directly
/// from the pool's live internal counters, not from sampling connection objects, so this is exact
/// pool-utilisation evidence rather than an estimate.
/// </summary>
internal sealed record SqlConnectionPoolSnapshot(
    string TypeName,
    ulong Address,
    int CurrentSize,
    int MaxPoolSize,
    int MinPoolSize,
    string? AnonymisedConnectionString);

/// <summary>
/// Domain result produced by <c>SqlConnectionPoolAnalyzer</c>. Reports exact current-size vs.
/// max-pool-size for each discovered SqlClient connection-pool manager object on the heap.
/// </summary>
internal sealed record SqlConnectionPoolDomainResult(
    bool PoolsFound,
    int TotalPools,
    int PoolsNearCapacity,
    IReadOnlyList<SqlConnectionPoolSnapshot> Pools) : DumpDetective.Core.Models.AnalyzerDomainResult;

