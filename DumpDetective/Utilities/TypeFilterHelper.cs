namespace DumpDetective.Utilities
{
    internal static class TypeFilterHelper
    {
        private static readonly string[] SystemNamespaces =
        {
            "System.",
            "Microsoft.",
            "MS.",
            "Internal.",
            "Windows.",
            "Interop.",
            "FxResources.",
            "System_Private_CoreLib"
        };

        public static bool IsSystemType(string? typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return false;

            // Use for loop for better performance
            foreach (var ns in SystemNamespaces)
            {
                if (typeName.StartsWith(ns, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public static bool IsEventField(string? typeName)
        {
            if (typeName == null)
                return false;

            // Use Ordinal comparison for better performance
            return typeName.Contains("EventHandler", StringComparison.Ordinal) ||
                   typeName.Contains("Action", StringComparison.Ordinal) ||
                   typeName.Contains("Func", StringComparison.Ordinal) ||
                   typeName.Contains("Delegate", StringComparison.Ordinal);
        }

        public static bool IsCollectionType(string? typeName)
        {
            if (typeName == null)
                return false;

            return typeName.Contains("Dictionary", StringComparison.Ordinal) ||
                   typeName.Contains("List", StringComparison.Ordinal) ||
                   typeName.Contains("Collection", StringComparison.Ordinal) ||
                   typeName.Contains("HashSet", StringComparison.Ordinal) ||
                   typeName.Contains("Queue", StringComparison.Ordinal) ||
                   typeName.Contains("Stack", StringComparison.Ordinal);
        }
    }
}
