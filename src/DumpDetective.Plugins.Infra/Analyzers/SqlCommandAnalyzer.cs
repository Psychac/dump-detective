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
[AnalyzerModule(key: "sql-command", displayName: "SQL Command Analysis", order: 426, tags: ["infra", "network"])]
[RequiresCapability(
    CapabilityVocabulary.DumpObjectFields,
    CapabilityVocabulary.HeapObjects)]
public sealed class SqlCommandAnalyzer :
    IAnalyzer, IProducesAnalyzerDomainResult, ITypedResourceCandidateSource,
    IParallelHeapIndexScanParticipant, ICustomizesAnalysisContext
{
    // Same provider namespaces as DbConnectionAnalyzer/SqlTransactionAnalyzer.
    private static readonly string[] CommandNamespacePrefixes =
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

    public string Name => "SQL Command Analysis";
    public string Category => "Infrastructure";

    private const int StateDisposed = 0;
    private const int StateActive = 1;

    // Field names tried in order when reading the owning connection reference.
    private static readonly string[] ConnectionFieldNames = ["_activeConnection", "_connection", "Connection"];

    public AnalyzerDomainResult? LastResult
    {
        get => _lastResult;
        // Stamps AnalyzerName/Category here (not left to a bridging adapter, which no
        // longer exists) so every LastResult consumer — pipeline or direct caller — sees
        // them populated the same way.
        private set => _lastResult = value is null ? null : value with { AnalyzerName = Name, Category = Category };
    }
    private AnalyzerDomainResult? _lastResult;

    public bool IsCandidateType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, CommandNamespacePrefixes, "Command", null);

    void IHeapIndexScanParticipant.BeforeHeapIndexScan(RuntimeAnalysisContext context) => _scanState.BeforeHeapIndexScan(this, context);
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => _scanState.OnHeapEntry(in entry);
    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanState.OnHeapIndexScanCompleted(succeeded);
    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new SqlCommandAnalyzer();
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials) =>
        _scanState.MergeFrom(partials.OfType<SqlCommandAnalyzer>().Select(a => a._scanState));

    Sdk.Analysis.AnalysisContext ICustomizesAnalysisContext.BuildAnalysisContext(RuntimeAnalysisContext context) =>
        AnalysisContextBuilder.Build(context, analyzerOptions: null, heapObjectsOverride: new PrecomputedHeapObjectStream(_scanState.Candidates));

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDumpObjectFieldQuery fields = context.DumpObjectFields
            ?? throw new InvalidOperationException($"{Name} requires '{CapabilityVocabulary.DumpObjectFields}' capability.");

        LastResult = await Analyze(fields, context.HeapObjects, context, cancellationToken);
    }

    /// <summary>
    /// Phase 5 step 1 (docs/refactor/modularity/phase-5-observations-synthesis.md): one
    /// <c>infra.sql-command</c> observation per active command instance — only the active subset
    /// (the anomaly-relevant one), not every scanned candidate, same scoping
    /// <c>GCHandleAnalyzer</c>'s pinned-handle emission uses. Inline, where
    /// <see cref="HeapObjectRef.Type"/> is still in scope.
    /// </summary>
    private static async ValueTask<SqlCommandDomainResult> Analyze(
        IDumpObjectFieldQuery fields, IHeapObjectStream? candidates, AnalysisContext observationContext, CancellationToken cancellationToken)
    {
        if (candidates is null)
            return Empty();

        var observationCapabilities = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects };
        var typeStats = new Dictionary<string, (int Total, int Active, int Disposed, ulong Bytes)>(StringComparer.Ordinal);
        var topActive = new List<SqlCommandSnapshot>();

        foreach (HeapObjectRef candidate in candidates.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            int stateVal = TryReadCommandState(fields, candidate.Address);
            typeStats.TryGetValue(candidate.TypeDisplayName, out var ts);

            int active = ts.Active, disposed = ts.Disposed;
            if (stateVal == StateActive)
            {
                active++;
                topActive.Add(new SqlCommandSnapshot(candidate.TypeDisplayName, candidate.Address, "Active", stateVal));

                await observationContext.Observations.EmitAsync(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "infra.sql-command",
                    Subjects = [candidate.Type],
                    When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["shallow.bytes"] = new Measure(candidate.Size, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = observationContext.ArtifactId!.Value,
                        AnalyzerKey = "SQL Command Analysis",
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

        // Whole-run scalar — no entity subject, same "no natural per-entity anchor" pattern as
        // gc.generation-composition. TotalCommands/DisposedCount cover every scanned candidate,
        // including disposed and unreadable-state ones that infra.sql-command never observes
        // individually (only active commands get one, per this analyzer's own Phase 5 step 1
        // remarks) — ActiveCount itself is reconstructable by counting those, so it isn't
        // duplicated here.
        await observationContext.Observations.EmitAsync(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "infra.sql-command-population",
            Subjects = [],
            When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
            Measures = new Dictionary<string, Measure>
            {
                ["total.count"] = new Measure(totalCommands, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["disposed.count"] = new Measure(totalDisposed, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = new Provenance
            {
                Artifact = observationContext.ArtifactId!.Value,
                AnalyzerKey = "SQL Command Analysis",
                CapabilitiesUsed = observationCapabilities,
                Fidelity = FidelityLevel.Full,
            },
            Confidence = 1.0,
        }, cancellationToken);

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
