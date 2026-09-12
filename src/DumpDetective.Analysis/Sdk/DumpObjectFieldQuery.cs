using Microsoft.Diagnostics.Runtime;

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
}
