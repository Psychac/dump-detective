using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Analyzers.EventLeak;

/// <summary>
/// The only publisher shape registered in Phase 3 — a delegate-typed instance or static field
/// that behaves like a standard C# event backing field. Replicates today's detection behavior
/// exactly (no detection-surface change, design §3): same field filters as the former
/// <c>EventLeakFastScanner.BuildFieldLayouts</c>. Instance and static fields are described
/// separately so <see cref="PublisherRegistry.Build"/> can scope the (expensive) instance-field
/// walk to live-instance MethodTables only while still covering every module type for static
/// fields, matching the scope of each original walk it replaces.
/// </summary>
internal sealed class FieldBackedDelegateShape : IPublisherShape
{
    public const byte Id = 1;
    public byte ShapeId => Id;

    private readonly EventNameResolver _eventNames;
    private readonly ClrHeap _heap;
    private readonly IMemoryReader _reader;
    private readonly int _ptrSize;
    private readonly int _targetOffset;
    private readonly int _invListOffset;

    public FieldBackedDelegateShape(ClrHeap heap, EventNameResolver eventNames)
    {
        _heap = heap;
        _eventNames = eventNames;

        IDataReader dr = heap.Runtime.DataTarget.DataReader;
        _reader = (IMemoryReader)dr;
        _ptrSize = dr.PointerSize;

        (_targetOffset, _invListOffset, _) = DelegateLayoutDiscovery.Discover(heap);
    }

    public IEnumerable<EventFieldDescriptor> DescribeInstanceFields(ClrType type)
    {
        HashSet<string>? eventNames = null;

        foreach (ClrInstanceField field in type.Fields)
        {
            if (!field.IsObjectReference) continue;
            if (!TypeFilterHelper.IsDelegateType(field.Type)) continue;
            if (TypeFilterHelper.IsCompilerGenerated(field.Name)) continue;
            if (string.IsNullOrEmpty(field.Name)) continue;

            eventNames ??= _eventNames.GetEventNames(type);

            if (eventNames.Count > 0)
            {
                if (!eventNames.Contains(field.Name) && !EventLeakAnalyzer.LooksLikeEventFieldName(field.Name))
                    continue;
            }
            else if (!EventLeakAnalyzer.LooksLikeEventFieldName(field.Name))
                continue;

            // Absolute offset (including the MT header), same interior-offset correction as
            // the former BuildFieldLayouts: ClrInstanceField.Offset is interior-relative.
            yield return new EventFieldDescriptor(
                offset: field.Offset + _ptrSize,
                fieldName: field.Name,
                publisherTypeName: type.Name ?? StringConstants.UnknownType,
                isStatic: false,
                shapeId: Id);
        }
    }

    public IEnumerable<EventFieldDescriptor> DescribeStaticFields(ClrType type)
    {
        HashSet<string>? eventNames = null;

        foreach (ClrStaticField sField in type.StaticFields)
        {
            if (!TypeFilterHelper.IsDelegateType(sField.Type)) continue;
            if (TypeFilterHelper.IsCompilerGenerated(sField.Name)) continue;
            if (string.IsNullOrEmpty(sField.Name)) continue;

            eventNames ??= _eventNames.GetEventNames(type);

            if (eventNames.Count > 0)
            {
                if (!eventNames.Contains(sField.Name!) && !EventLeakAnalyzer.LooksLikeEventFieldName(sField.Name))
                    continue;
            }
            else if (!EventLeakAnalyzer.LooksLikeEventFieldName(sField.Name))
                continue;

            // Offset is meaningless for static descriptors — read via ClrStaticField instead.
            yield return new EventFieldDescriptor(
                offset: 0,
                fieldName: sField.Name!,
                publisherTypeName: type.Name ?? StringConstants.UnknownType,
                isStatic: true,
                shapeId: Id);
        }
    }

    public void Extract(ulong objAddress, in EventFieldDescriptor descriptor,
        List<(ulong Address, ulong MethodTable, ulong DelegateAddress)> subscribersOut)
    {
        ulong delegateAddr = objAddress;
        if (!descriptor.IsStatic)
        {
            if (!_reader.ReadPointer(objAddress + (ulong)descriptor.Offset, out delegateAddr) || delegateAddr == 0)
                return;
        }
        if (delegateAddr == 0) return;

        DelegateChainWalker.ExtractSubscribers(_heap, _reader, delegateAddr, _targetOffset, _invListOffset, subscribersOut);
    }
}
