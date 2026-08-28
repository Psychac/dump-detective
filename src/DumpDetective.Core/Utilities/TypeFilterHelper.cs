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

    /// <summary>
    /// Extracts the namespace portion of a ClrMD type name for grouping purposes. Truncates at
    /// the first generic-argument (<c>&lt;</c>/backtick) or array (<c>[</c>) marker first, so a
    /// dot inside a generic argument (e.g. <c>System.Collections.Generic.List&lt;MyApp.Foo&gt;</c>)
    /// is never mistaken for a namespace separator of the outer type.
    /// </summary>
    public static string GetNamespace(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return "(no namespace)";

        int cut = typeName.Length;
        int lt = typeName.IndexOf('<');
        if (lt >= 0 && lt < cut) cut = lt;
        int backtick = typeName.IndexOf('`');
        if (backtick >= 0 && backtick < cut) cut = backtick;
        int bracket = typeName.IndexOf('[');
        if (bracket >= 0 && bracket < cut) cut = bracket;

        string head = typeName.Substring(0, cut);
        int lastDot = head.LastIndexOf('.');
        return lastDot > 0 ? head.Substring(0, lastDot) : "(no namespace)";
    }
}
