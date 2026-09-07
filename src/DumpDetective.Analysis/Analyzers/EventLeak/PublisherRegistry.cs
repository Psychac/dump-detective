using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Analyzers.EventLeak;

/// <summary>
/// Single eager pass that replaces the two independent type-metadata walks that used to run
/// per-analysis: <c>EventLeakFastScanner.BuildFieldLayouts</c> (lazy, per-unique-MT, during the
/// heap scan) and <c>EventLeakAnalyzer.SweepModuleStaticFields</c>'s own module walk (design
/// §3). Built once per analysis and shared by both consumers via <see cref="TryGetDescriptors"/>.
/// Phase 3 registers exactly one shape (<see cref="FieldBackedDelegateShape"/>) — the
/// <see cref="IPublisherShape"/> seam exists so future shapes (event-handler-list, weak-event)
/// can be added without a second type walk.
///
/// Split into two scopes matching each original walk (lever 3, design §3.3): the expensive
/// instance-field walk (touches every field's <c>ClrType</c> to check for a delegate base type)
/// only runs for MethodTables with a live heap instance, same scope
/// <c>BuildFieldLayouts</c> had. The static-field walk still covers every type reachable from
/// loaded modules — matching <c>SweepModuleStaticFields</c>'s scope — since a static-only
/// publisher can exist with zero live instances. Running the instance-field walk over every
/// module type (not just live ones) regressed this build from the ~22.8s combined baseline to
/// ~48s alone on the 3.3GB reference dump; do not reopen that by merging the two scopes.
/// </summary>
internal sealed class PublisherRegistry
{
    private readonly Dictionary<ulong, EventFieldDescriptor[]> _descriptorsByMt;

    public EventNameResolver EventNames { get; }

    /// <summary>Subscriber MT → implements-IDisposable, resolved once per unique MT (design §9). Migrated from the former per-scanner cache so it is shared across the whole analysis.</summary>
    public Dictionary<ulong, bool> DisposableTypeCache { get; } = new(capacity: 512);

    /// <summary>
    /// MethodTables that produced at least one static descriptor during Pass 1 (design §6).
    /// Drives the single post-scan static sweep — statics are no longer processed on
    /// <see cref="EventLeakFastScanner"/>'s hot path at all, so this is the only place they run,
    /// closing the double-count bug where a type with both heap instances and a static event
    /// field was accumulated once by the hot path and again by the old module walk.
    /// </summary>
    public IReadOnlyCollection<ulong> StaticPublisherMTs { get; }

    public int DelegateTargetOffset { get; }
    public int DelegateInvocationListOffset { get; }
    public int DelegateInvocationCountOffset { get; }

    /// <summary>
    /// Distinct MethodTables recognized as candidate publishers (instance and/or static
    /// descriptor(s) present) — the denominator for "types scanned, zero leaking" (P2-2,
    /// docs/analysis/phase1/eventleak-analyzer-audit.md). Every leak's <c>PublisherMethodTable</c>
    /// is guaranteed to be one of these keys, since leaks only ever come from a descriptor this
    /// registry produced.
    /// </summary>
    public int CandidatePublisherCount => _descriptorsByMt.Count;

    private PublisherRegistry(
        Dictionary<ulong, EventFieldDescriptor[]> descriptorsByMt,
        EventNameResolver eventNames,
        (int TargetOffset, int InvListOffset, int InvCountOffset) delegateOffsets,
        IReadOnlyCollection<ulong> staticPublisherMTs)
    {
        _descriptorsByMt = descriptorsByMt;
        EventNames = eventNames;
        DelegateTargetOffset = delegateOffsets.TargetOffset;
        DelegateInvocationListOffset = delegateOffsets.InvListOffset;
        DelegateInvocationCountOffset = delegateOffsets.InvCountOffset;
        StaticPublisherMTs = staticPublisherMTs;
    }

    // P1-1 (docs/analysis/phase1/eventleak-analyzer-audit.md): this build is the single most
    // expensive phase at scale (~121.6s / 57% of FindEventLeaks wall time on the 25.6GB reference
    // dump per docs/analysis/phase1-redesigns/event-leak-analyzer.md §0.1) and previously had no
    // cancellation support at all. Checked every 8192 iterations (bitmask, matches
    // SweepRegistryStatics's convention) rather than every iteration to keep the check off the
    // hot per-type/per-object path.
    private const int CancellationCheckMask = 8191;

    // Q2 measurement pass (docs/cache/cache-ideal-design.md §10): set DD_PERF_EVENTLEAK_REGISTRY=1
    // to split this build's wall clock across its three passes, answering whether the cost is scan
    // volume (Pass 2a walks every indexed object solely to collect the distinct-MethodTable set) or
    // per-type work (Passes 1 and 2b). Also reports the size of that set, which is what a
    // replacement would have to reproduce.
    private static readonly bool PerfLogRegistryBuild =
        Environment.GetEnvironmentVariable("DD_PERF_EVENTLEAK_REGISTRY") == "1";

    public static PublisherRegistry Build(
        ClrHeap heap, IHeapAnalysisCache? cache, IReadOnlyList<IPublisherShape>? shapes = null,
        CancellationToken cancellationToken = default)
    {
        var perfSw = PerfLogRegistryBuild ? System.Diagnostics.Stopwatch.StartNew() : null;
        System.TimeSpan perfPass1 = default, perfPass2a = default;

        var eventNames = new EventNameResolver();
        var delegateOffsets = DelegateLayoutDiscovery.Discover(heap);

        shapes ??= new IPublisherShape[] { new FieldBackedDelegateShape(heap, eventNames) };

        var descriptorsByMt = new Dictionary<ulong, EventFieldDescriptor[]>(capacity: 8192);
        var mtToType = new Dictionary<ulong, ClrType>(capacity: 16384);
        var staticPublisherMTs = new HashSet<ulong>(capacity: 1024);
        List<EventFieldDescriptor>? buf = null;
        int typesVisited = 0;

        // Pass 1: every type reachable from loaded modules, static fields only (matches
        // SweepModuleStaticFields's scope — static publishers can exist with zero instances).
        foreach (ClrAppDomain domain in heap.Runtime.AppDomains)
        {
            foreach (ClrModule module in domain.Modules)
            {
                foreach (var pair in module.EnumerateTypeDefToMethodTableMap())
                {
                    if ((typesVisited++ & CancellationCheckMask) == 0)
                        cancellationToken.ThrowIfCancellationRequested();

                    ulong mt = pair.MethodTable;
                    if (mt == 0 || mtToType.ContainsKey(mt))
                        continue;

                    ClrType? type = heap.GetTypeByMethodTable(mt);
                    if (type is null
                        || TypeFilterHelper.IsSystemType(type.Name)
                        || TypeFilterHelper.IsCompilerGenerated(type.Name))
                        continue;

                    mtToType[mt] = type;

                    buf?.Clear();
                    for (int s = 0; s < shapes.Count; s++)
                    {
                        foreach (EventFieldDescriptor descriptor in shapes[s].DescribeStaticFields(type))
                        {
                            buf ??= new List<EventFieldDescriptor>(capacity: 4);
                            buf.Add(descriptor);
                        }
                    }

                    if (buf is { Count: > 0 })
                    {
                        descriptorsByMt[mt] = [.. buf];
                        staticPublisherMTs.Add(mt);
                    }
                }
            }
        }

        if (perfSw is not null) perfPass1 = perfSw.Elapsed;

        // Pass 2: only MethodTables with a live heap instance, instance fields only (matches
        // BuildFieldLayouts's scope — the expensive per-field ClrType resolution here must not
        // run over every module type).
        var liveMts = new HashSet<ulong>(capacity: 16384);
        int objectsVisited = 0;
        // P2-1 (docs/analysis/phase1/eventleak-analyzer-audit.md): must match FindEventLeaks's
        // own check for choosing between cache.EnumerateIndexedEntries() and a raw heap walk
        // (EventLeakAnalyzer.cs). "cache is not null" alone is not sufficient — a real cache can
        // exist without a disk index having been built yet, and EnumerateIndexedEntriesAsTuples
        // silently yields nothing in that case (HeapIndexCache.EnumerateIndexedEntries: `if
        // (_heapIndex is null) yield break;`) rather than throwing or falling back. Using the
        // looser check here previously meant Pass 2 could silently produce an empty liveMts set
        // — zero instance-field descriptors — for the exact FindEventLeaks fallback branch that
        // then goes on to do a real full heap scan expecting real descriptors.
        // The distinct-MethodTable set is already persisted as ObjectTypeDictionary, so deriving it
        // by enumerating every object read 166.1 MiB to produce 0.09 MiB on the 27.5 GB dump — a
        // 1,760x amplification costing 5.28 s of this build's 105.66 s
        // (docs/cache/cache-ideal-design.md §7.7, O8). The enumeration below stays as the fallback
        // for a run with no disk index.
        IReadOnlyList<ulong>? distinctMethodTables = cache?.TryGetDistinctMethodTables();
        if (distinctMethodTables is not null)
        {
            foreach (ulong methodTable in distinctMethodTables)
                liveMts.Add(methodTable);
        }
        else if (cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out _))
        {
            foreach ((ulong _, ulong methodTable, ulong _) in cache.EnumerateIndexedEntriesAsTuples())
            {
                if ((objectsVisited++ & CancellationCheckMask) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                liveMts.Add(methodTable);
            }
        }
        else
        {
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if ((objectsVisited++ & CancellationCheckMask) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (obj.IsValid && obj.Type is not null)
                    liveMts.Add(obj.Type.MethodTable);
            }
        }

        if (perfSw is not null) perfPass2a = perfSw.Elapsed - perfPass1;

        int liveMtsVisited = 0;
        foreach (ulong mt in liveMts)
        {
            if ((liveMtsVisited++ & CancellationCheckMask) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            if (!mtToType.TryGetValue(mt, out ClrType? type))
                continue;

            buf?.Clear();
            for (int s = 0; s < shapes.Count; s++)
            {
                foreach (EventFieldDescriptor descriptor in shapes[s].DescribeInstanceFields(type))
                {
                    buf ??= new List<EventFieldDescriptor>(capacity: 4);
                    buf.Add(descriptor);
                }
            }

            if (buf is { Count: > 0 })
            {
                if (descriptorsByMt.TryGetValue(mt, out EventFieldDescriptor[]? existing))
                {
                    var merged = new EventFieldDescriptor[existing.Length + buf.Count];
                    existing.CopyTo(merged, 0);
                    buf.CopyTo(merged, existing.Length);
                    descriptorsByMt[mt] = merged;
                }
                else
                {
                    descriptorsByMt[mt] = [.. buf];
                }
            }
        }

        if (perfSw is not null)
        {
            System.TimeSpan total = perfSw.Elapsed;
            System.TimeSpan pass2b = total - perfPass1 - perfPass2a;
            Console.Error.WriteLine(
                $"[PERF] PublisherRegistry.Build: total {total.TotalSeconds:N2}s | " +
                $"pass1 typedef walk {perfPass1.TotalSeconds:N2}s ({100 * perfPass1.TotalSeconds / total.TotalSeconds:N1}%, {typesVisited:N0} typedefs) | " +
                $"pass2a index scan {perfPass2a.TotalSeconds:N2}s ({100 * perfPass2a.TotalSeconds / total.TotalSeconds:N1}%, {objectsVisited:N0} objects -> {liveMts.Count:N0} distinct MTs) | " +
                $"pass2b instance fields {pass2b.TotalSeconds:N2}s ({100 * pass2b.TotalSeconds / total.TotalSeconds:N1}%)");
        }

        return new PublisherRegistry(descriptorsByMt, eventNames, delegateOffsets, staticPublisherMTs);
    }

    /// <summary>Descriptors recognized for a MethodTable, or null if none of the registered shapes matched.</summary>
    public bool TryGetDescriptors(ulong methodTable, out EventFieldDescriptor[]? descriptors) =>
        _descriptorsByMt.TryGetValue(methodTable, out descriptors);
}
