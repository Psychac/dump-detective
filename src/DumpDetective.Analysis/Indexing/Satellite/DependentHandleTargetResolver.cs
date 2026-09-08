using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// Resolves a Dependent handle's secondary (dependent) target address via reflection —
/// ClrMD 4 doesn't expose a <c>DependentTarget</c> property directly (P0-1,
/// docs/analysis/phase1/gchandle-analyzer-audit.md). Shared between
/// <see cref="HandleSnapshotWriter"/>/<see cref="MemoryHandleSnapshotReader"/> (capture at
/// enumeration time, P3-3) and any live-enumeration fallback.
/// </summary>
internal static class DependentHandleTargetResolver
{
    public static bool TryGetDependentTargetAddress(ClrHandle handle, out ulong targetAddress)
    {
        targetAddress = 0;

        try
        {
            string[] propertyCandidates =
            [
                "DependentTarget",
                "Target",
                "Secondary",
                "DependentObject",
                "Dependent"
            ];

            Type handleType = handle.GetType();
            foreach (string propertyName in propertyCandidates)
            {
                System.Reflection.PropertyInfo? property = handleType.GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (property == null)
                    continue;

                object? value = property.GetValue(handle);
                if (value == null)
                    continue;

                if (TryGetTargetAddress(value, out targetAddress))
                    return true;
            }
        }
        catch (System.Reflection.TargetInvocationException)
        {
        }
        catch (System.Reflection.AmbiguousMatchException)
        {
        }

        return false;
    }

    public static bool TryGetTargetAddress(object value, out ulong address)
    {
        address = 0;

        if (value is ClrObject clrObject)
        {
            if (!clrObject.IsValid)
                return false;

            address = clrObject.Address;
            return true;
        }

        if (value is ulong targetAddress && targetAddress != 0)
        {
            address = targetAddress;
            return true;
        }

        return false;
    }
}
