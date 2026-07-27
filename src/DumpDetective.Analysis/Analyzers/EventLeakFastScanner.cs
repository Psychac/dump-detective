using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Analyzers;

// ─────────────────────────────────────────────────────────────────────────
// Type alias so the calling code can use a shorter name for the group key.
// ─────────────────────────────────────────────────────────────────────────
using GroupKey = (string PublisherType, string EventFieldName, bool IsStatic);

/// <summary>
/// High-performance two-phase event-leak scanner.
///
/// Phase A — type index:
///   For each unique MethodTable seen in the heap index, resolve the ClrType exactly once
///   (metadata only, no dump reads) and record the byte offset of every delegate-typed
///   instance field that looks like an event backing field.
///
/// Phase B — heap scan:
///   For each heap entry whose MT is in the index, use <see cref="IMemoryReader.ReadPointer"/>
///   at the pre-computed field offset.  This is a single 8-byte read at a known address —
///   no <c>ClrObject</c> construction, no MethodTable re-read, no ClrMD overhead.
///   • If the pointer is zero the event has no subscribers → 1 read, skip.
///   • If non-zero, follow the <c>MulticastDelegate._invocationList</c> / <c>._target</c>
///     chain using further direct reads (2–5 reads for a typical leaking event).
///   Subscriber type names are resolved lazily after the scan from a small MT→Name table.
///
/// Both the memory-backed and streaming paths process entries sequentially — see
/// <see cref="Scan"/> for why a parallel Phase B was rejected.
/// </summary>
internal sealed class EventLeakFastScanner
{
    // ──────────────────────────────────────────────────────────────────────────────
    // Field layout — stored per-publisher MethodTable
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Represents a single delegate-typed instance field of a publisher type.</summary>
    internal readonly struct DelegateFieldLayout
    {
        /// <summary>Byte offset from the start of the object (same frame as <c>ClrInstanceField.Offset</c>).</summary>
        public readonly int Offset;
        public readonly string FieldName;
        public readonly string PublisherTypeName;

        public DelegateFieldLayout(int offset, string fieldName, string publisherTypeName)
        {
            Offset = offset;
            FieldName = fieldName;
            PublisherTypeName = publisherTypeName;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // MulticastDelegate internal layout — discovered once from the first delegate type found.
    // All MulticastDelegate subclasses share identical offsets for _target / _invocationList.
    // ──────────────────────────────────────────────────────────────────────────────
    private int _delegateTargetOffset;
    private int _delegateInvListOffset;
    /// <summary>
    /// Absolute offset of <c>_invocationCount</c> (nint, immediately follows _invocationList).
    /// When <c>_invocationList</c> is an <c>object[]</c>, this is the number of VALID entries
    /// (the array may be over-allocated via doubling; entries beyond this index are null).
    /// </summary>
    private int _delegateInvCountOffset;
    private bool _delegateLayoutDiscovered;

    // ──────────────────────────────────────────────────────────────────────────────
    // Core state
    // ──────────────────────────────────────────────────────────────────────────────
    private readonly ClrHeap _heap;
    private readonly IMemoryReader _reader;
    private readonly bool _readerIsThreadSafe;
    private readonly int _ptrSize;
    private readonly IProgress<AnalyzerProgressReport>? _progress;
    private readonly Stopwatch _reporterStopwatch;
    private long _scannedObjects;
    private long _lastReportMs;
    private readonly int _reportEveryObjects = 100_000;

    /// <summary>
    /// Offset of element[0] within an <c>object[]</c> array:
    /// <c>MT(ptrSize) + length(4 padded to ptrSize)</c> = <c>ptrSize × 2</c>.
    /// </summary>
    private readonly int _arrayDataOffset;

    /// <summary>MT → delegate field layouts; absent key = not yet evaluated; null value = no delegate fields.</summary>
    private readonly Dictionary<ulong, DelegateFieldLayout[]?> _mtIndex = new(capacity: 8192);

    /// <summary>Subscriber MT → resolved type name (deferred to keep the hot scan free of ClrMD calls).</summary>
    private readonly Dictionary<ulong, string> _subscriberTypeNames = new(capacity: 512);

    /// <summary>
    /// Instruction pointer → resolved method name. Leaked events typically have the same handler
    /// method subscribed many times over (one handler, many publisher instances), so without this
    /// cache <see cref="ResolveSubscriberTypes"/> repeats the same expensive
    /// <see cref="ClrRuntime.GetMethodByInstructionPointer"/> DAC symbol lookup for the same IP.
    /// </summary>
    private readonly Dictionary<ulong, string?> _methodNameByInstructionPointer = new(capacity: 512);

    /// <summary>Shared event-name cache injected from the owner <see cref="EventLeakAnalyzer"/>.</summary>
    private readonly Func<ClrType, HashSet<string>> _getEventNames;

    // PERF INVESTIGATION (temporary): accumulated ticks split between the once-per-unique-MT
    // metadata walk (BuildFieldLayouts) and the per-object hot path (ProcessPublisherEntry).
    private long _buildFieldLayoutsTicks;
    private long _processPublisherEntryTicks;

    internal (double BuildFieldLayoutsMs, double ProcessPublisherEntryMs) GetScanTimings() =>
        (
            _buildFieldLayoutsTicks * 1000.0 / Stopwatch.Frequency,
            _processPublisherEntryTicks * 1000.0 / Stopwatch.Frequency
        );

    // ──────────────────────────────────────────────────────────────────────────────
    // Construction
    // ──────────────────────────────────────────────────────────────────────────────

    public EventLeakFastScanner(ClrHeap heap, Func<ClrType, HashSet<string>> getEventNames, IProgress<AnalyzerProgressReport>? progress = null)
    {
        _heap = heap;
        _getEventNames = getEventNames;

        IDataReader dr = heap.Runtime.DataTarget.DataReader;
        // IDataReader : IMemoryReader in ClrMD 3.x — direct cast is safe.
        _reader = (IMemoryReader)dr;
        _readerIsThreadSafe = dr.IsThreadSafe;
        _ptrSize = dr.PointerSize;
        _arrayDataOffset = _ptrSize * 2;

        _progress = progress;
        _reporterStopwatch = Stopwatch.StartNew();
        _scannedObjects = 0;
        _lastReportMs = 0;

        // Proactively discover MulticastDelegate field layout before any scan starts.
        // Searching known System.* type names via modules is more reliable than waiting
        // to stumble across a delegate field whose Type might be null in some dumps.
        DiscoverDelegateLayoutFromModules();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Public entry point
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the fast single-pass scan over a streaming entry source (disk-backed index or
    /// plain <c>heap.EnumerateObjects()</c>). The MT index is built lazily on first encounter
    /// of each MethodTable, so no separate type-index pre-pass is needed.
    /// </summary>
    /// <returns>
    ///   <c>true</c> always (the scan may have found 0 leaks — that is not a failure).
    /// </returns>
    public bool Scan(
        IEnumerable<HeapEntry> streamingEntries,
        Dictionary<GroupKey, EventLeakAnalyzer.GroupAccumulator> groupAcc,
        Dictionary<ulong, string> rootHints,
        IReadOnlyList<ClrAppDomain> appDomains,
        HashSet<ulong> processedStaticMTs,
        HashSet<ulong> processedStaticDelegates,
        EventLeakOptions options,
        ref int eventsScanned,
        ref int publisherInstances)
    {
        SinglePassScan(streamingEntries, groupAcc, rootHints, appDomains,
            processedStaticMTs, processedStaticDelegates, options,
            ref eventsScanned, ref publisherInstances);

        return true;
    }

    /// <summary>
    /// Processes a single heap entry, building its MT's delegate-field layout lazily on first
    /// encounter. Used both by <see cref="Scan"/>'s single-pass loop and directly by
    /// <see cref="EventLeakAnalyzer.OnHeapEntry"/> when driven by the shared
    /// <see cref="Pipeline.IHeapIndexScanParticipant"/> dispatcher pass.
    /// </summary>
    public void ScanEntry(
        in HeapEntry entry,
        List<(ulong addr, ulong mt, ulong delegateAddr)> buf,
        Dictionary<GroupKey, EventLeakAnalyzer.GroupAccumulator> groupAcc,
        Dictionary<ulong, string> rootHints,
        IReadOnlyList<ClrAppDomain> appDomains,
        HashSet<ulong> processedStaticMTs,
        HashSet<ulong> processedStaticDelegates,
        EventLeakOptions options,
        ref int eventsScanned,
        ref int publisherInstances)
    {
        ReportProgressInterlocked();
        if (entry.MethodTable == 0) return;

        // Lazily build field layouts on first encounter of this MT.
        if (!_mtIndex.TryGetValue(entry.MethodTable, out DelegateFieldLayout[]? layouts))
        {
            long t0 = Stopwatch.GetTimestamp();
            layouts = BuildFieldLayouts(entry.MethodTable);
            _buildFieldLayoutsTicks += Stopwatch.GetTimestamp() - t0;
            _mtIndex[entry.MethodTable] = layouts;
        }
        if (layouts is null) return;

        long p0 = Stopwatch.GetTimestamp();
        ProcessPublisherEntry(entry, layouts, buf, groupAcc, rootHints, appDomains,
            processedStaticMTs, processedStaticDelegates, options,
            ref eventsScanned, ref publisherInstances);
        _processPublisherEntryTicks += Stopwatch.GetTimestamp() - p0;
    }

    private DelegateFieldLayout[]? BuildFieldLayouts(ulong mt)
    {
        ClrType? type = _heap.GetTypeByMethodTable(mt);
        if (type is null
            || TypeFilterHelper.IsSystemType(type.Name)
            || TypeFilterHelper.IsCompilerGenerated(type.Name))
        {
            return null;
        }

        // PERF: Do NOT call _getEventNames(type) here.
        // _getEventNames iterates type.Methods to find add_/remove_ pairs — O(methods).
        // With 50k+ unique MTs in a large dump, calling it unconditionally burns
        // 30–80 seconds for the ~98% of types that have no delegate fields at all.
        // Instead: defer the call until we confirm the type has at least one delegate field.
        HashSet<string>? eventNames = null;   // null = not yet resolved
        List<DelegateFieldLayout>? layouts = null;

        foreach (ClrInstanceField field in type.Fields)
        {
            // PERF: IsObjectReference is a flag read — no metadata I/O.
            // Skipping value-type fields here avoids calling field.Type (which may need
            // metadata resolution) for the ~70–80% of fields that can never be delegates.
            if (!field.IsObjectReference)
                continue;
            if (!TypeFilterHelper.IsDelegateType(field.Type))
                continue;
            if (TypeFilterHelper.IsCompilerGenerated(field.Name))
                continue;
            if (string.IsNullOrEmpty(field.Name))
                continue;

            // First delegate field found — now worth fetching the event-name set (cached).
            eventNames ??= _getEventNames(type);

            // Accuracy: when the type declares explicit events (add_/remove_ pairs),
            // only accept fields whose name is in the event set.
            // When no events are declared, apply a name-pattern filter instead of all-pass:
            // accepting ALL delegate fields from types with no events produces huge false-positive
            // counts from callback/handler fields that are not leaked events.
            if (eventNames.Count > 0)
            {
                // Exact match OR looks like an explicit backing field (e.g. _myEvent for event MyEvent).
                // IsDelegateType is already confirmed above, so name heuristic is safe here.
                if (!eventNames.Contains(field.Name) && !EventLeakAnalyzer.LooksLikeEventFieldName(field.Name))
                    continue;
            }
            else
            {
                // No declared events — only accept fields whose name looks like a C# event backing field.
                if (!EventLeakAnalyzer.LooksLikeEventFieldName(field.Name))
                    continue;
            }

            // Discover MulticastDelegate internal layout from the first delegate field type.
            if (!_delegateLayoutDiscovered)
                TryDiscoverDelegateLayout(field.Type);

            layouts ??= new List<DelegateFieldLayout>(capacity: 4);
            // FIX: ClrInstanceField.Offset is the INTERIOR offset (relative to the start of
            // object instance data, i.e. AFTER the MT pointer). ClrMD's own GetAddress() does:
            //   address = objRef + Offset + PointerSize   (for interior=false)
            // We must store the ABSOLUTE offset (including the MT header) so that
            //   ReadPointer(entry.Address + layout.Offset)
            // lands on the actual field value, not on the MT pointer or adjacent data.
            layouts.Add(new DelegateFieldLayout(
                offset: field.Offset + _ptrSize,
                fieldName: field.Name,
                publisherTypeName: type.Name ?? StringConstants.UnknownType));
        }

        if (layouts is not null)
            return [.. layouts];

        // No instance delegate fields found.
        // Still check if the type has static delegate fields that look like events.
        // If so, return an EMPTY (non-null) array so that ProcessPublisherEntry is
        // invoked for instances of this type and its static field block fires.
        //
        // PERF: same deferral as the instance-field loop above — do NOT call
        // _getEventNames(type) until we've confirmed a static delegate field candidate
        // exists. This branch is hit for ~98% of unique MTs (the ones with no instance
        // delegate fields), so calling it unconditionally here burned the same
        // 30-80s that the comment above warns about, just via a different path.
        HashSet<string>? staticEventNames = null;
        foreach (ClrStaticField sf in type.StaticFields)
        {
            if (!TypeFilterHelper.IsDelegateType(sf.Type)
                || TypeFilterHelper.IsCompilerGenerated(sf.Name)
                || string.IsNullOrEmpty(sf.Name))
                continue;

            staticEventNames ??= _getEventNames(type);

            if (staticEventNames.Count > 0
                && !staticEventNames.Contains(sf.Name!)
                && !EventLeakAnalyzer.LooksLikeEventFieldName(sf.Name))
                continue;

            if (staticEventNames.Count == 0 && !EventLeakAnalyzer.LooksLikeEventFieldName(sf.Name))
                continue;

            // Found a qualifying static delegate field — return empty (not null) array.
            return [];
        }

        return null;
    }

    /// <summary>
    /// Eagerly resolves the <c>System.Delegate</c> and <c>System.MulticastDelegate</c>
    /// types from loaded modules and discovers their field offsets.
    /// This is more reliable than waiting for a delegate field to appear during the scan,
    /// because <c>ClrInstanceField.Type</c> can be null for unresolved fields in some dumps.
    /// </summary>
    private void DiscoverDelegateLayoutFromModules()
    {
        if (_delegateLayoutDiscovered) return;

        foreach (ClrAppDomain domain in _heap.Runtime.AppDomains)
        {
            foreach (ClrModule module in domain.Modules)
            {
                if (_delegateLayoutDiscovered) return;

                ClrType? delegateBase = module.GetTypeByName("System.Delegate");
                ClrType? multicastDelegate = module.GetTypeByName("System.MulticastDelegate");

                if (delegateBase != null || multicastDelegate != null)
                {
                    TryDiscoverDelegateLayout(multicastDelegate ?? delegateBase);
                    if (_delegateLayoutDiscovered) return;
                }
            }
        }

        // If module walk didn't work, apply the known-good fallback inside TryDiscoverDelegateLayout.
        if (!_delegateLayoutDiscovered)
            TryDiscoverDelegateLayout(null);  // triggers hardcoded fallback
    }

    /// <summary>
    /// Walks up the delegate type's base-type chain to discover the
    /// <c>_target</c> and <c>_invocationList</c> field offsets.
    /// <para>
    /// <b>Key distinction</b>: <c>_target</c> is declared on <c>System.Delegate</c>,
    /// while <c>_invocationList</c> is declared on <c>System.MulticastDelegate</c> —
    /// they live on *different* types in the hierarchy.  Both must be found for a
    /// complete layout; only searching <c>System.MulticastDelegate</c> for <c>_target</c>
    /// returns null and leaves offsets zeroed.
    /// </para>
    /// </summary>
    private void TryDiscoverDelegateLayout(ClrType? delegateType)
    {
        bool targetFound = false;
        bool invListFound = false;

        ClrType? cur = delegateType;
        while (cur != null && !(targetFound && invListFound))
        {
            if (!targetFound && cur.Name == "System.Delegate")
            {
                ClrInstanceField? tf = cur.GetFieldByName("_target");
                if (tf != null)
                {
                    // +_ptrSize: same interior-offset correction as BuildFieldLayouts.
                    _delegateTargetOffset = tf.Offset + _ptrSize;
                    targetFound = true;
                }
            }

            if (!invListFound && cur.Name == "System.MulticastDelegate")
            {
                ClrInstanceField? ilf = cur.GetFieldByName("_invocationList");
                if (ilf != null)
                {
                    _delegateInvListOffset = ilf.Offset + _ptrSize;
                    // _invocationCount is the nint field immediately after _invocationList
                    // (both pointer-sized; they are always declared consecutively in the CLR source).
                    _delegateInvCountOffset = _delegateInvListOffset + _ptrSize;
                    invListFound = true;
                }
            }

            cur = cur.BaseType;
        }

        if (targetFound && invListFound)
        {
            _delegateLayoutDiscovered = true;
            return;
        }

        // Fallback: use known .NET 6+ 64/32-bit layout if ClrMD type inspection failed
        // (e.g. incomplete symbols).  Offsets verified against coreclr source:
        //   System.Delegate:          _target(0) _methodBase(1) _methodPtr(2) _methodPtrAux(3)
        //   System.MulticastDelegate: _invocationList(4) _invocationCount(5)
        //   Absolute = interior + ptrSize, so multiply field-index by ptrSize.
        if (!_delegateLayoutDiscovered)
        {
            _delegateTargetOffset = _ptrSize;           // field 0 → absolute 8 (64-bit)
            _delegateInvListOffset = _ptrSize * 5;       // field 4 → absolute 40
            _delegateInvCountOffset = _ptrSize * 6;       // field 5 → absolute 48
            _delegateLayoutDiscovered = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Single-pass scan (disk-backed index or heap.EnumerateObjects)
    // Builds MT index lazily on first encounter; no second pass needed.
    // ──────────────────────────────────────────────────────────────────────────────

    private void SinglePassScan(
        IEnumerable<HeapEntry> entries,
        Dictionary<GroupKey, EventLeakAnalyzer.GroupAccumulator> groupAcc,
        Dictionary<ulong, string> rootHints,
        IReadOnlyList<ClrAppDomain> appDomains,
        HashSet<ulong> processedStaticMTs,
        HashSet<ulong> processedStaticDelegates,
        EventLeakOptions options,
        ref int eventsScanned,
        ref int publisherInstances)
    {
        var buf = new List<(ulong addr, ulong mt, ulong delegateAddr)>(capacity: 64);
        foreach (HeapEntry entry in entries)
        {
            ScanEntry(in entry, buf, groupAcc, rootHints, appDomains,
                processedStaticMTs, processedStaticDelegates, options,
                ref eventsScanned, ref publisherInstances);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Per-object processing helpers
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Processes one publisher object entry: instance delegate fields + static fields.
    /// Called from <see cref="ScanEntry"/> for every entry with a resolved layout.
    /// </summary>
    private void ProcessPublisherEntry(
        HeapEntry entry,
        DelegateFieldLayout[] layouts,
        List<(ulong addr, ulong mt, ulong delegateAddr)> buf,
        Dictionary<GroupKey, EventLeakAnalyzer.GroupAccumulator> groupAcc,
        Dictionary<ulong, string> rootHints,
        IReadOnlyList<ClrAppDomain> appDomains,
        HashSet<ulong> processedStaticMTs,
        HashSet<ulong> processedStaticDelegates,
        EventLeakOptions options,
        ref int eventsScanned,
        ref int publisherInstances)
    {
        bool hadField = ProcessInstanceFields(
            entry, layouts, buf, groupAcc, rootHints, options,
            ref eventsScanned);

        // ── Static fields (once per unique MethodTable) ──────────────────────────
        if (processedStaticMTs.Add(entry.MethodTable))
        {
            ClrType? type = _heap.GetTypeByMethodTable(entry.MethodTable);
            if (type != null)
            {
                HashSet<string> eventNames = _getEventNames(type);
                int minSubs = options.MinSubscribers;
                bool includeNonLeaking = options.IncludeNonLeakingEvents;

                foreach (ClrStaticField sField in type.StaticFields)
                {
                    if (!TypeFilterHelper.IsDelegateType(sField.Type)
                        || TypeFilterHelper.IsCompilerGenerated(sField.Name)
                        || string.IsNullOrEmpty(sField.Name)) continue;

                    if (eventNames.Count > 0
                        && !eventNames.Contains(sField.Name!)
                        && !EventLeakAnalyzer.LooksLikeEventFieldName(sField.Name)) continue;

                    eventsScanned++;
                    hadField = true;

                    List<SubscriberInfo> subs = EventLeakAnalyzer.GetStaticEventSubscribers(
                        _heap, sField, appDomains, processedStaticDelegates);

                    if (subs.Count == 0) continue;
                    if (!includeNonLeaking && subs.Count < minSubs) continue;

                    bool mismatch = CheckLifetimeMismatchDirect(subs, options);
                    EventLeakInfo leak = EventLeakAnalyzer.CreateLeakInfo(
                        publisherAddress: 0,
                        publisherType: type.Name ?? StringConstants.UnknownType,
                        eventFieldName: sField.Name!,
                        isStatic: true,
                            subs, rootHints, options, heap: _heap,
                        publisherGeneration: 2,
                        hasLifetimeMismatch: mismatch);

                    if (EventLeakAnalyzer.IsLikelyPublisher(leak, options))
                        EventLeakAnalyzer.AddToAccumulator(
                            groupAcc, leak, options.TopDetailedInstancesPerGroup);
                }
            }
        }

        if (hadField) publisherInstances++;
    }

    /// <summary>
    /// Processes instance delegate fields for one publisher entry.
    /// Returns true if any event field was found (regardless of subscriber count).
    /// </summary>
    private bool ProcessInstanceFields(
        HeapEntry entry,
        DelegateFieldLayout[] layouts,
        List<(ulong addr, ulong mt, ulong delegateAddr)> buf,
        Dictionary<GroupKey, EventLeakAnalyzer.GroupAccumulator> groupAcc,
        Dictionary<ulong, string> rootHints,
        EventLeakOptions options,
        ref int eventsScanned)
    {
        bool hadField = false;
        int minSubs = options.MinSubscribers;
        bool includeNonLeaking = options.IncludeNonLeakingEvents;
        // entry is the publisher itself, so its generation is already known from the
        // Phase 1 disk-backed index — no segment lookup needed here at all.
        int publisherGen = entry.Generation;

        foreach (ref readonly DelegateFieldLayout layout in layouts.AsSpan())
        {
            eventsScanned++;
            ReportProgressInterlocked();
            hadField = true;

            // One ReadPointer: reveals both null-ness and delegate address.
            // Null events skip with a single 8-byte read — no GetObject, no segment lookup.
            ulong delegateAddr;
            if (!_reader.ReadPointer(entry.Address + (ulong)layout.Offset, out delegateAddr)
                || delegateAddr == 0)
                continue;

            buf.Clear();
            ExtractSubscribersDirect(delegateAddr, buf);
            if (buf.Count == 0) continue;
            if (!includeNonLeaking && buf.Count < minSubs) continue;

            // Filter noise/compiler-generated publisher types but do NOT require Gen2.
            // Gen2 is a useful severity signal but excluding Gen0/Gen1 publishers removes a
            // large fraction of real subscriber counts from the total.
            if (TypeFilterHelper.IsCompilerGenerated(layout.PublisherTypeName)
                || EventLeakAnalyzer.IsNoiseTypeName(layout.PublisherTypeName))
                continue;
            if (buf.Count < options.PublisherSubscriberThreshold) continue;

            List<SubscriberInfo> subscribers = ResolveSubscriberTypes(buf);

            bool mismatch = CheckLifetimeMismatchDirect(subscribers, options);

            EventLeakInfo leak = EventLeakAnalyzer.CreateLeakInfo(
                publisherAddress: entry.Address,
                publisherType: layout.PublisherTypeName,
                eventFieldName: layout.FieldName,
                isStatic: false,
                    subscribers,
                    rootHints,
                    options, heap: null,   // skip low-incoming-refs heap scan on hot path
                    publisherGeneration: publisherGen,
                    hasLifetimeMismatch: mismatch);

            // IsLikelyPublisher: subscriber count and gen already validated above.
            // Still call to honour any future threshold changes.
            EventLeakAnalyzer.AddToAccumulator(groupAcc, leak, options.TopDetailedInstancesPerGroup);
        }

        // NOTE: publisherInstances is intentionally NOT incremented here.
        // ProcessPublisherEntry (the caller on sequential paths) owns the increment
        // to avoid double-counting.
        return hadField;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Direct subscriber extraction (no ClrObject, no heap.GetObject)
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts subscriber (address, methodTable) pairs from a delegate object
    /// entirely via <see cref="IMemoryReader.ReadPointer"/> calls.
    /// Uses pre-discovered <c>_target</c> / <c>_invocationList</c> offsets —
    /// no ClrType lookups, no <c>ClrObject</c> allocations.
    /// </summary>
    private void ExtractSubscribersDirect(ulong delegateAddr, List<(ulong addr, ulong mt, ulong delegateAddr)> results)
    {
        // Read _invocationList to distinguish single vs multicast delegate.
        ulong invListAddr;
        if (!_reader.ReadPointer(delegateAddr + (ulong)_delegateInvListOffset, out invListAddr))
            return;

        if (invListAddr == 0)
        {
            // Single subscriber: read _target.
            ExtractSingleTargetDirect(delegateAddr, results);
            return;
        }

        // Multicast: invListAddr points to a Delegate[] array containing per-subscriber delegates.
        // Use ClrMD's GetObject to validate the array and get its authoritative Length.
        // Raw MT-lookup via GetTypeByMethodTable is unreliable here — the type may not yet be
        // in ClrMD's cache, causing IsArray to return false and silently collapsing every
        // multicast event to a single-target read (1 subscriber per publisher instead of N).
        ClrObject invListObj = _heap.GetObject(invListAddr);
        if (!invListObj.IsValid || !invListObj.IsArray)
        {
            // Not an array — fall back to single-target (covers chained-delegate edge cases).
            ExtractSingleTargetDirect(delegateAddr, results);
            return;
        }

        ClrArray arr = invListObj.AsArray();
        int invCount = arr.Length;
        if (invCount == 0 || invCount > 1_000_000) // sanity cap
            return;

        // Iterate the Delegate[] using ClrMD's array accessors — same approach as the
        // ClrMD path in GetDelegateTargets, so behaviour is identical for both paths.
        for (int i = 0; i < invCount; i++)
        {
            ClrObject elem = arr.GetObjectValue(i);
            if (elem.IsValid && elem.Address != 0)
                ExtractSingleTargetDirect(elem.Address, results);
        }
    }

    private void ExtractSingleTargetDirect(ulong delegateAddr, List<(ulong addr, ulong mt, ulong delegateAddr)> results)
    {
        ulong targetAddr;
        if (_reader.ReadPointer(delegateAddr + (ulong)_delegateTargetOffset, out targetAddr)
            && targetAddr != 0)
        {
            // Instance method handler: _target is the subscriber object.
            ulong targetMt;
            if (_reader.ReadPointer(targetAddr, out targetMt) && targetMt != 0)
                results.Add((targetAddr, targetMt, delegateAddr));
        }
        else
        {
            // Static method handler: use the delegate object itself as the token
            // (matches existing behaviour in ExtractSingleSubscriber).
            ulong delegateMt;
            if (_reader.ReadPointer(delegateAddr, out delegateMt) && delegateMt != 0)
                results.Add((delegateAddr, delegateMt, delegateAddr));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Deferred type name resolution
    // ──────────────────────────────────────────────────────────────────────────────

    private List<SubscriberInfo> ResolveSubscriberTypes(List<(ulong addr, ulong mt, ulong delegateAddr)> raw)
    {
        var result = new List<SubscriberInfo>(raw.Count);
        foreach ((ulong addr, ulong mt, ulong daddr) in raw)
        {
            // Use cached type name when possible
            if (!_subscriberTypeNames.TryGetValue(mt, out string? name))
            {
                name = _heap.GetTypeByMethodTable(mt)?.Name ?? StringConstants.UnknownType;
                _subscriberTypeNames[mt] = name;
            }

            string? methodName = null;
            try
            {
                if (daddr != 0)
                {
                    ClrObject delObj = _heap.GetObject(daddr);
                    if (delObj.IsValid && delObj.Type != null)
                    {
                        var runtime = _heap.Runtime;

                        // Try _methodPtr
                        ClrInstanceField? f = null;
                        ClrType? cur = delObj.Type;
                        while (cur != null && f == null)
                        {
                            f = cur.GetFieldByName("_methodPtr");
                            cur = cur.BaseType;
                        }
                        if (f != null)
                        {
                            try
                            {
                                ulong ptr = (ulong)f.Read<IntPtr>(delObj, interior: false);
                                if (ptr != 0)
                                {
                                    if (!_methodNameByInstructionPointer.TryGetValue(ptr, out methodName))
                                    {
                                        var m = runtime.GetMethodByInstructionPointer(ptr);
                                        methodName = m != null ? (m.Signature ?? m.Name) : null;
                                        _methodNameByInstructionPointer[ptr] = methodName;
                                    }
                                }
                            }
                            catch { }
                        }

                        // Try _methodPtrAux
                        if (methodName == null)
                        {
                            cur = delObj.Type;
                            ClrInstanceField? faux = null;
                            while (cur != null && faux == null)
                            {
                                faux = cur.GetFieldByName("_methodPtrAux");
                                cur = cur.BaseType;
                            }
                            if (faux != null)
                            {
                                try
                                {
                                    ulong aux = (ulong)faux.Read<IntPtr>(delObj, interior: false);
                                    if (aux != 0)
                                    {
                                        if (!_methodNameByInstructionPointer.TryGetValue(aux, out methodName))
                                        {
                                            var m2 = runtime.GetMethodByInstructionPointer(aux);
                                            methodName = m2 != null ? (m2.Signature ?? m2.Name) : null;
                                            _methodNameByInstructionPointer[aux] = methodName;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }

                        // Try _methodBase wrapper for pointer-like fields
                        if (methodName == null)
                        {
                            cur = delObj.Type;
                            ClrInstanceField? fbase = null;
                            while (cur != null && fbase == null)
                            {
                                fbase = cur.GetFieldByName("_methodBase");
                                cur = cur.BaseType;
                            }
                            if (fbase != null)
                            {
                                try
                                {
                                    ClrObject mb = fbase.ReadObject(delObj, interior: false);
                                    if (mb.IsValid && mb.Type != null)
                                    {
                                        foreach (var field in mb.Type.Fields)
                                        {
                                            try
                                            {
                                                if (field.ElementType == ClrElementType.NativeInt || field.ElementType == ClrElementType.Int64 || field.ElementType == ClrElementType.UInt64)
                                                {
                                                    ulong val = 0;
                                                    try { val = (ulong)field.Read<IntPtr>(mb, interior: false); }
                                                    catch { continue; }
                                                    if (val == 0) continue;
                                                    if (!_methodNameByInstructionPointer.TryGetValue(val, out string? m3Name))
                                                    {
                                                        var m3 = runtime.GetMethodByInstructionPointer(val);
                                                        m3Name = m3 != null ? (m3.Signature ?? m3.Name) : null;
                                                        _methodNameByInstructionPointer[val] = m3Name;
                                                    }
                                                    if (m3Name != null)
                                                    {
                                                        methodName = m3Name;
                                                        break;
                                                    }
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { /* swallow */ }

            result.Add(new SubscriberInfo { Address = addr, Type = name, MethodName = methodName });
        }
        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Heuristic helpers (direct-read versions, no ClrMD heap objects needed)
    // ──────────────────────────────────────────────────────────────────────────────

    private int GetObjectGenerationDirect(ulong address)
    {
        if (address == 0) return -1;
        ClrSegment? seg = _heap.GetSegmentByAddress(address);
        if (seg is null) return -1;
        try { return (int)seg.GetGeneration(address); }
        catch { return -1; }
    }

    private void ReportProgressInterlocked(string? detail = null)
    {
        if (_progress is null) return;
        long newCount = Interlocked.Increment(ref _scannedObjects);
        // Report by count or by elapsed time.
        if (newCount % _reportEveryObjects == 0)
        {
            _progress.Report(new AnalyzerProgressReport((int)newCount, "scanning event handlers", detail ?? $"{newCount:N0} objects", _reporterStopwatch.Elapsed));
            Interlocked.Exchange(ref _lastReportMs, _reporterStopwatch.ElapsedMilliseconds);
            return;
        }

        long lastMs = Interlocked.Read(ref _lastReportMs);
        if (_reporterStopwatch.ElapsedMilliseconds - lastMs >= 2000)
        {
            _progress.Report(new AnalyzerProgressReport((int)newCount, "scanning event handlers", detail ?? $"{newCount:N0} objects", _reporterStopwatch.Elapsed));
            Interlocked.Exchange(ref _lastReportMs, _reporterStopwatch.ElapsedMilliseconds);
        }
    }

    private bool CheckLifetimeMismatchDirect(List<SubscriberInfo> subscribers, EventLeakOptions options)
    {
        if (subscribers.Count == 0) return false;
        int probeLimit = Math.Min(subscribers.Count, options.LifetimeMismatchProbeLimit);
        int gen01Count = 0;
        int probed = 0;
        for (int i = 0; i < subscribers.Count && probed < probeLimit; i++)
        {
            ulong addr = subscribers[i].Address;
            if (addr == 0 || subscribers[i].Type == StringConstants.StaticMethodSubscriber) continue;
            int gen = GetObjectGenerationDirect(addr);
            if (gen is 0 or 1) gen01Count++;
            probed++;
        }
        if (probed == 0) return false;
        return (double)gen01Count / probed >= options.LifetimeMismatchGen01Threshold;
    }
}
