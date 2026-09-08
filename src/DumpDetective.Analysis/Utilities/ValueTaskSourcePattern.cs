using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Detects <c>IValueTaskSource</c>/<c>IValueTaskSource&lt;T&gt;</c> implementers built on the
/// common BCL helper struct <c>System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore&lt;TResult&gt;</c>.
///
/// <c>ValueTask&lt;T&gt;</c> itself is a struct, never independently heap-allocated — it wraps
/// either a raw synchronous result (no heap object), a <c>Task&lt;T&gt;</c> (already covered by
/// <see cref="TaskTypeNamePattern"/>), or an <c>IValueTaskSource&lt;T&gt;</c> implementation —
/// the only case invisible to existing Task-based scanning.
///
/// <c>ManualResetValueTaskSourceCore&lt;TResult&gt;</c> does not itself implement
/// <c>IValueTaskSource&lt;T&gt;</c> — it's composition, not inheritance: a containing class
/// implements the interface and holds this struct as a field (under an arbitrary field name),
/// delegating to it. Detecting an implementer therefore requires two lookups per type: (1) does
/// it implement the interface, and (2) does it have a field whose *type* is this helper struct
/// (the field name itself is not stable, only its type name is).
///
/// Inherent, undetectable gap: fully custom <c>IValueTaskSource</c> implementations that roll
/// their own completion state without this helper struct are invisible — there's no stable
/// signal to key on for those. This covers the dominant BCL/community pattern (Socket internals,
/// System.IO.Pipelines, many ASP.NET Core primitives), not every implementer.
/// </summary>
internal static class ValueTaskSourcePattern
{
    private const string InterfacePrefix = "System.Threading.Tasks.Sources.IValueTaskSource";
    private const string CoreFieldTypePrefix = "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore";

    public static bool ImplementsValueTaskSource(ClrType type)
    {
        foreach (ClrInterface iface in type.EnumerateInterfaces())
        {
            if (iface.Name != null && iface.Name.StartsWith(InterfacePrefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Finds the embedded <c>ManualResetValueTaskSourceCore&lt;TResult&gt;</c> field on an
    /// <c>IValueTaskSource</c> implementer, if one exists. Returns null if the type doesn't use
    /// this helper struct (a custom implementation, or not an IValueTaskSource at all).
    /// </summary>
    public static ClrInstanceField? FindCoreField(ClrType type)
    {
        foreach (ClrInstanceField field in type.Fields)
        {
            string? fieldTypeName = field.Type?.Name;
            if (fieldTypeName != null && fieldTypeName.StartsWith(CoreFieldTypePrefix, StringComparison.Ordinal))
                return field;
        }
        return null;
    }
}
