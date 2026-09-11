using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Analyzers.Infra;

/// <summary>
/// Runs the retyped <see cref="SqlConnectionPoolAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-2 pilot.
/// </summary>
/// <remarks>
/// Candidate discovery (<see cref="ITypedResourceCandidateSource"/>/
/// <see cref="IParallelHeapIndexScanParticipant"/>) stays on this adapter, not the inner analyzer —
/// the same reason <c>GCRootAnalyzerLegacyAdapter</c> carries its dominator-tree marker interfaces:
/// pipeline gating inspects the *registered* <c>Core.Abstractions.IAnalyzer</c> instance, which the
/// inner SDK analyzer never is. Discovery here only identifies candidate addresses/type names during
/// the pipeline's single shared heap-index scan — the field-*value* extraction that used to happen
/// inline in the pre-retyping analyzer's own <c>OnHeapEntry</c> now happens in
/// <c>SqlConnectionPoolAnalyzer.AnalyzeAsync</c> via <see cref="Sdk.Analysis.IDumpObjectFieldQuery"/>,
/// so this adapter hands the discovered candidates across via <see cref="LegacyAnalysisContextTranslator"/>'s
/// existing <c>analyzerOptions</c> parameter (<see cref="BuildSdkContext"/>) instead of building
/// <see cref="SqlConnectionPoolSnapshot"/>s itself the way the pre-retyping analyzer did.
/// </remarks>
public sealed class SqlConnectionPoolAnalyzerLegacyAdapter :
    LegacyAnalyzerAdapter<SqlConnectionPoolAnalyzer>, IParallelHeapIndexScanParticipant, ITypedResourceCandidateSource
{
    private static readonly HashSet<string> PoolTypeNames = new(StringComparer.Ordinal)
    {
        "System.Data.ProviderBase.DbConnectionPool",
        "Microsoft.Data.ProviderBase.DbConnectionPool",
    };

    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private readonly List<SqlConnectionPoolCandidate> _candidates = new();

    public SqlConnectionPoolAnalyzerLegacyAdapter() : base(new SqlConnectionPoolAnalyzer())
    {
    }

    public bool IsCandidateType(string typeName) => PoolTypeNames.Contains(typeName);

    public void BeforeHeapIndexScan(AnalysisContext context) =>
        _candidateMts = TypedResourceScanDriver.DiscoverCandidates(this, context.Heap, context.Cache);

    // Explicit interface implementations: HeapEntry/IHeapIndexScanParticipant are internal, so a
    // public member can't expose them in its signature (CS0050/CS0051) — same accessibility
    // constraint LockGraphAnalyzerLegacyAdapter's own OnThreadStack already hit for the same reason.
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
    {
        if (_candidateMts is null || !_candidateMts.TryGetValue(entry.MethodTable, out var candidate))
            return;
        _candidates.Add(new SqlConnectionPoolCandidate(entry.Address, candidate.TypeName));
    }

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new SqlConnectionPoolAnalyzerLegacyAdapter();

    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        foreach (IHeapIndexScanParticipant p in partials)
        {
            if (p is SqlConnectionPoolAnalyzerLegacyAdapter other)
                _candidates.AddRange(other._candidates);
        }
    }

    protected override Sdk.Analysis.AnalysisContext BuildSdkContext(AnalysisContext context) =>
        LegacyAnalysisContextTranslator.Translate(context, _candidates);
}
