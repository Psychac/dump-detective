using System.Text.RegularExpressions;

namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Single source of truth for the compiler-generated async state machine type-name pattern
/// (<c>&lt;MethodName&gt;d__N</c>, optionally followed by CLR-appended generic type parameters,
/// e.g. <c>&lt;GetAsync&gt;d__5[[System.String, mscorlib]]</c>).
///
/// Shared between the Phase 1 <c>DiskBackedObjectIndexWriter.IsAsyncStateMachineType</c> flag
/// computation (gates <c>TypeAggregateFlags.IsAsyncStateMachineType</c>) and the Phase 2
/// <c>AsyncStateMachineAnalyzer</c> candidate parsing. These previously drifted out of sync —
/// the Phase 1 copy lacked generic-parameter tolerance, so generic async methods never got the
/// flag set and were filtered out before Phase 2's (correctly generic-aware) regex ever saw them,
/// silently defeating that fix end-to-end. This is the only place the pattern should be defined.
/// </summary>
internal static class AsyncStateMachineNamePattern
{
    public static readonly Regex Regex =
        new(@"<(.+?)>d__\d+(?:\[\[.+?\]\])?$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
}
