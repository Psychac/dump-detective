using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

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
[AnalyzerModule(key: "sql-transaction", displayName: "SQL Transaction Analysis", order: 425, tags: ["infra", "network"])]
[RequiresCapability(
    CapabilityVocabulary.DumpObjectFields,
    CapabilityVocabulary.HeapObjects)]
public sealed class SqlTransactionAnalyzer :
    IAnalyzer, IProducesAnalyzerDomainResult, ITypedResourceCandidateSource,
    IParallelHeapIndexScanParticipant, ICustomizesAnalysisContext
{
    // Same provider namespaces as DbConnectionAnalyzer -- transaction types live alongside
    // connection types in each provider's assembly.
    private static readonly string[] TransactionNamespacePrefixes =
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

    private readonly TypedResourceHeapIndexScanState _scanState = new();

    public string Name => "SQL Transaction Analysis";
    public string Category => "Infrastructure";

    private const int StateDisposed = 0;
    private const int StateActive = 1;

    // Field names tried in order when reading the owning connection reference.
    private static readonly string[] ConnectionFieldNames = ["_connection", "_internalConnection", "Connection"];

    public bool IsCandidateType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, TransactionNamespacePrefixes, "Transaction", null);

    void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _scanState.BeforeHeapIndexScan(this, context);
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => _scanState.OnHeapEntry(in entry);
    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanState.OnHeapIndexScanCompleted(succeeded);
    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new SqlTransactionAnalyzer();
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) =>
        _scanState.MergeFrom(partials.OfType<SqlTransactionAnalyzer>().Select(a => a._scanState));

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
    /// <c>infra.sql-transaction</c> observation per active transaction instance — same "only the
    /// anomaly-relevant subset, inline" scoping as <c>SqlCommandAnalyzer</c>.
    /// </summary>
    private static async ValueTask<SqlTransactionDomainResult> Analyze(
        IDumpObjectFieldQuery fields, IHeapObjectStream? candidates, AnalysisContext observationContext, CancellationToken cancellationToken)
    {
        if (candidates is null)
            return Empty();

        var observationCapabilities = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects };
        var typeStats = new Dictionary<string, (int Total, int Active, int Disposed, int Other, ulong Bytes)>(StringComparer.Ordinal);
        var topActive = new List<SqlTransactionSnapshot>();

        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            (int stateVal, ulong? connectionAddress) = TryReadTransactionState(fields, candidate.Address);
            typeStats.TryGetValue(candidate.TypeDisplayName, out var ts);

            int active = ts.Active, disposed = ts.Disposed, other = ts.Other;
            if (stateVal == StateActive)
            {
                active++;
                topActive.Add(new SqlTransactionSnapshot(candidate.TypeDisplayName, candidate.Address, "Active", stateVal, connectionAddress));

                // Phase 5 step 6.2 (docs/refactor/modularity/phase-5-step6-insight-engine-plan.md):
                // LongHeldTransactionOnOpenConnectionInsightRule needs to correlate this transaction
                // against DbConnectionAnalyzer's open-connection set — by the referenced connection's
                // own heap address, which had no home among Subjects before. ObjectRef's identity is
                // (artifact, address), explicitly scoped to within-artifact joins (see its own
                // remarks) — this is exactly that: both analyzers read the same dump in the same run,
                // never a cross-artifact join. Only present when a connection reference was actually
                // read (StateActive doesn't guarantee ConnectionAddress is non-null).
                var subjects = connectionAddress is ulong connAddr
                    ? new EntityRef[] { candidate.Type, new ObjectRef { Address = connAddr, Artifact = observationContext.ArtifactId!.Value, Fidelity = MatchFidelity.Exact } }
                    : [candidate.Type];

                await observationContext.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "infra.sql-transaction",
                    Subjects = subjects,
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["shallow.bytes"] = new Measure(candidate.Size, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = observationContext.ArtifactId!.Value,
                        AnalyzerKey = "SQL Transaction Analysis",
                        CapabilitiesUsed = observationCapabilities,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                }, cancellationToken);
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

        // Whole-run scalar — no entity subject, same "no natural per-entity anchor" pattern as
        // gc.generation-composition. TotalTransactions/DisposedCount/OtherCount cover every
        // scanned candidate, including disposed and unresolvable-state ones that
        // infra.sql-transaction never observes individually (only active transactions get one).
        // active.count is reconstructable by counting those, but is also emitted directly here
        // (Phase 5 step 4) since ObservationTrendDiffer can't do that grouping — same pragmatic
        // direct-emit call as infra.sql-connection-pool-population.near-capacity.count.
        await observationContext.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "infra.sql-transaction-population",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["total.count"] = new Measure(totalTransactions, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["active.count"] = new Measure(totalActive, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["disposed.count"] = new Measure(totalDisposed, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["other.count"] = new Measure(totalOther, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = new Provenance
            {
                Artifact = observationContext.ArtifactId!.Value,
                AnalyzerKey = "SQL Transaction Analysis",
                CapabilitiesUsed = observationCapabilities,
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);

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
