namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Single source of truth for classifying a type name as a genuine <c>System.Threading.Tasks.Task</c>
/// (or subtype, e.g. <c>Task&lt;T&gt;</c>, internal promise types) versus a type that merely shares the
/// <c>System.Threading.Tasks.Task</c> name prefix but is not a task — namely
/// <c>TaskCompletionSource</c> / <c>TaskCompletionSource&lt;T&gt;</c>, which wrap an inner <c>Task</c>
/// via an <c>m_task</c> field rather than being one.
///
/// A prior implementation matched on <c>StartsWith("System.Threading.Tasks.Task")</c> alone, which
/// also matches <c>"System.Threading.Tasks.TaskCompletionSource`1[[...]]"</c>. That silently
/// misclassified every heap-resident <c>TaskCompletionSource&lt;T&gt;</c> as a real task: its type has
/// no <c>m_stateFlags</c> field, so the field-lookup fails, <c>stateFlags</c> stays 0, and
/// <c>AsyncTaskAnalyzer</c> counted it as Pending.
/// </summary>
internal static class TaskTypeNamePattern
{
    private const string TaskPrefix = "System.Threading.Tasks.Task";
    private const string TaskCompletionSourcePrefix = "System.Threading.Tasks.TaskCompletionSource";

    public static bool IsTaskType(string typeName)
    {
        return typeName.StartsWith(TaskPrefix, StringComparison.Ordinal)
            && !typeName.StartsWith(TaskCompletionSourcePrefix, StringComparison.Ordinal);
    }

    public static bool IsTaskCompletionSource(string typeName)
    {
        return typeName.StartsWith(TaskCompletionSourcePrefix, StringComparison.Ordinal);
    }
}
