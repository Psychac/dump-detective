using Microsoft.Diagnostics.Runtime;

using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Walks <c>ClrType.StaticFields</c> across every AppDomain/module to build a
/// <c>RootAddr → (OwnerType, FieldName, AppDomainId)</c> map, keyed by each field's own storage
/// address (<see cref="ClrStaticField.GetAddress(ClrAppDomain)"/>) rather than the value it
/// holds — see <see cref="Cache.RootSetCache.GetStaticFieldsByRootAddress"/> for why the target
/// address is the wrong key (ambiguous when multiple fields reference the same object).
/// Shared between <see cref="Cache.RootSetCache"/>'s live fallback and
/// <see cref="Indexing.Satellite.RootIndexWriter"/>'s Phase-1 disk persistence so both compute
/// the identical map instead of drifting.
/// </summary>
internal static class StaticFieldResolver
{
    /// <summary>
    /// <paramref name="relevantRootAddresses"/>, when supplied, bounds the map to fields whose
    /// storage address matches a currently-enumerated static/thread-static root — skipping the
    /// (typically much larger) set of static fields that aren't presently rooted. Pass
    /// <c>null</c> only when the caller has no root list to filter against.
    /// </summary>
    public static Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)> BuildMapByRootAddress(
        ClrHeap heap,
        IReadOnlySet<ulong>? relevantRootAddresses = null)
    {
        var map = new Dictionary<ulong, (string, string, int)>(capacity: relevantRootAddresses?.Count is > 0 and int n ? n : 16384);

        foreach (ClrAppDomain domain in heap.Runtime.AppDomains)
        {
            int domainId = domain.Id;

            foreach (ClrModule module in domain.Modules)
            {
                foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
                {
                    if (mt == 0)
                        continue;

                    ClrType? type = heap.GetTypeByMethodTable(mt);
                    if (type is null || TypeFilterHelper.IsSystemType(type.Name) || TypeFilterHelper.IsCompilerGenerated(type.Name))
                        continue;

                    foreach (ClrStaticField field in type.StaticFields)
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(field.Name))
                                continue;

                            ulong fieldAddress = field.GetAddress(domain);
                            if (fieldAddress == 0)
                                continue;

                            if (relevantRootAddresses is not null && !relevantRootAddresses.Contains(fieldAddress))
                                continue;

                            map[fieldAddress] = (type.Name, field.Name, domainId);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        return map;
    }
}
