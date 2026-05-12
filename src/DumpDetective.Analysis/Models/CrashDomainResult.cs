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
    int InferredTraceCount = 0) : AnalyzerDomainResult;

internal sealed record CrashThreadCandidateSnapshot(
    uint ThreadId,
    uint OSThreadId,
    int ActiveExceptionCount,
    string PrimaryExceptionType,
    IReadOnlyList<string> TopFrames,
    IReadOnlyList<string>? OriginalStackTrace,
    bool OriginalStackTraceInferred,
    string? OriginalStackTraceInferredFrom,
    InferenceConfidence OriginalStackTraceConfidence = InferenceConfidence.None);

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
    IReadOnlyList<string>? OriginalStackTrace);
