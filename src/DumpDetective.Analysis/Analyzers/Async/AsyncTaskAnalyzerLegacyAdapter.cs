using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers.Async;

/// <summary>
/// Runs the retyped <see cref="AsyncTaskAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-2 wave.
/// </summary>
/// <remarks>
/// Bespoke, not built on <c>TypedResourceCandidateStreamingLegacyAdapter</c> — that base's
/// <c>IsCandidateType(string typeName)</c> is pure name-based, but <c>IValueTaskSource</c> detection
/// is structural (interface + embedded-field-type match), not expressible as a name predicate.
///
/// Also preserves a fast path <see cref="AsyncStateMachineAnalyzerLegacyAdapter"/>'s own shared-scan
/// design has no equivalent of: the pre-retyping analyzer's own <c>LoadTaskEntries</c> preferred a
/// dedicated <c>TaskIndex.bin</c> satellite index (or <c>HeapIndexBuildResult.InMemoryTaskCandidates</c>
/// in memory-backed mode) over the shared heap-index scan's participant-collected list for Task
/// candidates specifically — <c>TaskCompletionSource</c>/<c>IValueTaskSource</c> never had an
/// equivalent disk fast path, so those two always rode the shared scan. This adapter reproduces the
/// same three-way priority for Task discovery (dedicated index → in-memory candidates →
/// participant-collected list), dropping only the pre-retyping analyzer's fourth tier — an
/// independent raw <c>heap.EnumerateObjects()</c> fallback scan when the shared scan itself never ran
/// — the same accepted simplification <c>TimerLeakAnalyzer</c>/<c>WeakReferenceAnalyzer</c>/
/// <c>HangAnalyzer</c>'s own Phase-B-fallback removals already made: a real analysis session always
/// goes through the pipeline's shared scan.
///
/// One accepted fidelity/perf trade documented, not silently dropped: <c>TaskIndex.bin</c> records
/// carry each task's <c>m_stateFlags</c> read once at index-build time, which the pre-retyping
/// analyzer's Phase 2 reused directly (skipping a ClrMD field read) whenever that fast path won.
/// <see cref="HeapObjectRef"/> has no slot for an arbitrary extra field, so the retyped
/// <see cref="AsyncTaskAnalyzer"/> always re-reads <c>m_stateFlags</c> fresh via
/// <c>IDumpObjectFieldQuery.TryReadField</c> regardless of which candidate source won — the same
/// per-task field-read cost three of the pre-retyping analyzer's four load paths already paid, just
/// no longer skipped on the one path that happened to have it for free.
/// </remarks>
public sealed class AsyncTaskAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<AsyncTaskAnalyzer>, IParallelHeapIndexScanParticipant
{
    private HashSet<ulong>? _taskMts;
    private HashSet<ulong>? _tcsMts;
    private HashSet<ulong>? _vtsMts;
    private List<(ulong Address, ulong Mt, ulong Size)>? _participantTaskEntries;
    private List<(ulong Address, ulong Mt, ulong Size)>? _participantTcsEntries;
    private List<(ulong Address, ulong Mt, ulong Size)>? _participantVtsEntries;
    private bool _scanSucceeded = true;

    public AsyncTaskAnalyzerLegacyAdapter() : base(new AsyncTaskAnalyzer())
    {
    }

    // ── Heap-index-scan participant: discovers Task/TCS/VTS candidate addresses in one shared
    // pass, same three-MT-set shape the pre-retyping analyzer's own BeforeHeapIndexScan used. ──
    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        _participantTaskEntries = new List<(ulong, ulong, ulong)>(capacity: 1024);
        _participantTcsEntries = new List<(ulong, ulong, ulong)>(capacity: 64);
        _participantVtsEntries = new List<(ulong, ulong, ulong)>(capacity: 64);
        _taskMts = null;
        _tcsMts = null;
        _vtsMts = null;

        if (context.Cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
        {
            _taskMts = new HashSet<ulong>(capacity: 32);
            _tcsMts = new HashSet<ulong>(capacity: 8);
            _vtsMts = new HashSet<ulong>(capacity: 8);

            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kvp in heapIndex.TypeAggregates)
            {
                if ((kvp.Value.Flags & TypeAggregateFlags.IsTaskType) != 0)
                {
                    _taskMts.Add(kvp.Key);
                    continue;
                }

                // No precomputed TypeAggregateFlags bit for TaskCompletionSource or
                // IValueTaskSource -- resolve the type once per distinct MT here (bounded by
                // type count, not object count) and check both.
                ClrType? type = context.Heap.GetTypeByMethodTable(kvp.Key);
                if (type is null)
                    continue;

                if (type.Name != null && TaskTypeNamePattern.IsTaskCompletionSource(type.Name))
                {
                    _tcsMts.Add(kvp.Key);
                    continue;
                }

                if (ValueTaskSourcePattern.ImplementsValueTaskSource(type) && ValueTaskSourcePattern.FindCoreField(type) is not null)
                    _vtsMts.Add(kvp.Key);
            }
        }
    }

    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
    {
        if (_taskMts is not null && _taskMts.Contains(entry.MethodTable))
        {
            _participantTaskEntries!.Add((entry.Address, entry.MethodTable, entry.Size));
            return;
        }

        if (_tcsMts is not null && _tcsMts.Contains(entry.MethodTable))
        {
            _participantTcsEntries!.Add((entry.Address, entry.MethodTable, entry.Size));
            return;
        }

        if (_vtsMts is not null && _vtsMts.Contains(entry.MethodTable))
        {
            _participantVtsEntries!.Add((entry.Address, entry.MethodTable, entry.Size));
        }
    }

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new AsyncTaskAnalyzerLegacyAdapter();

    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        foreach (IHeapIndexScanParticipant p in partials)
        {
            if (p is not AsyncTaskAnalyzerLegacyAdapter other)
                continue;

            _participantTaskEntries!.AddRange(other._participantTaskEntries!);
            _participantTcsEntries!.AddRange(other._participantTcsEntries!);
            _participantVtsEntries!.AddRange(other._participantVtsEntries!);
            if (!other._scanSucceeded)
                _scanSucceeded = false;
        }
    }

    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanSucceeded = succeeded;

    protected override Sdk.Analysis.AnalysisContext BuildSdkContext(AnalysisContext context) =>
        LegacyAnalysisContextTranslator.Translate(
            context, ResolveOptions(context.AnalysisOptions),
            heapObjectsOverride: new PrecomputedHeapObjectStream(BuildCandidateList(context)));

    private List<Sdk.Analysis.HeapObjectRef> BuildCandidateList(AnalysisContext context)
    {
        var typeNameByMt = new Dictionary<ulong, string>(capacity: 64);
        var result = new List<Sdk.Analysis.HeapObjectRef>(capacity: 1024);

        AppendCandidates(result, LoadTaskCandidates(context), context.Heap, typeNameByMt);
        AppendCandidates(result, _scanSucceeded ? (_participantTcsEntries ?? []) : [], context.Heap, typeNameByMt);
        AppendCandidates(result, _scanSucceeded ? (_participantVtsEntries ?? []) : [], context.Heap, typeNameByMt);

        return result;
    }

    // Three-way priority mirroring the pre-retyping analyzer's own LoadTaskEntries: dedicated
    // TaskIndex.bin satellite index, then memory-backed mode's InMemoryTaskCandidates, then the
    // shared scan's participant-collected list. Size is always 0 here for the two disk/in-memory
    // fast-path sources (neither format carries it) -- AppendCandidates resolves it with one
    // heap.GetObject call per entry, same per-task ClrMD cost the pre-retyping analyzer's own
    // Phase 2 always paid via taskObj.Size, just relocated here.
    private IReadOnlyList<(ulong Address, ulong Mt, ulong Size)> LoadTaskCandidates(AnalysisContext context)
    {
        if (context.Cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
        {
            if (heapIndex.InMemoryTaskCandidates is { Length: > 0 } inMemory)
            {
                var converted = new List<(ulong, ulong, ulong)>(inMemory.Length);
                foreach ((ulong addr, ulong mt) in inMemory)
                    converted.Add((addr, mt, 0));
                return converted;
            }

            List<(ulong Address, ulong Mt, int StateFlags)>? diskEntries =
                TaskIndexReader.ReadTaskIndexFile(heapIndex.IndexPath, CancellationToken.None);
            if (diskEntries is { Count: > 0 })
            {
                var converted = new List<(ulong, ulong, ulong)>(diskEntries.Count);
                foreach ((ulong address, ulong mt, int _) in diskEntries)
                    converted.Add((address, mt, 0));
                return converted;
            }
        }

        return _scanSucceeded ? (_participantTaskEntries ?? []) : [];
    }

    private static void AppendCandidates(
        List<Sdk.Analysis.HeapObjectRef> result,
        IReadOnlyList<(ulong Address, ulong Mt, ulong Size)> entries,
        ClrHeap heap,
        Dictionary<ulong, string> typeNameByMt)
    {
        foreach ((ulong address, ulong mt, ulong size) in entries)
        {
            if (!typeNameByMt.TryGetValue(mt, out string? name))
            {
                name = heap.GetTypeByMethodTable(mt)?.Name ?? $"MT:0x{mt:x}";
                typeNameByMt[mt] = name;
            }

            ulong resolvedSize = size;
            if (resolvedSize == 0)
            {
                ClrObject obj = heap.GetObject(address);
                if (obj.IsValid)
                    resolvedSize = obj.Size;
            }

            result.Add(new Sdk.Analysis.HeapObjectRef(address, SdkTypeRefFactory.FromName(name, mt), resolvedSize, name));
        }
    }
}
