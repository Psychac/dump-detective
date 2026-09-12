using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers.Threads;

/// <summary>
/// Runs the retyped <see cref="HangAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Bespoke, not built on <c>TypedResourceCandidateStreamingLegacyAdapter</c> — <c>HangAnalyzer</c> is
/// the one analyzer needing both a heap-index-scan participant role (to discover
/// Task/QueuedWorkItem/continuation-shaped objects) and a thread-stack-scan participant role
/// (to detect waiting threads) simultaneously; the shared adapter was built for heap-scan-only
/// consumers and doesn't compose with <see cref="IThreadStackScanParticipant"/> without duplicating
/// its private state. Implements both scan-participant interfaces directly instead, mirroring what
/// the pre-retyping analyzer's own dual role already did, just moved to this layer — the same
/// relocation every other quartet member's marker/participant interfaces already went through.
///
/// Per-method-table classification (<see cref="AsyncTypeProfile.FromTypeName"/>) happens once per
/// distinct method table here (cached), same as the pre-retyping analyzer's own
/// <c>profileByMethodTable</c> cache — only the *fact* "is this candidate potentially relevant"
/// gates whether a <c>HeapObjectRef</c> is collected; the retyped analyzer re-derives which specific
/// category (Task/QueuedWorkItem/Continuation) a collected candidate belongs to from its type name,
/// a pure string operation needing no further ClrMD access.
/// </remarks>
public sealed class HangAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<HangAnalyzer>, IParallelHeapIndexScanParticipant, IThreadStackScanParticipant
{
    private ClrHeap? _heap;
    private readonly Dictionary<ulong, string?> _relevantNameByMt = new();
    private readonly List<Sdk.Analysis.HeapObjectRef> _candidates = new();
    private bool _heapScanSucceeded = true;

    private Dictionary<uint, Sdk.Analysis.RuntimeThreadRef>? _threadsByOsId;
    private Dictionary<uint, IReadOnlyList<Sdk.Analysis.ThreadStackFrameRef>>? _framesByOsId;
    private bool _threadScanSucceeded;

    public HangAnalyzerLegacyAdapter() : base(new HangAnalyzer())
    {
    }

    // ── Heap-index-scan participant ──────────────────────────────────────────────
    public void BeforeHeapIndexScan(AnalysisContext context) => _heap = context.Heap;

    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
    {
        if (entry.Address == 0 || entry.MethodTable == 0)
            return;

        if (!_relevantNameByMt.TryGetValue(entry.MethodTable, out string? name))
        {
            ClrType? type = _heap!.GetTypeByMethodTable(entry.MethodTable);
            string resolvedName = type?.Name ?? string.Empty;
            name = AsyncTypeProfile.FromTypeName(resolvedName).IsPotentiallyRelevant ? resolvedName : null;
            _relevantNameByMt[entry.MethodTable] = name;
        }

        if (name is null)
            return;

        _candidates.Add(new Sdk.Analysis.HeapObjectRef(entry.Address, SdkTypeRefFactory.FromName(name, entry.MethodTable), entry.Size, name));
    }

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new HangAnalyzerLegacyAdapter();

    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        foreach (IHeapIndexScanParticipant p in partials)
        {
            if (p is HangAnalyzerLegacyAdapter other)
            {
                _candidates.AddRange(other._candidates);
                if (!other._heapScanSucceeded)
                    _heapScanSucceeded = false;
            }
        }
    }

    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _heapScanSucceeded = succeeded;

    // ── Thread-stack-scan participant ────────────────────────────────────────────
    // Only the top frame is needed (HangAnalyzer's own wait-pattern check looks at the innermost
    // frame only), matching the pre-retyping analyzer's own GetRequiredFrameCount => 1.
    public int GetRequiredFrameCount(AnalysisContext context) => 1;

    public void BeforeThreadStackScan(AnalysisContext context)
    {
        _threadsByOsId = new Dictionary<uint, Sdk.Analysis.RuntimeThreadRef>();
        _framesByOsId = new Dictionary<uint, IReadOnlyList<Sdk.Analysis.ThreadStackFrameRef>>();
    }

    // HangAnalyzer never reads RuntimeThreadRef.StackRootCount (its domain result carries no such
    // field), so stackRootCount stays 0 here, same as LockGraphAnalyzerLegacyAdapter's own precedent
    // for a fact it never needed either.
    void IThreadStackScanParticipant.OnThreadStack(in ThreadStackSnapshot snapshot)
    {
        ClrThread thread = snapshot.Thread;
        _threadsByOsId![thread.OSThreadId] = ThreadStackTranslator.ToThreadRef(thread, stackRootCount: 0);

        var frames = new List<Sdk.Analysis.ThreadStackFrameRef>(snapshot.TopFrames.Count);
        foreach (ClrStackFrame frame in snapshot.TopFrames)
            frames.Add(ThreadStackTranslator.ToFrameRef(frame));
        _framesByOsId![thread.OSThreadId] = frames;
    }

    public void OnThreadStackScanCompleted(bool succeeded) => _threadScanSucceeded = succeeded;

    protected override Sdk.Analysis.AnalysisContext BuildSdkContext(AnalysisContext context)
    {
        Sdk.Analysis.IRuntimeThreadQuery? precomputedThreads = _threadScanSucceeded
            ? new PrecomputedRuntimeThreadQuery(_threadsByOsId!, _framesByOsId!)
            : null;

        return LegacyAnalysisContextTranslator.Translate(
            context, ResolveOptions(context.AnalysisOptions),
            runtimeThreadsOverride: precomputedThreads,
            heapObjectsOverride: new PrecomputedHeapObjectStream(_heapScanSucceeded ? _candidates : []));
    }
}
