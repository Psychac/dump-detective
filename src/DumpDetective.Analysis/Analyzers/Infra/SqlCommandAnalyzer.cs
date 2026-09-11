using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Scans the managed heap for ADO.NET command objects (SqlCommand/IDbCommand implementations).
/// Reports outstanding commands still wired to a connection — a large population indicates
/// command objects held onto (e.g. cached on a long-lived owner) rather than short-lived
/// per-call instances, which retains parameter/result-set state and connection references.
///
/// Same namespace-prefix providers as <see cref="DbConnectionAnalyzer"/>/<see cref="SqlTransactionAnalyzer"/>.
/// Unlike transactions, ADO.NET providers do not reliably null out a command's internal
/// connection-reference field on <c>Dispose()</c>, so "Active" means "still references a
/// connection object", not strictly "not yet disposed".
/// </summary>
/// <remarks>
/// Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), second real
/// consumer of <c>TypedResourceCandidateStreamingLegacyAdapter</c> after the pilot. Candidate
/// discovery (address + type name) stays Tier-1-shaped on
/// <see cref="SqlCommandAnalyzerLegacyAdapter"/>; per-instance connection-field state, read here via
/// <see cref="IDumpObjectFieldQuery"/>, moves entirely off the scan-participant path — the
/// pre-retyping analyzer's <c>ITypedResourceInstanceSampler&lt;SqlCommandSnapshot&gt;</c> classified
/// state inline during <c>OnHeapEntry</c>; this class does the same classification per candidate,
/// after the shared scan, so the adapter no longer needs a sampler at all.
///
/// The pre-retyping analyzer tracked a fourth "Other" (unreadable-state) bucket internally but never
/// surfaced it in <see cref="SqlCommandTypeSummary"/>/<see cref="SqlCommandDomainResult"/> — this
/// retyping doesn't add it either, since that would be a real behavior change, not a plumbing one.
/// </remarks>
public sealed class SqlCommandAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "SQL Command Analysis";
    public string Category => "Infrastructure";

    private const int StateDisposed = 0;
    private const int StateActive = 1;

    // Field names tried in order when reading the owning connection reference.
    private static readonly string[] ConnectionFieldNames = ["_activeConnection", "_connection", "Connection"];

    public AnalyzerDomainResult? LastResult { get; private set; }

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");

        LastResult = Analyze(fields, context.HeapObjects);
        return ValueTask.CompletedTask;
    }

    private static SqlCommandDomainResult Analyze(IDumpObjectFieldQuery fields, IHeapObjectStream? candidates)
    {
        if (candidates is null)
            return Empty();

        var typeStats = new Dictionary<string, (int Total, int Active, int Disposed, ulong Bytes)>(StringComparer.Ordinal);
        var topActive = new List<SqlCommandSnapshot>();

        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
            int stateVal = TryReadCommandState(fields, candidate.Address);
            typeStats.TryGetValue(candidate.TypeDisplayName, out var ts);

            int active = ts.Active, disposed = ts.Disposed;
            if (stateVal == StateActive)
            {
                active++;
                topActive.Add(new SqlCommandSnapshot(candidate.TypeDisplayName, candidate.Address, "Active", stateVal));
            }
            else if (stateVal == StateDisposed)
            {
                disposed++;
            }

            typeStats[candidate.TypeDisplayName] = (ts.Total + 1, active, disposed, ts.Bytes + candidate.Size);
        }

        if (typeStats.Count == 0)
            return Empty();

        int totalCommands = 0, totalActive = 0, totalDisposed = 0;
        var byType = new List<SqlCommandTypeSummary>(typeStats.Count);
        foreach (var kv in typeStats)
        {
            byType.Add(new SqlCommandTypeSummary(kv.Key, kv.Value.Total, kv.Value.Disposed, kv.Value.Active, kv.Value.Bytes));
            totalCommands += kv.Value.Total;
            totalActive += kv.Value.Active;
            totalDisposed += kv.Value.Disposed;
        }

        byType.Sort(static (a, b) => b.TotalCount.CompareTo(a.TotalCount));

        return new SqlCommandDomainResult(
            CommandsFound: totalCommands > 0,
            TotalCommands: totalCommands,
            DisposedCount: totalDisposed,
            ActiveCount: totalActive,
            ByType: byType,
            TopActiveCommands: topActive);
    }

    private static int TryReadCommandState(IDumpObjectFieldQuery fields, ulong address)
    {
        foreach (string fieldName in ConnectionFieldNames)
        {
            if (!fields.TryReadField(address, fieldName, out FieldValue value))
                continue; // field not declared on this type -- try the next name

            return value.Kind == FieldValueKind.ObjectReference ? StateActive : StateDisposed;
        }

        return -1;
    }

    private static SqlCommandDomainResult Empty() => new(false, 0, 0, 0, [], []);

    public void Dispose() { }
}
