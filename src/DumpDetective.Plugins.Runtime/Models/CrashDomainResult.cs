using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Crash

/// <summary>Confidence level of an inferred original stack trace.</summary>
internal enum InferenceConfidence
{
    /// <summary>Exact match: candidate had its own OriginalExceptionStack.</summary>
    Exact,
    /// <summary>Matched by ThreadId to a heap exception instance.</summary>
    ThreadId,
    /// <summary>Matched by Message + HResult heuristic.</summary>
    MessageHResult,
    /// <summary>Matched by Type + InnerExceptionType as last-resort heuristic.</summary>
    TypeInnerType,
    /// <summary>No original trace could be found.</summary>
    None,
}

internal sealed record CrashDomainResult(
    int TotalExceptions,
    int ActiveExceptions,
    IReadOnlyDictionary<string, int> ExceptionTypeCounts,
    IReadOnlyDictionary<string, int> ActiveExceptionTypeCounts,
    IReadOnlyList<CrashThreadCandidateSnapshot>? TopCrashThreadCandidates = null,
    IReadOnlyList<ExceptionInstanceSnapshot>? TopExceptionInstances = null,
    int InferredTraceCount = 0,
    int AggregateExceptionCount = 0,
    IReadOnlyDictionary<string, int>? AggregateInnerExceptionTypeCounts = null,
    IReadOnlyList<ExceptionMessageDistribution>? MessageDistributions = null,
    IReadOnlyList<CrashBucket>? CrashBuckets = null,
    IReadOnlyDictionary<string, ulong>? ExceptionHeapSizeByType = null,
    IReadOnlyList<ExceptionRetentionPath>? Gen2RetentionPaths = null) : AnalyzerDomainResult;

internal sealed record CrashThreadCandidateSnapshot(
    uint ThreadId,
    uint OSThreadId,
    int ActiveExceptionCount,
    string PrimaryExceptionType,
    IReadOnlyList<string> TopFrames,
    IReadOnlyList<string>? OriginalStackTrace,
    bool OriginalStackTraceInferred,
    string? OriginalStackTraceInferredFrom,
    InferenceConfidence OriginalStackTraceConfidence = InferenceConfidence.None,
    bool OriginalStackTraceIsRethrown = false,
    // Owning assembly of TopFrames' first user-code frame, resolved directly via
    // ClrStackFrame.Method.Type.Module — not a ModuleDomainResult cross-reference. Null when the
    // whole captured stack is framework code, or the frame's module couldn't be resolved.
    string? TopUserFrameModule = null);

internal sealed record ExceptionInstanceSnapshot(
    string Type,
    ulong Address,
    string? Message,
    int? HResult,
    string? InnerExceptionType,
    int ChainDepth,
    bool IsActive,
    uint? ThreadId,
    uint? OSThreadId,
    IReadOnlyList<string>? CurrentThreadFrames,
    IReadOnlyList<string>? OriginalStackTrace,
    IReadOnlyList<string>? AggregateInnerExceptionTypes = null,
    bool IsRethrown = false);

/// <summary>
/// Per-type message distribution, derived from the (per-type, MaxExceptionsPerType-capped, plus
/// always-included active) sampled instance set — the same set backing the "Exception instances"
/// table, not a fresh unconditional heap-wide scan of every instance's Message field.
/// </summary>
internal sealed record ExceptionMessageDistribution(
    string Type,
    int SampledInstanceCount,
    int DistinctMessageCount,
    string? MostCommonMessage,
    int MostCommonMessageCount,
    string? MostCommonActiveMessage,
    int MostCommonActiveMessageCount);

/// <summary>
/// Crash bucket / fault signature: a (ExceptionType, TopUserFrame) dedup key over the sampled
/// instance set (same scope as <see cref="ExceptionMessageDistribution"/>). Groups exceptions by
/// their real originating call site rather than just their .NET type, so a systemic single-site
/// fault (one bucket, high InstanceCount) is distinguishable from scattered independent failures
/// (many buckets, low InstanceCount each) even when both share the same exception type.
/// </summary>
internal sealed record CrashBucket(
    string ExceptionType,
    string TopUserFrame,
    int InstanceCount,
    int ActiveInstanceCount,
    ulong SampleAddress);

/// <summary>
/// Retention path (E-1) for a Gen2/LOH exception instance — a formatted GC-root-to-object path
/// found via the shared <c>RootPathFinder</c>/reverse-edge-index infrastructure, the same one
/// EventLeakAnalyzer/DominatorAnalyzer/TimerLeakAnalyzer already use. Answers "why is this
/// exception object still alive" (static field, event handler, cache entry, etc.).
/// </summary>
internal sealed record ExceptionRetentionPath(
    string ExceptionType,
    ulong Address,
    string RootKind,
    string FormattedPath,
    bool SearchTruncated);
