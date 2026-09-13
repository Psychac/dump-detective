using Microsoft.Diagnostics.Runtime;

using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of <see cref="IDumpObjectFieldQuery"/> (<c>dump.object-fields</c>) — the
/// Tier-2 pilot capability, docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md. Lives
/// here rather than a <c>Sources.ClrDump</c> project because that project doesn't exist yet; written
/// to move unchanged once it does, matching every other Tier-1 dump-side implementation's interim
/// home.
/// </summary>
internal sealed class DumpObjectFieldQuery : IDumpObjectFieldQuery
{
    private readonly ClrHeap _heap;
    private readonly Dictionary<ulong, bool> _delegateFieldByMethodTable = new();

    public DumpObjectFieldQuery(ClrHeap heap) => _heap = heap;

    public bool TryReadField(ulong address, string fieldName, out FieldValue value)
    {
        value = FieldValue.Null;
        if (address == 0)
            return false;

        ClrObject obj = _heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            return false;

        ClrInstanceField? field = obj.Type.GetFieldByName(fieldName);
        if (field is null)
            return false;

        try
        {
            switch (field.ElementType)
            {
                case ClrElementType.Boolean:
                    value = FieldValue.ForBool(field.Read<bool>(address, interior: false));
                    return true;

                case ClrElementType.Int32:
                    value = FieldValue.ForInt32(field.Read<int>(address, interior: false));
                    return true;

                case ClrElementType.Int64:
                    value = FieldValue.ForInt64(field.Read<long>(address, interior: false));
                    return true;

                case ClrElementType.UInt32:
                    value = FieldValue.ForUInt32(field.Read<uint>(address, interior: false));
                    return true;

                case ClrElementType.NativeInt:
                    value = FieldValue.ForNativeInt(field.Read<nint>(address, interior: false));
                    return true;

                case ClrElementType.Double:
                    value = FieldValue.ForDouble(field.Read<double>(address, interior: false));
                    return true;

                case ClrElementType.String:
                {
                    // A string field is itself a heap-object reference in ClrMD — ClrElementType
                    // special-cases it so callers don't have to chase the reference by hand the way
                    // Class/Object fields below require. A null string field is real, readable data
                    // ("no value set"), not a read failure.
                    ClrObject strObj = field.ReadObject(address, interior: false);
                    value = strObj.IsValid && strObj.AsString() is string s ? FieldValue.ForString(s) : FieldValue.Null;
                    return true;
                }

                case ClrElementType.Class:
                case ClrElementType.Object:
                case ClrElementType.SZArray:
                case ClrElementType.Array:
                {
                    ClrObject nested = field.ReadObject(address, interior: false);
                    value = nested.IsValid ? FieldValue.ForObjectReference(nested.Address) : FieldValue.Null;
                    return true;
                }

                default:
                    // Struct/enum/other primitive fields: no Tier-2 consumer has needed one yet
                    // (hard-need-basis) — surface as unreadable rather than guess a representation.
                    return false;
            }
        }
        catch
        {
            // Corrupt/unloaded object — matches every pre-retyping Tier-2 read site's own
            // "field unreadable, not a crash" catch (e.g. FinalizableObjectAnalyzer's disposed-field
            // read, SqlConnectionPoolAnalyzer's pre-retyping TryReadPoolSnapshot).
            return false;
        }
    }

    public bool TryGetStringValue(ulong address, out string? value)
    {
        value = null;
        if (address == 0)
            return false;

        try
        {
            ClrObject obj = _heap.GetObject(address);
            if (!obj.IsValid)
                return false;

            value = obj.AsString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetStringValue(ulong address, int maxLength, out string? value)
    {
        value = null;
        if (address == 0)
            return false;

        try
        {
            ClrObject obj = _heap.GetObject(address);
            if (!obj.IsValid)
                return false;

            value = obj.AsString(maxLength);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetFieldTypeName(ulong address, string fieldName, out string? typeName)
    {
        typeName = null;
        if (address == 0)
            return false;

        ClrObject obj = _heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            return false;

        ClrInstanceField? field = obj.Type.GetFieldByName(fieldName);
        if (field is null)
            return false;

        typeName = field.Type?.Name;
        return true;
    }

    public IReadOnlyList<(string Name, ulong TargetAddress)> EnumerateObjectReferenceFields(ulong address)
    {
        if (address == 0)
            return [];

        ClrObject obj = _heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            return [];

        var result = new List<(string Name, ulong TargetAddress)>();
        foreach (ClrInstanceField field in obj.Type.Fields)
        {
            if (!field.IsObjectReference)
                continue;

            ulong targetAddress = 0;
            try
            {
                ClrObject target = field.ReadObject(address, interior: false);
                if (target.IsValid)
                    targetAddress = target.Address;
            }
            catch
            {
                // Corrupt/unloaded object — matches TryReadField's own catch above.
            }

            result.Add((field.Name ?? string.Empty, targetAddress));
        }

        return result;
    }

    public bool TryReadNestedStructField(ulong address, string outerFieldName, string innerFieldName, out FieldValue value)
    {
        value = FieldValue.Null;
        if (address == 0)
            return false;

        ClrObject obj = _heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            return false;

        ClrInstanceField? outerField = obj.Type.GetFieldByName(outerFieldName);
        if (outerField is null || !outerField.IsValueType || outerField.Type is null)
            return false;

        try
        {
            ClrValueType inner = obj.ReadValueTypeField(outerField);
            if (!inner.IsValid || inner.Type is null)
                return false;

            ClrInstanceField? innerField = inner.Type.GetFieldByName(innerFieldName);
            if (innerField is null)
                return false;

            // Same primitive shapes as TryReadField's own switch, read via ClrValueType's
            // interior-address form (ReadValueTypeField always yields an embedded/interior struct)
            // instead of the object-header form TryReadField uses.
            switch (innerField.ElementType)
            {
                case ClrElementType.Boolean:
                    value = FieldValue.ForBool(innerField.Read<bool>(inner.Address, interior: true));
                    return true;

                case ClrElementType.Int32:
                    value = FieldValue.ForInt32(innerField.Read<int>(inner.Address, interior: true));
                    return true;

                case ClrElementType.Int64:
                    value = FieldValue.ForInt64(innerField.Read<long>(inner.Address, interior: true));
                    return true;

                case ClrElementType.UInt32:
                    value = FieldValue.ForUInt32(innerField.Read<uint>(inner.Address, interior: true));
                    return true;

                case ClrElementType.NativeInt:
                    value = FieldValue.ForNativeInt(innerField.Read<nint>(inner.Address, interior: true));
                    return true;

                case ClrElementType.Double:
                    value = FieldValue.ForDouble(innerField.Read<double>(inner.Address, interior: true));
                    return true;

                default:
                    // No Tier-2 consumer has needed a nested string/object-reference/struct field
                    // yet (hard-need-basis) -- surface as unreadable rather than guess a representation.
                    return false;
            }
        }
        catch
        {
            // Corrupt/unloaded object -- matches TryReadField's own catch above.
            return false;
        }
    }

    public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address)
    {
        if (address == 0)
            return [];

        ClrObject obj = _heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            return [];

        var result = new List<InstanceFieldDescriptor>();
        foreach (ClrInstanceField field in obj.Type.Fields)
        {
            bool isArrayTyped = field.ElementType is ClrElementType.SZArray or ClrElementType.Array;
            FieldValueKind kind = field.ElementType switch
            {
                ClrElementType.Boolean => FieldValueKind.Bool,
                ClrElementType.Int32 => FieldValueKind.Int32,
                ClrElementType.Int64 => FieldValueKind.Int64,
                ClrElementType.UInt32 => FieldValueKind.UInt32,
                ClrElementType.NativeInt => FieldValueKind.NativeInt,
                ClrElementType.Double => FieldValueKind.Double,
                ClrElementType.String => FieldValueKind.String,
                ClrElementType.Class or ClrElementType.Object or ClrElementType.SZArray or ClrElementType.Array => FieldValueKind.ObjectReference,
                _ => FieldValueKind.Null,
            };

            result.Add(new InstanceFieldDescriptor(field.Name ?? string.Empty, kind, isArrayTyped));
        }

        return result;
    }

    /// <summary>
    /// Ported near-verbatim from the pre-retyping <c>StaticRootLeakDetector</c>'s own
    /// <c>HasDelegateFields</c> — checks both instance and static fields (a field-like event
    /// compiles to a static backing delegate field, not an instance one), cached per method table.
    /// </summary>
    public bool HasDelegateField(ulong address)
    {
        if (address == 0)
            return false;

        ClrObject obj = _heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            return false;

        ulong methodTable = obj.Type.MethodTable;
        if (methodTable != 0 && _delegateFieldByMethodTable.TryGetValue(methodTable, out bool cached))
            return cached;

        bool hasDelegateField = false;
        foreach (ClrInstanceField field in obj.Type.Fields)
        {
            if (TypeFilterHelper.IsDelegateType(field.Type))
            {
                hasDelegateField = true;
                break;
            }
        }

        if (!hasDelegateField)
        {
            foreach (ClrStaticField staticField in obj.Type.StaticFields)
            {
                if (TypeFilterHelper.IsDelegateType(staticField.Type))
                {
                    hasDelegateField = true;
                    break;
                }
            }
        }

        if (methodTable != 0)
            _delegateFieldByMethodTable[methodTable] = hasDelegateField;

        return hasDelegateField;
    }

    public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames)
    {
        if (exceptionAddress == 0)
            return [];

        try
        {
            ClrObject exceptionObj = _heap.GetObject(exceptionAddress);
            if (!exceptionObj.IsValid)
                return [];

            ClrException? clrException = exceptionObj.AsException();
            if (clrException is null)
                return [];

            var frames = new List<ThreadStackFrameRef>();
            foreach (ClrStackFrame frame in clrException.EnumerateExceptionStackTrace())
            {
                if (frames.Count >= maxFrames)
                    break;
                frames.Add(ThreadStackTranslator.ToFrameRef(frame));
            }
            return frames;
        }
        catch
        {
            // Corrupt/unloaded object -- matches TryReadField's own catch above.
            return [];
        }
    }
}
