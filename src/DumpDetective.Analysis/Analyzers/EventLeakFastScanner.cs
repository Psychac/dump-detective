using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers.EventLeak;
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
/// High-performance heap-scan driver for event-leak detection.
///
/// Per-object hot path:
///   For each heap entry, look up its MethodTable in the pre-built <see cref="PublisherRegistry"/>
///   (design §3 — the registry's single eager module walk replaced this class's former lazy
///   per-unique-MT metadata walk). If descriptors are found, read each instance delegate field via
///   <see cref="IMemoryReader.ReadPointer"/> at its pre-computed offset — no <c>ClrObject</c>
///   construction, no MethodTable re-read, no ClrMD overhead.
///   • A null pointer means no subscribers → 1 read, skip.
///   • If non-zero, follow the <c>MulticastDelegate._invocationList</c> / <c>._target</c> chain
///     using direct reads (2–5 reads for a typical leaking event).
///   Subscriber type names are resolved lazily from a small MT→Name table.
/// </summary>
internal sealed class EventLeakFastScanner
{
    // ──────────────────────────────────────────────────────────────────────────────
    // Core state
    // ──────────────────────────────────────────────────────────────────────────────
    private readonly ClrHeap _heap;
    private readonly PublisherRegistry _registry;
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

    /// <summary>Subscriber MT → resolved type name (deferred to keep the hot scan free of ClrMD calls).</summary>
    private readonly Dictionary<ulong, string> _subscriberTypeNames = new(capacity: 512);

    /// <summary>
    /// Instruction pointer → resolved method name. Leaked events typically have the same handler
    /// method subscribed many times over (one handler, many publisher instances), so without this
    /// cache <see cref="ResolveSubscriberTypes"/> repeats the same expensive
    /// <see cref="ClrRuntime.GetMethodByInstructionPointer"/> DAC symbol lookup for the same IP.
    /// </summary>
    private readonly Dictionary<ulong, string?> _methodNameByInstructionPointer = new(capacity: 512);

    // PERF INVESTIGATION (temporary): accumulated ticks for the per-object hot path. The
    // once-per-unique-MT metadata walk now happens eagerly in PublisherRegistry.Build, before
    // this scanner runs at all — see EventLeakAnalyzer's own timing around that call.
    private long _processPublisherEntryTicks;

    internal double GetScanTimings() => _processPublisherEntryTicks * 1000.0 / Stopwatch.Frequency;

    // ──────────────────────────────────────────────────────────────────────────────
    // Construction
    // ──────────────────────────────────────────────────────────────────────────────

    public EventLeakFastScanner(ClrHeap heap, PublisherRegistry registry, IProgress<AnalyzerProgressReport>? progress = null)
    {
        _heap = heap;
        _registry = registry;

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
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Public entry point
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the fast single-pass scan over a streaming entry source (disk-backed index or
    /// plain <c>heap.EnumerateObjects()</c>).
    /// </summary>
    /// <returns>
    ///   <c>true</c> always (the scan may have found 0 leaks — that is not a failure).
    /// </returns>
    public bool Scan(
        IEnumerable<HeapEntry> streamingEntries,
        Dictionary<GroupKey, EventLeakAnalyzer.GroupAccumulator> groupAcc,
        Dictionary<ulong, string> rootHints,
        EventLeakOptions options,
        HashSet<ulong> leakingMTs,
        ref int eventsScanned,
        ref int publisherInstances)
    {
        SinglePassScan(streamingEntries, groupAcc, rootHints, options, leakingMTs,
            ref eventsScanned, ref publisherInstances);

        return true;
    }

    /// <summary>
    /// Processes a single heap entry using descriptors pre-resolved by
    /// <see cref="PublisherRegistry"/>. Used both by <see cref="Scan"/>'s single-pass loop and
    /// directly by <see cref="EventLeakAnalyzer.OnHeapEntry"/> when driven by the shared
    /// <see cref="Pipeline.IHeapIndexScanParticipant"/> dispatcher pass.
    /// </summary>
    public void ScanEntry(
        in HeapEntry entry,
        List<(ulong addr, ulong mt, ulong delegateAddr)> buf,
        Dictionary<GroupKey, EventLeakAnalyzer.GroupAccumulator> groupAcc,
        Dictionary<ulong, string> rootHints,
        EventLeakOptions options,
        HashSet<ulong> leakingMTs,
        ref int eventsScanned,
        ref int publisherInstances)
    {
        ReportProgressInterlocked();
        if (entry.MethodTable == 0) return;

        if (!_registry.TryGetDescriptors(entry.MethodTable, out EventFieldDescriptor[]? descriptors) || descriptors is null)
            return;

        long p0 = Stopwatch.GetTimestamp();
        ProcessPublisherEntry(entry, descriptors, buf, groupAcc, rootHints, options, leakingMTs,
            ref eventsScanned, ref publisherInstances);
        _processPublisherEntryTicks += Stopwatch.GetTimestamp() - p0;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Single-pass scan (disk-backed index or heap.EnumerateObjects)
    // ──────────────────────────────────────────────────────────────────────────────

    private void SinglePassScan(
        IEnumerable<HeapEntry> entries,
        Dictionary<GroupKey, EventLeakAnalyzer.GroupAccumulator> groupAcc,
        Dictionary<ulong, string> rootHints,
        EventLeakOptions options,
        HashSet<ulong> leakingMTs,
        ref int eventsScanned,
        ref int publisherInstances)
    {
        var buf = new List<(ulong addr, ulong mt, ulong delegateAddr)>(capacity: 64);
        foreach (HeapEntry entry in entries)
        {
            ScanEntry(in entry, buf, groupAcc, rootHints, options, leakingMTs,
                ref eventsScanned, ref publisherInstances);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Per-object processing helpers
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Processes one publisher object entry: instance delegate fields + static fields.
    /// Called from <see cref="ScanEntry"/> for every entry with resolved descriptors.
    /// </summary>
    private void ProcessPublisherEntry(
        HeapEntry entry,
        EventFieldDescriptor[] descriptors,
        List<(ulong addr, ulong mt, ulong delegateAddr)> buf,
        Dictionary<GroupKey, EventLeakAnalyzer.GroupAccumulator> groupAcc,
        Dictionary<ulong, string> rootHints,
        EventLeakOptions options,
        HashSet<ulong> leakingMTs,
        ref int eventsScanned,
        ref int publisherInstances)
    {
        // Statics no longer run on the hot path (design §6) — EventLeakAnalyzer.SweepRegistryStatics
        // is now the single place static delegate fields are read, once per MT in
        // PublisherRegistry.StaticPublisherMTs, after the scan completes.
        bool hadField = ProcessInstanceFields(
            entry, descriptors, buf, groupAcc, rootHints, options, leakingMTs,
            ref eventsScanned);

        if (hadField) publisherInstances++;
    }

    /// <summary>
    /// Processes instance delegate fields for one publisher entry.
    /// Returns true if any event field was found (regardless of subscriber count).
    /// </summary>
    private bool ProcessInstanceFields(
        HeapEntry entry,
        EventFieldDescriptor[] descriptors,
        List<(ulong addr, ulong mt, ulong delegateAddr)> buf,
        Dictionary<GroupKey, EventLeakAnalyzer.GroupAccumulator> groupAcc,
        Dictionary<ulong, string> rootHints,
        EventLeakOptions options,
        HashSet<ulong> leakingMTs,
        ref int eventsScanned)
    {
        bool hadField = false;
        // entry is the publisher itself, so its generation is already known from the
        // Phase 1 disk-backed index — no segment lookup needed here at all.
        int publisherGen = entry.Generation;

        foreach (ref readonly EventFieldDescriptor descriptor in descriptors.AsSpan())
        {
            if (descriptor.IsStatic) continue;

            eventsScanned++;
            ReportProgressInterlocked();
            hadField = true;

            // One ReadPointer: reveals both null-ness and delegate address.
            // Null events skip with a single 8-byte read — no GetObject, no segment lookup.
            ulong delegateAddr;
            if (!_reader.ReadPointer(entry.Address + (ulong)descriptor.Offset, out delegateAddr)
                || delegateAddr == 0)
                continue;

            buf.Clear();
            // P1-3 (docs/analysis/phase1/eventleak-analyzer-audit.md): call the shared
            // DelegateChainWalker directly instead of maintaining a second, hand-copied
            // implementation of the same pointer chase — DelegateChainWalker.ExtractSubscribers
            // is a plain static method, not a virtual IPublisherShape.Extract call, so nothing
            // about the hot-path/virtual-dispatch rationale in DelegateChainWalker's own doc
            // comment applied to keeping a duplicate here.
            DelegateChainWalker.ExtractSubscribers(_heap, _reader, delegateAddr, _registry.DelegateTargetOffset, _registry.DelegateInvocationListOffset, buf);
            if (buf.Count == 0) continue;

            // Filter noise/compiler-generated publisher types but do NOT require Gen2.
            // Gen2 is a useful severity signal but excluding Gen0/Gen1 publishers removes a
            // large fraction of real subscriber counts from the total.
            if (TypeFilterHelper.IsCompilerGenerated(descriptor.PublisherTypeName)
                || EventLeakAnalyzer.IsNoiseTypeName(descriptor.PublisherTypeName))
                continue;
            if (buf.Count < options.PublisherSubscriberThreshold) continue;

            List<SubscriberInfo> subscribers = ResolveSubscriberTypes(buf);

            bool mismatch = CheckLifetimeMismatchDirect(subscribers, options);

            EventLeakInfo leak = EventLeakAnalyzer.CreateLeakInfo(
                publisherAddress: entry.Address,
                publisherType: descriptor.PublisherTypeName,
                eventFieldName: descriptor.FieldName,
                isStatic: false,
                    subscribers,
                    rootHints,
                    // heap is passed so IsDisposedButSubscribed can resolve via the MT-cached
                    // interface check; the (per-object, unbounded) low-incoming-refs heap scan
                    // stays opt-in only and is skipped here regardless (EnableLowIncomingRefsCheck).
                    options, heap: _heap,
                    publisherGeneration: publisherGen,
                    hasLifetimeMismatch: mismatch,
                    disposableTypeCache: _registry.DisposableTypeCache,
                    publisherMethodTable: entry.MethodTable);

            // IsLikelyPublisher: subscriber count and gen already validated above.
            // Still call to honour any future threshold changes.
            EventLeakAnalyzer.AddToAccumulator(groupAcc, leak, options.TopDetailedInstancesPerGroup, leakingMTs);
        }

        // NOTE: publisherInstances is intentionally NOT incremented here.
        // ProcessPublisherEntry (the caller on sequential paths) owns the increment
        // to avoid double-counting.
        return hadField;
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

            result.Add(new SubscriberInfo { Address = addr, MethodTable = mt, Type = name, MethodName = methodName });
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

    // §9.19 (docs/refactor/analysis-profile-removal-plan.md): probes every subscriber, not a
    // capped sample — each generation lookup is an O(1) segment lookup, cheap regardless of scale.
    private bool CheckLifetimeMismatchDirect(List<SubscriberInfo> subscribers, EventLeakOptions options)
    {
        if (subscribers.Count == 0) return false;
        int gen01Count = 0;
        int probed = 0;
        for (int i = 0; i < subscribers.Count; i++)
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
