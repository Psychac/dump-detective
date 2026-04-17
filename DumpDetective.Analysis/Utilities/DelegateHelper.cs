using Microsoft.Diagnostics.Runtime;
using System.Threading;

namespace DumpDetective.Analysis.Utilities
{
    internal static class DelegateHelper
    {
        // Cache for field lookups to avoid repeated GetFieldByName calls
        private static readonly Dictionary<string, ClrInstanceField?> _fieldCache = new();
        private static readonly Lock _cacheLock = new();

        public static ClrInstanceField? GetCachedField(ClrType? type, string fieldName)
        {
            if (type == null || type.Name == null)
                return null;

            string cacheKey = $"{type.Name}::{fieldName}";

            lock (_cacheLock)
            {
                if (_fieldCache.TryGetValue(cacheKey, out var cachedField))
                    return cachedField;

                var field = type.GetFieldByName(fieldName);
                _fieldCache[cacheKey] = field;
                return field;
            }
        }

        public static void ClearCache()
        {
            lock (_cacheLock)
            {
                _fieldCache.Clear();
            }
        }
    }
}


