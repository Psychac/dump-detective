using Microsoft.Diagnostics.Runtime;
using System.Threading;

namespace DumpDetective.Analysis.Utilities
{
    internal static class DelegateHelper
    {
        // Cache for field lookups keyed by MethodTable to avoid repeated string allocations
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Mt, string Field), ClrInstanceField?> _fieldCache = new();

        public static ClrInstanceField? GetCachedField(ClrType? type, string fieldName)
        {
            if (type == null)
                return null;

            ulong mt = type.MethodTable;
            var key = (Mt: mt, Field: fieldName);

            return _fieldCache.GetOrAdd(key, k => type.GetFieldByName(k.Field));
        }

        public static void ClearCache()
        {
            _fieldCache.Clear();
        }
    }
}


