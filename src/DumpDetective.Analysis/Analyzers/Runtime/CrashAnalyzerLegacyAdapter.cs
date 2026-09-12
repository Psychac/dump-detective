using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers.Runtime;

/// <summary>
/// Runs the retyped <see cref="CrashAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Bespoke, not built on <c>TypedResourceCandidateStreamingLegacyAdapter</c> — same reasoning
/// <c>AsyncTaskAnalyzerLegacyAdapter</c>'s own remarks give: exception detection is
/// <c>ClrType.IsException</c>, a structural type fact, not a name predicate that base's
/// <c>IsCandidateType(string typeName)</c> shape can express. This adapter owns every ClrType-level
/// fact the pre-retyping analyzer's own <c>OnHeapEntry</c>/<c>ResolveExceptionType</c> needed
/// (is-exception, is-<c>AggregateException</c>, resolved display name) since no Tier-1/Tier-2 SDK
/// surface exists for "is this type an exception" and building one now, with a single consumer,
/// would be exactly the speculative work this project's conventions reject.
///
/// <see cref="IsAggregateExceptionType"/> results are cached per method table and their matching
/// addresses collected into a side set, handed to the retyped analyzer via
/// <see cref="CrashAnalyzerOptions.AggregateExceptionAddresses"/> — <see cref="Sdk.Analysis.HeapObjectRef"/>
/// has no slot for an arbitrary per-object flag, and this fact isn't needed by any other Tier-1
/// consumer, so a wrapper option record is the right shape (matches
/// <see cref="Types.CollectionAnalyzerOptions"/>'s own precedent for a single-slot, two-record need).
///
/// One accepted simplification, same shape as every other Tier-2-wave adapter's own removal: the
/// pre-retyping analyzer's independent parallel no-index fallback scan
/// (<c>RunParallelExceptionScan</c>, used only when the shared heap-index scan never ran) no longer
/// exists post-retyping — every candidate now always arrives via the pipeline's one shared scan
/// (<c>context.HeapObjects</c>), the same "degraded case can no longer occur by construction"
/// simplification <c>WeakReferenceAnalyzer</c>/<c>TimerLeakAnalyzer</c>/<c>HangAnalyzer</c>/
/// <c>AsyncTaskAnalyzer</c>'s own retypings already made.
/// </remarks>
public sealed class CrashAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<CrashAnalyzer>, IParallelHeapIndexScanParticipant
{
    private ClrHeap? _heap;
    private readonly Dictionary<ulong, bool> _isExceptionByMt = new();
    private readonly Dictionary<ulong, bool> _isAggregateByMt = new();
    private readonly List<Sdk.Analysis.HeapObjectRef> _candidates = new();
    private readonly HashSet<ulong> _aggregateAddresses = new();
    private bool _scanSucceeded = true;

    public CrashAnalyzerLegacyAdapter() : base(new CrashAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.Crash;

    public void BeforeHeapIndexScan(AnalysisContext context) => _heap = context.Heap;

    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
    {
        if (entry.Address == 0 || entry.MethodTable == 0)
            return;

        if (!_isExceptionByMt.TryGetValue(entry.MethodTable, out bool isException))
        {
            ClrType? type = _heap!.GetTypeByMethodTable(entry.MethodTable);
            isException = type?.IsException == true;
            _isExceptionByMt[entry.MethodTable] = isException;
            if (isException)
                _isAggregateByMt[entry.MethodTable] = IsAggregateExceptionType(type);
        }

        if (!isException)
            return;

        string name = _heap!.GetTypeByMethodTable(entry.MethodTable)?.Name ?? $"MT:0x{entry.MethodTable:x}";
        _candidates.Add(new Sdk.Analysis.HeapObjectRef(entry.Address, SdkTypeRefFactory.FromName(name, entry.MethodTable), entry.Size, name));

        if (_isAggregateByMt.TryGetValue(entry.MethodTable, out bool isAggregate) && isAggregate)
            _aggregateAddresses.Add(entry.Address);
    }

    // Walks the base-type chain so AggregateException subclasses (rare, but legal) are still
    // unwrapped, not just the sealed-in-practice BCL type — ported unchanged from the pre-retyping
    // analyzer's own IsAggregateExceptionType.
    private static bool IsAggregateExceptionType(ClrType? type)
    {
        while (type != null)
        {
            if (type.Name == "System.AggregateException")
                return true;
            type = type.BaseType;
        }
        return false;
    }

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new CrashAnalyzerLegacyAdapter();

    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        foreach (IHeapIndexScanParticipant p in partials)
        {
            if (p is not CrashAnalyzerLegacyAdapter other)
                continue;

            _candidates.AddRange(other._candidates);
            foreach (ulong address in other._aggregateAddresses)
                _aggregateAddresses.Add(address);
            if (!other._scanSucceeded)
                _scanSucceeded = false;
        }
    }

    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanSucceeded = succeeded;

    protected override Sdk.Analysis.AnalysisContext BuildSdkContext(AnalysisContext context) =>
        LegacyAnalysisContextTranslator.Translate(
            context,
            new CrashAnalyzerOptions((CrashAnalysisOptions)ResolveOptions(context.AnalysisOptions)!, _scanSucceeded ? _aggregateAddresses : []),
            heapObjectsOverride: new PrecomputedHeapObjectStream(_scanSucceeded ? _candidates : []));
}
