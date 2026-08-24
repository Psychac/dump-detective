using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Cache;

internal readonly record struct RootRecord(ulong TargetAddr, ulong RootAddr, byte Kind)
{
    private const byte PinnedHandleKind = 3;
    private const byte ThreadStaticVarKind = 9;
    private const byte StaticVarKind = 10;
    private const byte AsyncPinnedHandleKind = 7;

    public string KindName => RootIndexReader.KindToString(Kind);

    public bool IsStatic => Kind is ThreadStaticVarKind or StaticVarKind;

    public bool IsPinned => Kind is PinnedHandleKind or AsyncPinnedHandleKind;
}

/// <summary>
/// Canonical per-run root set: reads the Phase-1 disk root index when available,
/// falling back to a live <see cref="ClrHeap.EnumerateRoots"/> walk otherwise.
/// The single source of truth for root data.
/// </summary>
internal class RootSetCache
{
    private IReadOnlyList<RootRecord>? _roots;
    private HashSet<ulong>? _staticRootedAddresses;
    private HashSet<ulong>? _pinnedRootedAddresses;
    private Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)>? _staticFieldsByRootAddress;
    private Dictionary<ulong, (string OwnerType, string MethodName)>? _stackFrameOwnersByRootAddress;
    private IProgress<AnalyzerProgressReport>? _progress;
    private DateTime? _lastBuildTime;
    private string? _lastBuildError;

    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;

    public RootSetCache(Func<HeapIndexBuildResult?> getHeapIndex)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
    }

    public void SetProgress(IProgress<AnalyzerProgressReport>? progress)
    {
        _progress = progress;
    }

    public IReadOnlyList<RootRecord> GetOrBuildRoots(ClrHeap heap, CancellationToken cancellationToken = default)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_roots is not null)
            return _roots;

        var builtIndex = _getHeapIndex();
        if (builtIndex is not null)
        {
            try
            {
                var candidates = RootIndexReader.ReadRootCandidates(builtIndex, cancellationToken);
                if (candidates.Count > 0)
                {
                    var fromIndex = new List<RootRecord>(candidates.Count);
                    foreach ((ulong targetAddr, ulong rootAddr, byte kind) in candidates)
                        fromIndex.Add(new RootRecord(targetAddr, rootAddr, kind));

                    _roots = fromIndex;
                    _lastBuildTime = DateTime.UtcNow;
                    _lastBuildError = null;
                    return _roots;
                }
            }
            catch
            {
                // Fall back to a live heap walk on any read error.
            }
        }

        _roots = BuildFromLiveHeap(heap);
        _lastBuildTime ??= DateTime.UtcNow;
        return _roots;
    }

    public HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_staticRootedAddresses is not null)
            return _staticRootedAddresses;

        var roots = GetOrBuildRoots(heap);
        var statics = new HashSet<ulong>(capacity: Math.Max(256, roots.Count));
        foreach (RootRecord root in roots)
        {
            if (root.IsStatic)
                statics.Add(root.TargetAddr);
        }

        _staticRootedAddresses = statics;
        return _staticRootedAddresses;
    }

    /// <summary>
    /// Addresses targeted by a <c>PinnedHandle</c> or <c>AsyncPinnedHandle</c> GC root — the
    /// objects a <c>GCHandle.Alloc(obj, GCHandleType.Pinned)</c> (or async-pinned I/O buffer)
    /// prevents the GC from moving/compacting around. Reads the same Phase-1 disk root index as
    /// <see cref="GetOrBuildRoots"/>, so this costs nothing beyond the one-time root-index read
    /// already paid for by other consumers (e.g. <see cref="GetStaticRootedAddresses"/>).
    /// </summary>
    public HashSet<ulong> GetPinnedRootedAddresses(ClrHeap heap)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_pinnedRootedAddresses is not null)
            return _pinnedRootedAddresses;

        var roots = GetOrBuildRoots(heap);
        var pinned = new HashSet<ulong>(capacity: 256);
        foreach (RootRecord root in roots)
        {
            if (root.IsPinned)
                pinned.Add(root.TargetAddr);
        }

        _pinnedRootedAddresses = pinned;
        return _pinnedRootedAddresses;
    }

    /// <summary>
    /// Maps a static/thread-static root's field *storage* address
    /// (<see cref="ClrStaticField.GetAddress(ClrAppDomain)"/>, matching <c>ClrRoot.Address</c> /
    /// <see cref="RootRecord.RootAddr"/> for those root kinds) to the declaring type and field name.
    /// Keyed by the field's own address rather than the value it holds, so multiple fields that
    /// happen to reference the same target object each resolve to their own correct name instead
    /// of colliding on a shared key. Reads the Phase-1 disk index when available; falls back to a
    /// live <see cref="StaticFieldResolver"/> scan otherwise (or on any read error).
    /// </summary>
    public Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)> GetStaticFieldsByRootAddress(ClrHeap heap)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_staticFieldsByRootAddress is not null)
            return _staticFieldsByRootAddress;

        var builtIndex = _getHeapIndex();
        if (builtIndex is not null)
        {
            try
            {
                var fromIndex = RootIndexReader.ReadRootFieldNames(builtIndex, CancellationToken.None);
                if (fromIndex.Count > 0)
                {
                    _staticFieldsByRootAddress = fromIndex;
                    return _staticFieldsByRootAddress;
                }
            }
            catch
            {
                // Fall back to a live scan on any read error.
            }
        }

        var staticRootAddresses = new HashSet<ulong>();
        foreach (RootRecord root in GetOrBuildRoots(heap))
        {
            if (root.IsStatic)
                staticRootAddresses.Add(root.RootAddr);
        }

        _staticFieldsByRootAddress = StaticFieldResolver.BuildMapByRootAddress(heap, staticRootAddresses);
        return _staticFieldsByRootAddress;
    }

    /// <summary>
    /// Resolves a <c>Stack</c>-kind root's owning method by correlating its stack-slot address
    /// (<paramref name="rootAddr"/>, matching <c>RootRecord.RootAddr</c>) against a per-thread
    /// walk of <see cref="ClrThread.EnumerateStackTrace(bool, int)"/> frame
    /// <see cref="ClrStackFrame.StackPointer"/> ranges — not an exact local-variable name (ClrMD
    /// exposes no PDB-based variable-slot mapping), but the method that owns the frame the slot
    /// lives in. See docs/analysis/root-field-name-index-plan.md (Mechanism B) for why this is a
    /// separate live, on-demand path rather than a <see cref="GetStaticFieldsByRootAddress"/>-style
    /// disk-persisted map: correlating roots to frames requires enumerating
    /// <see cref="ClrThread.EnumerateStackRoots"/> per thread (there is no thread affiliation on
    /// an aggregate <c>heap.EnumerateRoots()</c> root), and is deliberately built lazily — the
    /// first call walks every thread once and caches the result for the rest of the analysis run;
    /// callers should therefore only invoke this for the small set of top-severity
    /// <c>Stack</c>-kind findings that actually make it into a report, not for every stack root in
    /// the dump.
    /// </summary>
    public bool TryResolveStackFrameOwner(ClrHeap heap, ulong rootAddr, out string ownerType, out string methodName)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        ownerType = "";
        methodName = "";
        if (rootAddr == 0)
            return false;

        _stackFrameOwnersByRootAddress ??= BuildStackFrameOwnerMap(heap);

        if (_stackFrameOwnersByRootAddress.TryGetValue(rootAddr, out (string OwnerType, string MethodName) info))
        {
            ownerType = info.OwnerType;
            methodName = info.MethodName;
            return true;
        }

        return false;
    }

    // ClrThread.EnumerateStackTrace's own doc warns it "may loop infinitely in the case of stack
    // corruption" — always call the maxFrames overload, never the unbounded one.
    private const int MaxFramesPerThread = 256;

    private static Dictionary<ulong, (string OwnerType, string MethodName)> BuildStackFrameOwnerMap(ClrHeap heap)
    {
        var map = new Dictionary<ulong, (string, string)>();

        foreach (ClrThread thread in heap.Runtime.Threads)
        {
            if (!thread.IsAlive)
                continue;

            var frames = new List<ClrStackFrame>(64);
            foreach (ClrStackFrame frame in thread.EnumerateStackTrace(includeContext: false, maxFrames: MaxFramesPerThread))
                frames.Add(frame);

            if (frames.Count == 0)
                continue;

            var stackPointers = new ulong[frames.Count];
            for (int i = 0; i < frames.Count; i++)
                stackPointers[i] = frames[i].StackPointer;

            // Frames come back innermost-first; StackPointer should increase monotonically
            // outward toward the caller on the conventional (x64) stack. A misordered/corrupted
            // stack breaks the range-correlation binary search — skip this thread's roots
            // entirely rather than risk silently attributing a root to the wrong frame.
            if (!StackFrameRangeCorrelator.IsSortedAscending(stackPointers))
                continue;

            foreach (ClrRoot root in thread.EnumerateStackRoots())
            {
                ulong slotAddr = root.Address;
                // Address == 0 means the value is enregistered rather than spilled to the
                // stack — no slot to correlate against a frame range.
                if (slotAddr == 0 || map.ContainsKey(slotAddr))
                    continue;

                int frameIndex = StackFrameRangeCorrelator.FindOwningFrameIndex(stackPointers, slotAddr);
                if (frameIndex < 0)
                    continue;

                ClrMethod? method = frames[frameIndex].Method;
                if (method?.Type?.Name is string ownerType && method.Name is string methodName)
                    map[slotAddr] = (ownerType, methodName);
            }
        }

        return map;
    }

    /// <summary>
    /// Compatibility projection for call sites still consuming the legacy
    /// <c>(string RootKind, ulong Address)</c> shape. Delete once all callers use
    /// <see cref="RootRecord"/> directly.
    /// </summary>
    public IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap)
    {
        var roots = GetOrBuildRoots(heap);
        var projected = new List<(string RootKind, ulong Address)>(roots.Count);
        foreach (RootRecord root in roots)
            projected.Add((root.KindName, root.TargetAddr));

        return projected;
    }

    /// <summary>
    /// Compatibility projection exposing both the target object address and the root's own
    /// storage address (<c>ClrRoot.Address</c>) — needed by callers that must resolve a root's
    /// declaring field via <see cref="GetStaticFieldsByRootAddress"/>, which is keyed on the
    /// latter. Delete once all callers use <see cref="RootRecord"/> directly.
    /// </summary>
    public IReadOnlyList<(string RootKind, ulong TargetAddr, ulong RootAddr)> GetOrBuildRootTriples(ClrHeap heap)
    {
        var roots = GetOrBuildRoots(heap);
        var projected = new List<(string RootKind, ulong TargetAddr, ulong RootAddr)>(roots.Count);
        foreach (RootRecord root in roots)
            projected.Add((root.KindName, root.TargetAddr, root.RootAddr));

        return projected;
    }

    private List<RootRecord> BuildFromLiveHeap(ClrHeap heap)
    {
        var roots = new List<RootRecord>(capacity: 4096);
        var scanCounter = new ObjectScanCounter("enumerating roots", _progress, reportEveryObjects: 10_000, reportEveryElapsed: TimeSpan.FromSeconds(1));

        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            scanCounter.Tick();

            ulong address = root.Object.Address;
            if (address == 0)
                continue;

            roots.Add(new RootRecord(address, root.Address, (byte)root.RootKind));
        }

        scanCounter.Complete();
        return roots;
    }

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(RootSetCache),
            LastBuildDurationMs = null,
            LastBuildStatus = _lastBuildError is null ? "success" : "failure",
            EntryCount = _roots?.Count ?? 0,
            MemoryUsageBytes = _staticRootedAddresses?.Count * sizeof(ulong) ?? 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError
        };
    }
}
