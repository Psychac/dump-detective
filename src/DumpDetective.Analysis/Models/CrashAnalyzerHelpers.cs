using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    // Internal helper types used by CrashAnalyzer and unit tests.
    internal class ExceptionAnalysis
    {
        public int TotalExceptions { get; set; }
        public int ActiveExceptions { get; set; }
        public Dictionary<string, int> ExceptionTypeCounts { get; set; } = new();
        public Dictionary<string, int> ActiveExceptionTypeCounts { get; set; } = new();
        public Dictionary<string, List<ExceptionInstance>> ExceptionsByType { get; set; } = new();
        public List<CrashThreadCandidate> CrashThreadCandidates { get; set; } = new();
        // GC generation distribution per exception type
        public Dictionary<string, int> ExceptionGen0Counts { get; set; } = new();
        public Dictionary<string, int> ExceptionGen1Counts { get; set; } = new();
        public Dictionary<string, int> ExceptionGen2Counts { get; set; } = new();
        public Dictionary<string, int> ExceptionLohCounts { get; set; } = new();
        // Set by BuildCrashThreadSnapshots after inference pass
        public int InferredTraceCount { get; set; }
        // AggregateException unwrapping — computed unconditionally per AggregateException
        // instance encountered, independent of MaxExceptionsPerType (same rationale as the
        // Gen0/Gen1/Gen2/Loh counts above: totals must be exact, never sampled).
        public int AggregateExceptionCount { get; set; }
        public Dictionary<string, int> AggregateInnerExceptionTypeCounts { get; set; } = new();
        // Total heap bytes occupied by exception objects per type — HeapEntry.Size (or
        // ClrObject.Size on the no-index fallback) summed unconditionally, zero marginal scan
        // cost (the value is already read for every entry regardless of this analyzer).
        public Dictionary<string, ulong> ExceptionHeapSizeByType { get; set; } = new();
    }

    internal class CrashThreadCandidate
    {
        public uint ThreadId { get; set; }
        public uint OSThreadId { get; set; }
        public int ActiveExceptionCount { get; set; }
        public string PrimaryExceptionType { get; set; } = string.Empty;
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
        public List<string> OriginalExceptionStack { get; set; } = new();
        // Whether the instance that supplied OriginalExceptionStack had a non-null
        // _remoteStackTraceString — its top frames are the rethrow site, not the original throw.
        public bool OriginalExceptionStackIsRethrown { get; set; }
        // Representative metadata from the active exception (for inference heuristics)
        public string SampleMessage { get; set; } = string.Empty;
        public int SampleHResult { get; set; }
        public string? SampleInnerExceptionType { get; set; }
    }

    internal class ActiveExceptionContext
    {
        public uint ThreadId { get; set; }
        public uint OSThreadId { get; set; }
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
    }

    internal class ExceptionInstance
    {
        public ulong Address { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int HResult { get; set; }
        public string? InnerExceptionType { get; set; }
        public int ChainDepth { get; set; } = 1;
        public uint? ThreadId { get; set; }
        public uint? OSThreadId { get; set; }
        public List<string> OriginalStackTrace { get; set; } = new();
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
        // Populated only for AggregateException instances; the types of its InnerExceptions
        // (capped at MaxDisplayedInnerExceptionTypes — display-only, the global
        // AggregateInnerExceptionTypeCounts tally on ExceptionAnalysis is always exact).
        public List<string>? AggregateInnerExceptionTypes { get; set; }
        // True when _remoteStackTraceString is non-null — the exception was rethrown via
        // `throw;` or ExceptionDispatchInfo.Throw(), so OriginalStackTrace's top frames are the
        // rethrow site rather than the original throw site.
        public bool IsRethrown { get; set; }
        // GC generation at capture time (0/1/2; >2 covers Large/Pinned/Frozen/Unknown — the same
        // "LOH" bucket ExceptionLohCounts already uses). Drives Gen2/LOH retention-path candidate
        // selection (E-1) — set for free from the value OnHeapEntry/ProcessEntry already compute
        // for ExceptionGen0/1/2/LohCounts, no extra heap read.
        public int Generation { get; set; }
    }

    // Mutable per-(ExceptionType, TopUserFrame) running total while building crash buckets.
    internal sealed class CrashBucketAccumulator
    {
        public CrashBucketAccumulator(ulong sampleAddress) => SampleAddress = sampleAddress;

        public int InstanceCount { get; set; }
        public int ActiveInstanceCount { get; set; }
        public ulong SampleAddress { get; }
    }
}
