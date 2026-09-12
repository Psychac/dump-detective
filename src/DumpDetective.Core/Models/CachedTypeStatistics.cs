namespace DumpDetective.Core.Models;

public sealed class CachedTypeStatistics
{
    public string TypeName { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public int Count { get; set; }
    public ulong TotalSize { get; set; }
    public int LohCount { get; set; }
    public ulong LohSize { get; set; }

    /// <summary>
    /// Added 2026-09-12 for <c>FinalizableObjectAnalyzer</c>'s retyping, so its no-index fallback
    /// (the same shared <see cref="StatisticsCache.GetOrBuildTypeStatistics"/> walk 5 other
    /// analyzers already use) can carry finalizable/per-generation facts too, instead of the SDK
    /// bridge's <c>HeapTypeStatisticsQuery.BuildFromCachedStatistics</c> hardcoding them to
    /// false/0. Only populated by <see cref="StatisticsCache"/>'s full-heap-walk branch (reached
    /// only when no prebuilt index exists); left at their defaults on the index-hydration branch,
    /// which no existing consumer of this type needs generation data from.
    /// </summary>
    public bool IsFinalizableType { get; set; }
    public long Gen0Count { get; set; }
    public long Gen1Count { get; set; }
    public long Gen2Count { get; set; }

    /// <summary>
    /// Added 2026-09-12 for <c>AsyncStateMachineAnalyzer</c>'s retyping, same reasoning as
    /// <see cref="IsFinalizableType"/>: only populated by <see cref="StatisticsCache"/>'s
    /// full-heap-walk branch, left <c>false</c> on the index-hydration branch (which sources this
    /// flag from <c>TypeAggregateFlags.IsAsyncStateMachineType</c> instead).
    /// </summary>
    public bool IsAsyncStateMachineType { get; set; }

    /// <summary>
    /// Added 2026-09-12 for <c>AsyncTaskAnalyzer</c>'s retyping, same reasoning as
    /// <see cref="IsAsyncStateMachineType"/>: only populated by <see cref="StatisticsCache"/>'s
    /// full-heap-walk branch, left <c>false</c> on the index-hydration branch (which sources this
    /// flag from <c>TypeAggregateFlags.IsTaskType</c> instead).
    /// </summary>
    public bool IsTaskType { get; set; }

    /// <summary>
    /// No <c>TypeAggregateFlags</c> bit exists for <c>TaskCompletionSource</c> (same gap the
    /// pre-retyping <c>AsyncTaskAnalyzer</c> already lived with) — always computed from the type
    /// name, so unlike <see cref="IsTaskType"/> this is populated on both the index-hydration and
    /// full-heap-walk branches.
    /// </summary>
    public bool IsTaskCompletionSourceType { get; set; }

    /// <summary>
    /// <c>true</c> when this type implements <c>IValueTaskSource</c>/<c>IValueTaskSource&lt;T&gt;</c>
    /// via an embedded <c>ManualResetValueTaskSourceCore&lt;TResult&gt;</c> field (see
    /// <c>ValueTaskSourcePattern</c>). Structural, not name-based, so — unlike
    /// <see cref="IsTaskType"/>/<see cref="IsTaskCompletionSourceType"/> — only ever computed once
    /// per distinct type (not per instance): a real <c>ClrType.EnumerateInterfaces()</c> walk, too
    /// expensive to repeat per object.
    /// </summary>
    public bool ImplementsValueTaskSource { get; set; }

    /// <summary>Name of the embedded <c>ManualResetValueTaskSourceCore&lt;TResult&gt;</c> field when
    /// <see cref="ImplementsValueTaskSource"/> is <c>true</c>; <c>null</c> otherwise.</summary>
    public string? ValueTaskSourceCoreFieldName { get; set; }
}
