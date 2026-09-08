using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers.EventLeak;

/// <summary>
/// One publisher-detection pattern (design §3.2). <see cref="FieldBackedDelegateShape"/> is
/// the only shape registered in Phase 3 — the seam exists so <c>EventHandlerListShape</c> and
/// <c>WeakEventShape</c> (Phase 7) can be added without a new independent type walk.
/// </summary>
internal interface IPublisherShape
{
    byte ShapeId { get; }

    /// <summary>
    /// Called only for MethodTables with at least one live heap instance (lever 3, design
    /// §3.3) during <see cref="PublisherRegistry.Build"/> — instance-field enumeration touches
    /// every field's <c>ClrType</c> to check for a delegate base type, which is the expensive
    /// part of a type visit, so it must not run over every type loaded by every module (that
    /// regressed build cost to ~48s on the 3.3GB reference dump — see the phase-3 revert note).
    /// Metadata-only — no heap/pointer access.
    /// </summary>
    IEnumerable<EventFieldDescriptor> DescribeInstanceFields(ClrType type);

    /// <summary>
    /// Called once per type reachable from loaded modules (matches the scope of the former
    /// <c>SweepModuleStaticFields</c> module walk) — static-only publishers can exist with zero
    /// live instances, so this pass cannot be restricted to live-instance MethodTables the way
    /// <see cref="DescribeInstanceFields"/> is. Metadata-only — no heap/pointer access.
    /// </summary>
    IEnumerable<EventFieldDescriptor> DescribeStaticFields(ClrType type);

    /// <summary>
    /// Reference extraction for a matched descriptor. NOT called on
    /// <c>EventLeakFastScanner</c>'s hot path (design §5) — the hot path dispatches through a
    /// fixed switch on <see cref="EventFieldDescriptor.ShapeId"/> instead, to avoid a virtual
    /// call per object. This method exists for shape-level correctness tests.
    /// </summary>
    void Extract(ulong objAddress, in EventFieldDescriptor descriptor,
        List<(ulong Address, ulong MethodTable, ulong DelegateAddress)> subscribersOut);
}
