using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Scans the managed heap for ADO.NET transaction objects (SqlTransaction/IDbTransaction
/// implementations). Detects long-held transactions that prevent connection pool return — a
/// transaction still referencing its owning connection is a strong leak/anti-pattern signal,
/// distinct from an ordinary open-but-idle connection.
///
/// Same namespace-prefix providers as <see cref="DbConnectionAnalyzer"/>. A transaction is
/// classified Active when its internal <c>_connection</c> field still references a live
/// connection object (Commit/Rollback/Dispose null it out on completion), Disposed otherwise.
/// </summary>
/// <remarks>
/// Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Same shape as
/// <see cref="SqlCommandAnalyzer"/>'s retyping — see that class's remarks for the general pattern.
/// Unlike commands, this analyzer's pre-retyping "Other" bucket *is* surfaced
/// (<see cref="SqlTransactionTypeSummary.OtherCount"/>/<see cref="SqlTransactionDomainResult.OtherCount"/>),
/// so it's tracked and reported here too.
/// </remarks>
public sealed class SqlTransactionAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "SQL Transaction Analysis";
    public string Category => "Infrastructure";

    private const int StateDisposed = 0;
    private const int StateActive = 1;

    // Field names tried in order when reading the owning connection reference.
    private static readonly string[] ConnectionFieldNames = ["_connection", "_internalConnection", "Connection"];

    public AnalyzerDomainResult? LastResult { get; private set; }

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");

        LastResult = Analyze(fields, context.HeapObjects);
        return ValueTask.CompletedTask;
    }

    private static SqlTransactionDomainResult Analyze(IDumpObjectFieldQuery fields, IHeapObjectStream? candidates)
    {
        if (candidates is null)
            return Empty();

        var typeStats = new Dictionary<string, (int Total, int Active, int Disposed, int Other, ulong Bytes)>(StringComparer.Ordinal);
        var topActive = new List<SqlTransactionSnapshot>();

        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
            (int stateVal, ulong? connectionAddress) = TryReadTransactionState(fields, candidate.Address);
            typeStats.TryGetValue(candidate.TypeDisplayName, out var ts);

            int active = ts.Active, disposed = ts.Disposed, other = ts.Other;
            if (stateVal == StateActive)
            {
                active++;
                topActive.Add(new SqlTransactionSnapshot(candidate.TypeDisplayName, candidate.Address, "Active", stateVal, connectionAddress));
            }
            else if (stateVal == StateDisposed)
            {
                disposed++;
            }
            else
            {
                other++;
            }

            typeStats[candidate.TypeDisplayName] = (ts.Total + 1, active, disposed, other, ts.Bytes + candidate.Size);
        }

        if (typeStats.Count == 0)
            return Empty();

        int totalTransactions = 0, totalActive = 0, totalDisposed = 0, totalOther = 0;
        var byType = new List<SqlTransactionTypeSummary>(typeStats.Count);
        foreach (var kv in typeStats)
        {
            byType.Add(new SqlTransactionTypeSummary(kv.Key, kv.Value.Total, kv.Value.Disposed, kv.Value.Active, kv.Value.Other, kv.Value.Bytes));
            totalTransactions += kv.Value.Total;
            totalActive += kv.Value.Active;
            totalDisposed += kv.Value.Disposed;
            totalOther += kv.Value.Other;
        }

        byType.Sort(static (a, b) => b.TotalCount.CompareTo(a.TotalCount));

        return new SqlTransactionDomainResult(
            TransactionsFound: totalTransactions > 0,
            TotalTransactions: totalTransactions,
            DisposedCount: totalDisposed,
            ActiveCount: totalActive,
            OtherCount: totalOther,
            ByType: byType,
            TopActiveTransactions: topActive);
    }

    private static (int StateValue, ulong? ConnectionAddress) TryReadTransactionState(IDumpObjectFieldQuery fields, ulong address)
    {
        foreach (string fieldName in ConnectionFieldNames)
        {
            if (!fields.TryReadField(address, fieldName, out FieldValue value))
                continue; // field not declared on this type -- try the next name

            return value.Kind == FieldValueKind.ObjectReference
                ? (StateActive, value.AsObjectAddress())
                : (StateDisposed, null);
        }

        return (-1, null);
    }

    private static SqlTransactionDomainResult Empty() => new(false, 0, 0, 0, 0, [], []);

    public void Dispose() { }
}
