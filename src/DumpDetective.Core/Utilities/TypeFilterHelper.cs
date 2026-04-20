using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Utilities;
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

    public static bool IsDelegateType(ClrType? type) =>
        type?.BaseType?.Name == "System.MulticastDelegate";

    public static bool IsCompilerGenerated(string? name) =>
        name != null && name.Contains("<>", StringComparison.Ordinal);

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
